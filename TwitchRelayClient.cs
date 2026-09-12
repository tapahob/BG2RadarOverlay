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
    /// Push-only client for the (self-hosted) relay server under TwitchRelay/ - phase 0 of the
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
        private DateTime lastSendUtc = DateTime.MinValue;
        private volatile string pendingPayload;

        private TwitchRelayClient() { }

        /// <summary>
        /// Called every ProcessHacker.MainLoop() tick with the current settings (same pattern as
        /// cacheInvalidationRequested - a single-threaded owner re-checking desired state each
        /// tick, instead of pushing change events in from the UI thread). No-op unless
        /// enabled/url/key actually changed since the last call.
        /// </summary>
        public void UpdateConfig(bool enabled, string relayUrl, string streamKey, string controlKey)
        {
            lock (gate)
            {
                if (!enabled || string.IsNullOrWhiteSpace(relayUrl) || string.IsNullOrWhiteSpace(streamKey))
                {
                    stopLocked();
                    status = TwitchRelayStatus.Disabled;
                    return;
                }

                if (cts != null && activeUrl == relayUrl && activeKey == streamKey && activeControlKey == controlKey)
                    return;

                stopLocked();
                activeUrl = relayUrl;
                activeKey = streamKey;
                activeControlKey = controlKey;
                cts = new CancellationTokenSource();
                status = TwitchRelayStatus.Connecting;
                var token = cts.Token;
                Task.Factory.StartNew(() => runAsync(relayUrl, streamKey, controlKey, token),
                    token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }
        }

        private void stopLocked()
        {
            cts?.Cancel();
            cts = null;
            activeUrl = null;
            activeKey = null;
            activeControlKey = null;
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
            pendingPayload = buildPayload(party, CurrentPacks());
            sendSignal.Release();
        }

        private string cachedPackSource;
        private List<SpawnPack> cachedPacks = new List<SpawnPack>();

        /// <summary>
        /// The streamer's packs as configured right now. Cached on the raw config string so the
        /// snapshot, which is rebuilt every couple of seconds, doesn't re-parse it every time,
        /// while an edit in the options tab still shows up on viewers' tiles immediately.
        /// </summary>
        public List<SpawnPack> CurrentPacks()
        {
            var source = Configuration.SpawnPacks ?? "";
            if (!string.Equals(source, cachedPackSource, StringComparison.Ordinal))
            {
                cachedPacks = SpawnPack.Deserialize(source);
                cachedPackSource = source;
            }
            return cachedPacks;
        }

        private async Task runAsync(string relayUrl, string streamKey, string controlKey, CancellationToken token)
        {
            var uri = new Uri($"{relayUrl.TrimEnd('/')}/ws/ingest/{streamKey}");
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
                        // commands at this connection. Sent as a message rather than in the
                        // URL so the secret doesn't end up in the relay's request logs.
                        if (!string.IsNullOrWhiteSpace(controlKey))
                        {
                            var handshake = Encoding.UTF8.GetBytes($"{{\"control\":\"{jsonEscape(controlKey)}\"}}");
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

        private void handleCommand(string message)
        {
            if (extractString(message, "type") != "summon")
                return;

            var resref = extractString(message, "resref");

            // Whatever a viewer typed in the extension. Passed on as-is: GameSpawnBridge is the
            // one place that sanitises it, so there is a single choke point between viewer text
            // and the game rather than one per caller.
            var viewerText = extractString(message, "message");

            // An explicit ResRef overrides the packs; without one the summon is resolved
            // against the streamer's level-banded packs.
            if (!string.IsNullOrEmpty(resref))
            {
                var amount = extractInt(message, "amount", 1);
                GameSpawnBridge.Instance.Enqueue(new[] { new SpawnEntry { ResRef = resref, Amount = amount } }, viewerText);
                return;
            }

            var packs = CurrentPacks();

            // Viewers pick tiles by id, and may pick several - everything they chose spawns, in
            // the order they appear in the streamer's list rather than the order they arrived,
            // so a summon reads the same way each time.
            var requestedIds = extractStringArray(message, "packs");
            if (requestedIds.Count > 0)
            {
                enqueueById(packs, requestedIds, viewerText);
                return;
            }

            // No pack named: fall back to resolving one from the protagonist's level. This is
            // what a channel-point reward that predates the pack tiles still sends.
            var pack = SpawnPack.ForLevel(packs, ProtagonistLevel);
            if (pack == null)
            {
                Logger.Info($"Summon ignored: no spawn pack covers level {ProtagonistLevel}.");
                return;
            }

            Logger.Info($"Summoning pack for level {ProtagonistLevel}: {pack}");
            GameSpawnBridge.Instance.Enqueue(pack.Entries, viewerText);
        }

        private void enqueueById(IReadOnlyList<SpawnPack> packs, List<string> requestedIds, string viewerText)
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

                entries.AddRange(packs[i].Entries);
                summoned.Add(ids[i]);
            }

            if (entries.Count == 0)
            {
                Logger.Info($"Summon ignored: none of [{string.Join(", ", requestedIds.ToArray())}] matched an available pack at level {ProtagonistLevel}.");
                return;
            }

            Logger.Info($"Summoning packs [{string.Join(", ", summoned.ToArray())}] at level {ProtagonistLevel}.");
            GameSpawnBridge.Instance.Enqueue(entries, viewerText);
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
                sb.Append("\"name\":\"").Append(jsonEscape(member.Name2)).Append("\",");
                sb.Append("\"race\":\"").Append(jsonEscape(member.Race)).Append("\",");
                sb.Append("\"class\":\"").Append(jsonEscape(member.Class)).Append("\",");
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
