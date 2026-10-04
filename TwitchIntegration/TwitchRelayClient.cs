using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BGOverlay
{
    public enum TwitchRelayStatus
    {
        Disabled,
        Connecting,
        Connected,
        Error
    }

    /// <summary>
    /// Push-only client for the (self-hosted) relay server under TwitchIntegration/Relay/ - phase 0 of the
    /// Twitch integration. Maintains a background WebSocket connection to
    /// Configuration.TwitchRelayUrl and periodically sends a JSON snapshot of the current party,
    /// so a relay-hosted backend can show it to viewers later (phase 1) or trigger effects back
    /// (phase 2). Never reads viewer input or writes anything into game memory itself - this is
    /// strictly an outbound telemetry pipe.
    /// </summary>
    public sealed class TwitchRelayClient
    {
        public static TwitchRelayClient Instance { get; } = new TwitchRelayClient();

        private volatile TwitchRelayStatus status = TwitchRelayStatus.Disabled;
        public TwitchRelayStatus Status => status;
        public string LastError { get; private set; }

        private static readonly TimeSpan SendInterval = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);

        private readonly object gate = new object();
        private readonly SemaphoreSlim sendSignal = new SemaphoreSlim(0);
        private CancellationTokenSource cts;
        private string activeUrl;
        private string activeKey;
        private string activeControlKey;
        private string activeBroadcasterLogin;
        private DateTime lastSendUtc = DateTime.MinValue;
        private volatile string pendingPayload;

        private TwitchRelayClient() { }

        /// <summary>
        /// Called every ProcessHacker.MainLoop() tick with the current settings (same pattern as
        /// cacheInvalidationRequested - a single-threaded owner re-checking desired state each
        /// tick, instead of pushing change events in from the UI thread). No-op unless
        /// enabled/url/key actually changed since the last call.
        /// </summary>
        public void UpdateConfig(bool enabled, string relayUrl, string streamKey, string controlKey, string broadcasterLogin)
        {
            lock (gate)
            {
                if (!enabled || string.IsNullOrWhiteSpace(relayUrl) || string.IsNullOrWhiteSpace(streamKey))
                {
                    stopLocked();
                    status = TwitchRelayStatus.Disabled;
                    return;
                }

                if (cts != null && activeUrl == relayUrl && activeKey == streamKey && activeControlKey == controlKey && activeBroadcasterLogin == broadcasterLogin)
                    return;

                stopLocked();
                activeUrl = relayUrl;
                activeKey = streamKey;
                activeControlKey = controlKey;
                activeBroadcasterLogin = broadcasterLogin;
                cts = new CancellationTokenSource();
                status = TwitchRelayStatus.Connecting;
                var token = cts.Token;
                Task.Factory.StartNew(() => runAsync(relayUrl, streamKey, controlKey, broadcasterLogin, token),
                    token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }
        }

        /// <summary>
        /// Drops any live session so the next UpdateConfig builds a new one even though nothing
        /// in the configuration changed. UpdateConfig deliberately no-ops when the settings it is
        /// handed match the running connection, which is right for being called every frame but
        /// leaves the Connect button with nothing to do - this is what gives it an effect, and
        /// what lets it cut short the reconnect backoff after a failure.
        /// </summary>
        public void Reconnect()
        {
            lock (gate)
            {
                stopLocked();
            }
        }

        private void stopLocked()
        {
            cts?.Cancel();
            cts = null;
            activeUrl = null;
            activeKey = null;
            activeControlKey = null;
            activeBroadcasterLogin = null;
        }

        /// <summary>
        /// Builds and queues a party snapshot for sending - internally throttled to
        /// SendInterval so callers (ProcessHacker.MainLoop, ticking every RefreshTimeMS) don't
        /// need to know or care about the actual send cadence. No-op while not connected.
        /// </summary>
        public void PushPartySnapshot(IReadOnlyList<BGEntity> party)
        {
            if (status != TwitchRelayStatus.Connected)
                return;
            if (DateTime.UtcNow - lastSendUtc < SendInterval)
                return;

            lastSendUtc = DateTime.UtcNow;
            // Kept for the random-summon path, which has to pick a party member and name them
            // in the streamer's message. The command arrives on the socket's own thread with no
            // party to hand, and reading game memory from there is what the rest of this file
            // is built to avoid.
            var names = new string[party.Count];
            for (int i = 0; i < party.Count; i++)
                names[i] = party[i].Name2 ?? "";
            lastPartyNames = names;

            pendingPayload = buildPayload(party, CurrentPacks());
            sendSignal.Release();
        }

        // Written by the loop thread on every snapshot, read by the socket thread when a summon
        // arrives. Replaced wholesale rather than mutated, so a reader always sees one whole
        // party rather than half of two.
        private volatile string[] lastPartyNames = new string[0];

        private string cachedPackSource;
        private List<SpawnPack> cachedPacks = new List<SpawnPack>();

        // Whether the cached list was filtered with a creature index to hand. The packs are first
        // asked for before the game is hooked, when nothing can be checked and everything is
        // kept; without this the "has the config changed" test would hold that unfiltered list
        // for the rest of the session, since the config string has not changed at all.
        private bool cachedWithCreatureIndex;

        /// <summary>
        /// The streamer's packs as configured right now. Cached on the raw config string so the
        /// snapshot, which is rebuilt every couple of seconds, doesn't re-parse it every time,
        /// while an edit in the options tab still shows up on viewers' tiles immediately.
        /// </summary>
        public List<SpawnPack> CurrentPacks()
        {
            var source = Configuration.SpawnPacks ?? "";
            var haveCreatureIndex = ResourceManager.Instance != null;

            if (!string.Equals(source, cachedPackSource, StringComparison.Ordinal)
                || (haveCreatureIndex && !cachedWithCreatureIndex))
            {
                var parsed = SpawnPack.Deserialize(source);

                // Filtered here, at the one place every consumer reads packs from, so the tiles a
                // viewer is offered, the ids a summon names and the pool a random summon draws
                // from can never disagree about which packs exist.
                cachedPacks = SpawnPack.Summonable(parsed);
                cachedPackSource = source;
                cachedWithCreatureIndex = haveCreatureIndex;

                if (parsed.Count != cachedPacks.Count)
                {
                    // Worth saying out loud: from a streamer's side a pack with a mistyped ResRef
                    // simply stops appearing, with nothing anywhere explaining the disappearance.
                    foreach (var pack in parsed)
                    {
                        if (!pack.CanSummonHere())
                            Logger.Info($"Pack '{pack.DisplayName}' is hidden from viewers: this installation has none of its creatures.");
                    }
                }
            }
            return cachedPacks;
        }

        /// <summary>
        /// The same "Relay Server URL" gets typed into two places that need opposite schemes for
        /// the same host: this WebSocket client (ws/wss) and the Twitch extension's fetch() calls
        /// (http/https) - see video_component.js's own normalizeToHttpScheme. Rather than expect a
        /// streamer to remember which of the two identically-labelled fields wants which prefix,
        /// both sides now accept either and translate. ClientWebSocket.ConnectAsync throws
        /// "Only Websocket schemes are allowed: ws, wss" for anything else, which is exactly the
        /// silent-looking "Error" status a wrong scheme used to produce here.
        /// </summary>
        private static string normalizeToWebSocketScheme(string relayUrl)
        {
            if (relayUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return "wss://" + relayUrl.Substring("https://".Length);
            if (relayUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                return "ws://" + relayUrl.Substring("http://".Length);
            return relayUrl;
        }

        private async Task runAsync(string relayUrl, string streamKey, string controlKey, string broadcasterLogin, CancellationToken token)
        {
            var uri = new Uri($"{normalizeToWebSocketScheme(relayUrl).TrimEnd('/')}/ws/ingest/{streamKey}");
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using (var socket = new ClientWebSocket())
                    {
                        await socket.ConnectAsync(uri, token).ConfigureAwait(false);
                        status = TwitchRelayStatus.Connected;
                        LastError = null;

                        // Handshake first: tells the relay which control key may address
                        // commands at this connection, and which Twitch channel this stream key
                        // belongs to - a relay shared by several streamers has no other way to
                        // tell them apart. Sent as a message rather than in the URL so the
                        // control key doesn't end up in the relay's request logs.
                        if (!string.IsNullOrWhiteSpace(controlKey))
                        {
                            var login = broadcasterLogin ?? "";
                            var handshake = Encoding.UTF8.GetBytes(
                                $"{{\"control\":\"{jsonEscape(controlKey)}\",\"broadcasterLogin\":\"{jsonEscape(login)}\"}}");
                            await socket.SendAsync(new ArraySegment<byte>(handshake), WebSocketMessageType.Text, true, token)
                                .ConfigureAwait(false);
                        }

                        var receiveLoop = receiveAsync(socket, token);

                        while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
                        {
                            await sendSignal.WaitAsync(token).ConfigureAwait(false);
                            var payload = pendingPayload;
                            if (payload == null)
                                continue;

                            var bytes = Encoding.UTF8.GetBytes(payload);
                            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token)
                                .ConfigureAwait(false);
                        }

                        await receiveLoop.ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    status = TwitchRelayStatus.Error;
                    Logger.Error("TwitchRelayClient connection error", ex);
                }

                if (token.IsCancellationRequested)
                    break;

                status = TwitchRelayStatus.Connecting;
                try
                {
                    await Task.Delay(ReconnectDelay, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        /// <summary>
        /// Reads commands the relay sends back down this connection - currently viewer-triggered
        /// summons. Runs alongside the send loop; a WebSocket allows one concurrent receive and
        /// one concurrent send, which is exactly this shape.
        /// </summary>
        private async Task receiveAsync(ClientWebSocket socket, CancellationToken token)
        {
            var buffer = new byte[4096];
            try
            {
                while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
                {
                    var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                        break;

                    var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    handleCommand(message);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Logger.Error("TwitchRelayClient receive error", ex);
            }
        }

        /// <summary>
        /// The protagonist's class level, refreshed each ProcessHacker tick. Used to pick which
        /// spawn pack a viewer summon draws from, so a level 1 party gets gibberlings rather
        /// than something that would wipe them.
        /// </summary>
        public int ProtagonistLevel { get; set; }

        // One instance, not a fresh Random per summon: two redemptions landing in the same
        // millisecond would otherwise seed identically and pick the same "random" pack. Only ever
        // used from the receive loop's own thread.
        private readonly Random random = new Random();

        private void handleCommand(string message)
        {
            if (extractString(message, "type") != "summon")
                return;

            var resref = extractString(message, "resref");

            // Whatever a viewer typed in the extension. Passed on as-is: GameSpawnBridge is the
            // one place that sanitises it, so there is a single choke point between viewer text
            // and the game rather than one per caller.
            var viewerText = extractString(message, "message");

            // Who to credit in the game's message log. Resolved by the relay from the viewer's
            // verified identity, so it is their actual Twitch name rather than anything they
            // typed; empty for the control-key mock and for Test summon, which have no viewer.
            var viewerName = extractString(message, "viewer");
            viewerText = GameSpawnBridge.ComposeLogLine(viewerName, viewerText);

            // Which party member the pack lands on. Absent, zero or out of range all mean the
            // protagonist - GameSpawnBridge.ClampTarget is the one place that decides.
            var target = extractInt(message, "target", GameSpawnBridge.DefaultTarget);

            // An explicit ResRef overrides the packs; without one the summon is resolved
            // against the streamer's level-banded packs.
            if (!string.IsNullOrEmpty(resref))
            {
                var amount = extractInt(message, "amount", 1);
                GameSpawnBridge.Instance.Enqueue(new[] { new SpawnEntry { ResRef = resref, Amount = amount } }, viewerText, target);
                return;
            }

            var packs = CurrentPacks();

            // Viewers pick tiles by id, and may pick several - everything they chose spawns, in
            // the order they appear in the streamer's list rather than the order they arrived,
            // so a summon reads the same way each time.
            var requestedIds = extractStringArray(message, "packs");
            if (requestedIds.Count > 0)
            {
                enqueueById(packs, requestedIds, viewerText, target);
                return;
            }

            // No pack named. Either a viewer redeemed the random-summon reward - which names no
            // pack on purpose, and is how people take part while the extension is unapproved and
            // the panel is out of reach - or it is a channel-point reward that predates the pack
            // tiles, which gets the same best-fit pack it always did.
            var wantsRandom = extractBool(message, "random");

            SpawnPack pack;
            if (wantsRandom)
            {
                // The reward that was redeemed may narrow the pool to packs carrying particular
                // tags - that is what lets one channel run several rewards off one pack list.
                var tags = extractStringArray(message, "tags");
                var pool = new List<SpawnPack>();
                foreach (var candidate in packs)
                {
                    if (candidate.HasAllTags(tags))
                        pool.Add(candidate);
                }

                if (pool.Count == 0)
                {
                    Logger.Info($"Random summon ignored: no pack carries [{string.Join(", ", tags.ToArray())}].");
                    return;
                }

                pack = SpawnPack.RandomForLevel(pool, ProtagonistLevel, random);
            }
            else
            {
                pack = SpawnPack.ForLevel(packs, ProtagonistLevel);
            }

            if (pack == null)
            {
                Logger.Info($"Summon ignored: no spawn pack covers level {ProtagonistLevel}.");
                return;
            }

            // A random summon picks its own victim: nobody chose one in a panel, and a reward
            // that always landed on the protagonist would make the streamer's <victim> read the
            // same every single time.
            if (wantsRandom)
            {
                var party = lastPartyNames;
                if (party.Length > 0)
                {
                    var slot = random.Next(party.Length);
                    target = slot + 1;

                    // The template is the streamer's own sentence and already names the viewer,
                    // so it replaces the composed line rather than being prefixed with one.
                    var rendered = renderTemplate(extractString(message, "template"), viewerName, party[slot]);
                    if (rendered != null)
                        viewerText = rendered;
                }
            }

            Logger.Info($"Summoning {(wantsRandom ? "a random pack" : "pack")} for level {ProtagonistLevel}: {pack}");
            GameSpawnBridge.Instance.Enqueue(pack.ExistingEntries(), viewerText, target);
        }

        private void enqueueById(IReadOnlyList<SpawnPack> packs, List<string> requestedIds, string viewerText, int target)
        {
            var ids = SpawnPack.AssignIds(packs);
            var entries = new List<SpawnEntry>();
            var summoned = new List<string>();

            for (int i = 0; i < packs.Count; i++)
            {
                if (!requestedIds.Contains(ids[i]))
                    continue;

                // Re-checked here, not trusted from the command: the pack list this id came from
                // is served to every viewer's browser, so "the extension greyed that tile out"
                // is not something the game side can rely on.
                if (!packs[i].Matches(ProtagonistLevel))
                {
                    Logger.Info($"Summon skipped pack '{ids[i]}': level {ProtagonistLevel} is outside {packs[i].LevelFrom}-{packs[i].LevelTo}.");
                    continue;
                }

                entries.AddRange(packs[i].ExistingEntries());
                summoned.Add(ids[i]);
            }

            if (entries.Count == 0)
            {
                Logger.Info($"Summon ignored: none of [{string.Join(", ", requestedIds.ToArray())}] matched an available pack at level {ProtagonistLevel}.");
                return;
            }

            Logger.Info($"Summoning packs [{string.Join(", ", summoned.ToArray())}] at level {ProtagonistLevel}.");
            GameSpawnBridge.Instance.Enqueue(entries, viewerText, target);
        }

        /// <summary>
        /// A JSON boolean, by the same hand-rolled scan as its siblings - the relay sends small,
        /// known-shaped commands and this file has never pulled in a JSON parser for them.
        /// Absent, false, or anything unrecognised all read as false, so only a literal true
        /// turns a flag on.
        /// </summary>
        /// <summary>
        /// Fills in the streamer's message template: &lt;viewername&gt; for who redeemed, and
        /// &lt;victim&gt; for the party member the pack is about to land on.
        ///
        /// Null when there is no template, which leaves the caller's own line alone. Both
        /// values are ones this side already trusts - a Twitch name the relay resolved against
        /// a verified identity, and a character name read out of the game - and the result goes
        /// through the same sanitiser as every other line before it reaches the message log.
        /// </summary>
        private static string renderTemplate(string template, string viewerName, string victimName)
        {
            if (string.IsNullOrWhiteSpace(template))
                return null;

            return template
                .Replace("<viewername>", viewerName ?? "")
                .Replace("<victim>", victimName ?? "");
        }

        private static bool extractBool(string json, string field)
        {
            var marker = "\"" + field + "\"";
            var at = json.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0)
                return false;

            at = json.IndexOf(':', at + marker.Length);
            if (at < 0)
                return false;

            for (int i = at + 1; i < json.Length; i++)
            {
                var c = json[i];
                if (c == ' ' || c == '"')
                    continue;
                return string.CompareOrdinal(json, i, "true", 0, 4) == 0;
            }

            return false;
        }

        private static int extractInt(string json, string field, int fallback)
        {
            var marker = "\"" + field + "\"";
            var at = json.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0)
                return fallback;

            at = json.IndexOf(':', at + marker.Length);
            if (at < 0)
                return fallback;

            var digits = new StringBuilder();
            for (int i = at + 1; i < json.Length; i++)
            {
                var c = json[i];
                if (char.IsDigit(c))
                    digits.Append(c);
                else if (digits.Length > 0)
                    break;
                else if (c != ' ' && c != '"')
                    break;
            }

            return digits.Length > 0 && int.TryParse(digits.ToString(), out var value) ? value : fallback;
        }

        /// <summary>
        /// Pulls one string field out of a flat JSON object. The core project targets .NET
        /// Framework 4.8 with no JSON library referenced, and the command shape is fixed and
        /// tiny, so this stays hand-rolled rather than dragging in a dependency.
        /// </summary>
        private static string extractString(string json, string field)
        {
            if (string.IsNullOrEmpty(json))
                return null;

            var marker = "\"" + field + "\"";
            var at = json.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0)
                return null;

            at = json.IndexOf(':', at + marker.Length);
            if (at < 0)
                return null;

            var start = json.IndexOf('"', at + 1);
            if (start < 0)
                return null;

            var sb = new StringBuilder();
            for (int i = start + 1; i < json.Length; i++)
            {
                var c = json[i];
                if (c == '\\' && i + 1 < json.Length)
                {
                    sb.Append(json[++i]);
                    continue;
                }
                if (c == '"')
                    return sb.ToString();
                sb.Append(c);
            }
            return null;
        }

        /// <summary>
        /// Pulls a flat array of strings out of the command JSON - "packs":["a","b"]. Same
        /// reasoning as <see cref="extractString"/>: net48 with no JSON library referenced, and
        /// a command shape small enough that a scanner beats a dependency. Stops at the closing
        /// bracket, so a later field can't extend the array.
        /// </summary>
        private static List<string> extractStringArray(string json, string field)
        {
            var values = new List<string>();
            if (string.IsNullOrEmpty(json))
                return values;

            var marker = "\"" + field + "\"";
            var at = json.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0)
                return values;

            at = json.IndexOf('[', at + marker.Length);
            if (at < 0)
                return values;

            var current = new StringBuilder();
            var inString = false;

            for (int i = at + 1; i < json.Length; i++)
            {
                var c = json[i];

                if (inString)
                {
                    if (c == '\\' && i + 1 < json.Length)
                    {
                        current.Append(json[++i]);
                        continue;
                    }
                    if (c == '"')
                    {
                        values.Add(current.ToString());
                        current.Length = 0;
                        inString = false;
                        continue;
                    }
                    current.Append(c);
                    continue;
                }

                if (c == '"')
                    inString = true;
                else if (c == ']')
                    break;
            }

            return values;
        }

        private string buildPayload(IReadOnlyList<BGEntity> party, IReadOnlyList<SpawnPack> packs)
        {
            var sb = new StringBuilder();
            sb.Append("{\"party\":[");
            for (int i = 0; i < party.Count; i++)
            {
                if (i > 0)
                    sb.Append(',');

                var member = party[i];
                sb.Append('{');
                // 1-based, and the order this list is already in: the scan walks the engine's
                // entity array in slot order, which is the same order the game answers Player1..
                // Player6 in. That is what lets a viewer pick a face here and have the game
                // resolve it live when the summon lands, rather than the overlay shipping a
                // position that is already a couple of seconds stale.
                sb.Append("\"slot\":").Append(i + 1).Append(',');
                sb.Append("\"name\":\"").Append(jsonEscape(member.Name2)).Append("\",");
                sb.Append("\"race\":\"").Append(jsonEscape(member.Race)).Append("\",");
                sb.Append("\"class\":\"").Append(jsonEscape(member.ClassString)).Append("\",");
                sb.Append("\"currentHp\":").Append(member.CurrentHP);
                sb.Append('}');
            }
            sb.Append(']');

            // The pack list travels with the party snapshot rather than on an endpoint of its
            // own: viewers already poll this every few seconds, and the tiles they are offered
            // should change with the party they are looking at.
            //
            // "available" is the level band, evaluated here. It is advisory - the tile is shown
            // greyed out rather than hidden, so a viewer can see what a pack unlocks at - and
            // the band is checked again when the summon comes back, since this payload is public
            // and nothing stops a forged command naming an unavailable pack.
            var level = ProtagonistLevel;
            var ids = SpawnPack.AssignIds(packs);

            sb.Append(",\"level\":").Append(level);
            sb.Append(",\"packs\":[");
            for (int i = 0; i < packs.Count; i++)
            {
                if (i > 0)
                    sb.Append(',');

                var pack = packs[i];
                sb.Append('{');
                sb.Append("\"id\":\"").Append(jsonEscape(ids[i])).Append("\",");
                sb.Append("\"name\":\"").Append(jsonEscape(pack.DisplayName)).Append("\",");
                sb.Append("\"from\":").Append(pack.LevelFrom).Append(',');
                sb.Append("\"to\":").Append(pack.LevelTo).Append(',');
                sb.Append("\"cost\":").Append(pack.Cost).Append(',');
                sb.Append("\"available\":").Append(pack.Matches(level) ? "true" : "false");
                sb.Append('}');
            }
            sb.Append(']');

            sb.Append('}');
            return sb.ToString();
        }

        private static string jsonEscape(string s)
        {
            if (string.IsNullOrEmpty(s))
                return "";

            var sb = new StringBuilder(s.Length);
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else
                            sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
