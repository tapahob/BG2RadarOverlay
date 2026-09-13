using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using TwitchRelay;

// Relay between a streamer's overlay and their viewers.
//
// Upstream (overlay -> relay): party snapshots, keyed by a "stream key". Only the latest
// snapshot per key is kept, in memory; a restart forgets everyone until their overlay
// reconnects and pushes again.
//
// Downstream (relay -> overlay): commands, e.g. a viewer-triggered summon. These travel back
// down the same WebSocket and are addressed by a *separate* control key, never the stream key
// - the stream key ends up in the Twitch Extension's broadcaster config, which Twitch serves
// to every viewer's browser, so it is effectively public and must not authorize anything that
// touches the streamer's game.

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddCors(options =>
{
    // Polled/called by a browser-hosted viewer frontend from an origin this relay can't predict.
    //
    // POST and custom headers (Authorization, for the token-balance endpoints) are both allowed
    // now - note what CORS was never actually providing here: it never stops a cross-origin
    // request from being *delivered*, only from being *read back*. /api/command relies on that
    // distinction on purpose (a "simple" no-cors POST still reaches it, response ignored); the
    // real protection everywhere in this file is a secret in the URL or a verified signature, not
    // CORS - so widening this doesn't weaken anything that was actually depending on it.
    options.AddDefaultPolicy(policy => policy.AllowAnyOrigin().AllowAnyHeader().WithMethods("GET", "POST"));
});

var app = builder.Build();
app.UseCors();
app.UseWebSockets();

var snapshots = new ConcurrentDictionary<string, (string Json, DateTime UpdatedUtc)>();
var commandWriters = new ConcurrentDictionary<string, ChannelWriter<string>>();

// Redemption/Bits events name a stream key (or, for EventSub, a Twitch broadcaster id resolved
// to one via EVENTSUB_STREAM_KEY below) but need a *control key* to actually reach an overlay -
// recorded here from the same handshake that already links the two, rather than asking the
// streamer to keep a second copy of her control key in sync in an env var.
var streamKeyToControlKey = new ConcurrentDictionary<string, string>();

var keyPattern = new Regex("^[a-z0-9]{8,64}$");
var staleAfter = TimeSpan.FromSeconds(15);

// Kept in step with GameSpawnBridge.MaxMessageLength on the overlay side, which cuts again at
// the mailbox. Both ends clamp: this relay is the only thing between a viewer and the game, but
// a stale or third-party relay must not be able to overrun the buffer either.
const int maxMessageLength = 95;

// A viewer can only tick as many tiles as the streamer has packs, so this is a backstop against
// a forged command rather than a limit anyone should meet.
const int maxPacksPerSummon = 8;

// ---- Channel Points / Bits payment integrations - both optional, independently configured ----
//
// Both currencies buy the *same* thing: "summon tokens" - the streamer sets a Bits price and/or
// a Channel Points price for one token in config.html, and SpawnPack.Cost (already configured on
// the Twitch Integration tab) is a token count. A viewer buys however many tokens she likes,
// however she likes, then spends them on however many packs in one request from the extension -
// see /api/summon below. That's the whole point of the indirection: neither Bits nor Channel
// Points can charge a viewer-computed total in one native transaction, but *we* can debit a
// balance we track ourselves for any total the extension asks for.
//
// Channel Points (EventSub) needs a *separate* OAuth "Application" from the Extension itself -
// Extensions get their own client id meant only for Extensions Manager flows (onAuthorized,
// configuration.set(), Bits), not for calling Helix/EventSub on the broadcaster's behalf. See
// CLAUDE.md for how to register one and what to put in these environment variables.
var twitchClientId = Environment.GetEnvironmentVariable("TWITCH_CLIENT_ID");
var twitchClientSecret = Environment.GetEnvironmentVariable("TWITCH_CLIENT_SECRET");
var oauthRedirectUri = Environment.GetEnvironmentVariable("TWITCH_OAUTH_REDIRECT_URI");

// Bits verification, and reading a viewer's own identity in /api/summon + /api/balance, both need
// the *Extension's* shared JWT-signing secret (Dev Console -> Extensions -> Manage -> Secret),
// base64-encoded as Twitch hands it out - decoded once here rather than on every verification.
var extensionSecretB64 = Environment.GetEnvironmentVariable("TWITCH_EXTENSION_SECRET");
byte[]? extensionSecret = null;
if (!string.IsNullOrEmpty(extensionSecretB64))
{
    try { extensionSecret = Convert.FromBase64String(extensionSecretB64); }
    catch (FormatException) { extensionSecret = null; }
}

// Reading config.html's saved token prices back out needs a *third* kind of Extension credential:
// an app access token from the extension's own client id/secret (a different secret again from
// TWITCH_EXTENSION_SECRET above - that one signs JWTs, this one is for the client_credentials
// OAuth grant). See ExtensionConfigCache for why a server needs this at all instead of just
// reading Twitch.ext.configuration directly.
var extensionClientId = Environment.GetEnvironmentVariable("TWITCH_EXTENSION_CLIENT_ID");
var extensionClientSecret = Environment.GetEnvironmentVariable("TWITCH_EXTENSION_CLIENT_SECRET");
var broadcasterLogin = Environment.GetEnvironmentVariable("TWITCH_BROADCASTER_LOGIN");
ExtensionConfigCache? tokenPriceCache = null;
if (!string.IsNullOrEmpty(extensionClientId) && !string.IsNullOrEmpty(extensionClientSecret) && !string.IsNullOrEmpty(broadcasterLogin))
    tokenPriceCache = new ExtensionConfigCache(extensionClientId, extensionClientSecret, broadcasterLogin, TimeSpan.FromSeconds(60));

var dataDir = Path.Combine(AppContext.BaseDirectory, "data");
var tokenStore = new TokenStore(Path.Combine(dataDir, "oauth-tokens.json"));
var webhookSecretStore = new WebhookSecretStore(Path.Combine(dataDir, "eventsub-secret.txt"));
var balanceStore = new BalanceStore(Path.Combine(dataDir, "token-balances.json"));
var httpClient = new HttpClient();

// Twitch retries webhook deliveries and can send the same notification more than once - recorded
// here so a redemption already credited doesn't get credited twice. Bits transaction ids get the
// same treatment, in a separate set below, since they arrive over a different path.
var seenEventSubMessageIds = new ConcurrentDictionary<string, DateTime>();
var usedBitsTransactionIds = new ConcurrentDictionary<string, DateTime>();

// One in-flight /oauth/authorize attempt at a time - this is a one-person, one-time setup flow
// run by hand from a browser, not something concurrent callers ever race over.
string? oauthState = null;

app.MapGet("/health", () => Results.Text("OK"));

app.Map("/ws/ingest/{streamKey}", async (HttpContext context, string streamKey) =>
{
    if (!keyPattern.IsMatch(streamKey) || !context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    using var connectionClosed = new CancellationTokenSource();

    // Bounded so a wedged or slow overlay can't grow this without limit; dropping the oldest
    // matters more than delivering every summon, since a stale backlog would fire minutes
    // after the viewer redeemed it.
    var commands = Channel.CreateBounded<string>(
        new BoundedChannelOptions(16) { FullMode = BoundedChannelFullMode.DropOldest });

    string? controlKey = null;

    // One writer task: WebSocket.SendAsync must not be called concurrently.
    var sendLoop = Task.Run(async () =>
    {
        try
        {
            await foreach (var command in commands.Reader.ReadAllAsync(connectionClosed.Token))
            {
                var bytes = Encoding.UTF8.GetBytes(command);
                await socket.SendAsync(bytes, WebSocketMessageType.Text, true, connectionClosed.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
    });

    var buffer = new byte[8192];

    try
    {
        while (socket.State == WebSocketState.Open)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                    break;
                }
                message.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Close)
                break;

            var json = Encoding.UTF8.GetString(message.ToArray());

            // The overlay's first message is a handshake carrying its control key. Only worth
            // parsing until that arrives - everything after it is a snapshot, stored verbatim.
            if (controlKey is null)
            {
                if (tryReadControlKey(json, out var declared) && keyPattern.IsMatch(declared))
                {
                    controlKey = declared;
                    commandWriters[controlKey] = commands.Writer;
                    // Links this stream key to the control key that can actually reach it - the
                    // Bits and Channel Points paths both need this: a viewer's browser (Bits) or
                    // an EventSub notification via EVENTSUB_STREAM_KEY (Channel Points) only ever
                    // names a stream key, never the control key that authorizes writing to game.
                    streamKeyToControlKey[streamKey] = controlKey;
                    continue;
                }
            }

            snapshots[streamKey] = (json, DateTime.UtcNow);
        }
    }
    catch (WebSocketException)
    {
        // Client vanished mid-read; fall through to cleanup.
    }
    finally
    {
        connectionClosed.Cancel();
        commands.Writer.TryComplete();
        if (controlKey is not null)
        {
            commandWriters.TryRemove(controlKey, out _);
            // Only remove the link if it's still pointing at *this* connection - a fast
            // reconnect could otherwise have already overwritten it with the new one, and this
            // cleanup running after that would incorrectly erase a link that's still live.
            streamKeyToControlKey.TryRemove(new KeyValuePair<string, string>(streamKey, controlKey));
        }
        await sendLoop;
    }
});

app.MapGet("/api/character/{streamKey}", (string streamKey) =>
{
    if (!snapshots.TryGetValue(streamKey, out var entry))
        return Results.NotFound();

    if (DateTime.UtcNow - entry.UpdatedUtc > staleAfter)
        return Results.NotFound();

    return Results.Content(entry.Json, "application/json");
});

// Sends a command down to a connected overlay. Addressed by control key, which only the
// overlay and this relay ever see - see the note at the top about why the stream key can't be
// used here. Used by the local mock (a streamer testing with her own control key pasted in) -
// the Channel Points and Bits paths below dispatch through streamKeyToControlKey instead, since
// neither an EventSub notification nor a viewer's browser ever holds a control key.
app.MapPost("/api/command/{controlKey}", async (string controlKey, HttpContext context) =>
{
    if (!keyPattern.IsMatch(controlKey))
        return Results.BadRequest();

    if (!commandWriters.TryGetValue(controlKey, out var writer))
        return Results.NotFound();

    using var reader = new StreamReader(context.Request.Body);
    var body = await reader.ReadToEndAsync();

    if (string.IsNullOrWhiteSpace(body))
        return Results.BadRequest();

    // Rebuilt rather than forwarded verbatim. The `message` field carries whatever a viewer
    // typed, and it ends up in the streamer's game, so this is the point where it stops being
    // arbitrary: unknown fields are dropped, and the text is cut to a shape the downstream
    // hand-rolled parser and the engine's message log can both take.
    if (!tryNormalizeCommand(body, out var command))
        return Results.BadRequest();

    return writer.TryWrite(command) ? Results.Ok() : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
});

// ---- Channel Points (EventSub) - one-time setup flow, run by hand from a browser ----
//
// /oauth/authorize sends the broadcaster to Twitch's consent screen; /oauth/callback exchanges
// the resulting code for tokens, resolves her broadcaster id, and (idempotently) ensures the
// EventSub webhook subscription exists. Nothing here is reachable by viewers in any useful way -
// worst case an outsider hitting /oauth/callback with a guessed code gets "state mismatch".
app.MapGet("/oauth/authorize", () =>
{
    if (string.IsNullOrEmpty(twitchClientId) || string.IsNullOrEmpty(oauthRedirectUri))
        return Results.Text(
            "Channel Points isn't configured on this relay yet - set TWITCH_CLIENT_ID, " +
            "TWITCH_CLIENT_SECRET and TWITCH_OAUTH_REDIRECT_URI (see CLAUDE.md) and restart.",
            statusCode: StatusCodes.Status501NotImplemented);

    oauthState = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    var url = "https://id.twitch.tv/oauth2/authorize"
        + "?client_id=" + Uri.EscapeDataString(twitchClientId)
        + "&redirect_uri=" + Uri.EscapeDataString(oauthRedirectUri)
        + "&response_type=code"
        + "&scope=" + Uri.EscapeDataString("channel:read:redemptions")
        + "&state=" + oauthState;
    return Results.Redirect(url);
});

app.MapGet("/oauth/callback", async (HttpContext context) =>
{
    if (string.IsNullOrEmpty(twitchClientId) || string.IsNullOrEmpty(twitchClientSecret) || string.IsNullOrEmpty(oauthRedirectUri))
        return Results.Text("Channel Points isn't configured on this relay.", statusCode: StatusCodes.Status501NotImplemented);

    var query = context.Request.Query;
    var state = query["state"].ToString();
    var code = query["code"].ToString();

    if (oauthState is null || state != oauthState)
        return Results.Text("Authorization state mismatch or expired - start again from /oauth/authorize.", statusCode: StatusCodes.Status400BadRequest);
    oauthState = null; // one-shot: a replayed callback URL must not be able to redo this.

    if (string.IsNullOrEmpty(code))
    {
        var error = query["error_description"].ToString();
        return Results.Text("Authorization was not granted" + (error.Length > 0 ? ": " + error : "."), statusCode: StatusCodes.Status400BadRequest);
    }

    using var tokenResponse = await httpClient.PostAsync("https://id.twitch.tv/oauth2/token", new FormUrlEncodedContent(new Dictionary<string, string>
    {
        ["client_id"] = twitchClientId,
        ["client_secret"] = twitchClientSecret,
        ["code"] = code,
        ["grant_type"] = "authorization_code",
        ["redirect_uri"] = oauthRedirectUri
    }));

    var tokenBody = await tokenResponse.Content.ReadAsStringAsync();
    if (!tokenResponse.IsSuccessStatusCode)
        return Results.Text("Could not exchange the authorization code: " + tokenBody, statusCode: StatusCodes.Status502BadGateway);

    using var tokenDoc = JsonDocument.Parse(tokenBody);
    var tokenRoot = tokenDoc.RootElement;
    var accessToken = tokenRoot.GetProperty("access_token").GetString() ?? "";
    var refreshToken = tokenRoot.GetProperty("refresh_token").GetString() ?? "";
    var expiresIn = tokenRoot.TryGetProperty("expires_in", out var expEl) ? expEl.GetInt32() : 3600;
    await tokenStore.SaveAsync(accessToken, refreshToken, expiresIn);

    var broadcasterId = await TwitchApi.ResolveBroadcasterIdAsync(httpClient, accessToken, twitchClientId);
    if (broadcasterId is null)
        return Results.Text("Authorized, but could not resolve your Twitch user id - Channel Points redemptions won't be picked up yet.", statusCode: StatusCodes.Status502BadGateway);

    var webhookSecret = await webhookSecretStore.GetOrCreateAsync();
    var callbackUrl = new Uri(new Uri(oauthRedirectUri), "/eventsub/callback").ToString();
    var (ok, status) = await TwitchApi.EnsureRedemptionSubscriptionAsync(httpClient, accessToken, twitchClientId, broadcasterId, callbackUrl, webhookSecret);

    return Results.Text(
        ok ? $"Authorized. EventSub subscription: {status}. Channel Points redemptions are live."
           : $"Authorized, but the EventSub subscription could not be created: {status}",
        statusCode: ok ? StatusCodes.Status200OK : StatusCodes.Status502BadGateway);
});

// Twitch's actual delivery endpoint once the subscription above exists. Public by necessity -
// Twitch calls it directly - so the HMAC signature check is what stands in for authentication;
// see the comment on BitsReceipt for why that's a sound way to trust a public endpoint.
app.MapPost("/eventsub/callback", async (HttpContext context) =>
{
    using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
    var rawBody = await reader.ReadToEndAsync();

    var messageType = context.Request.Headers["Twitch-Eventsub-Message-Type"].ToString();
    var messageId = context.Request.Headers["Twitch-Eventsub-Message-Id"].ToString();
    var timestamp = context.Request.Headers["Twitch-Eventsub-Message-Timestamp"].ToString();
    var signature = context.Request.Headers["Twitch-Eventsub-Message-Signature"].ToString();

    var webhookSecret = await webhookSecretStore.GetOrCreateAsync();
    var expectedBytes = new HMACSHA256(Encoding.UTF8.GetBytes(webhookSecret))
        .ComputeHash(Encoding.UTF8.GetBytes(messageId + timestamp + rawBody));
    var expected = "sha256=" + Convert.ToHexString(expectedBytes).ToLowerInvariant();

    if (signature.Length != expected.Length
        || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(signature)))
        return Results.Unauthorized();

    using var doc = JsonDocument.Parse(rawBody);
    var root = doc.RootElement;

    if (messageType == "webhook_callback_verification")
        return Results.Text(root.GetProperty("challenge").GetString() ?? "", "text/plain");

    if (messageType != "notification")
        return Results.Ok(); // revocation, or a message type this relay doesn't act on

    // Twitch retries deliveries and can send the same notification more than once - a message
    // already handled must not spawn its pack again.
    var now = DateTime.UtcNow;
    foreach (var stale in seenEventSubMessageIds.Where(kv => now - kv.Value > TimeSpan.FromMinutes(10)).Select(kv => kv.Key).ToList())
        seenEventSubMessageIds.TryRemove(stale, out _);
    if (messageId.Length == 0 || !seenEventSubMessageIds.TryAdd(messageId, now))
        return Results.Ok();

    if (root.GetProperty("subscription").GetProperty("type").GetString() != "channel.channel_points_custom_reward_redemption.add")
        return Results.Ok();

    var ev = root.GetProperty("event");
    var rewardTitle = ev.GetProperty("reward").GetProperty("title").GetString() ?? "";
    var rewardCost = ev.GetProperty("reward").TryGetProperty("cost", out var costEl) && costEl.ValueKind == JsonValueKind.Number ? costEl.GetInt32() : 0;
    var userId = ev.GetProperty("user_id").GetString() ?? "";

    if (tokenPriceCache is null || userId.Length == 0 || rewardCost <= 0)
        return Results.Ok();

    var prices = await tokenPriceCache.GetAsync(httpClient);
    if (prices is null || prices.PointsPerToken <= 0 || prices.RewardName.Trim().Length == 0)
        return Results.Ok(); // Channel Points isn't priced/named in config.html yet

    // Matched by name, not a stored id - the one Custom Reward that sells tokens just has to be
    // titled whatever she typed into config.html's "Token Reward Name" field.
    if (!string.Equals(rewardTitle.Trim(), prices.RewardName.Trim(), StringComparison.OrdinalIgnoreCase))
        return Results.Ok(); // some other reward on her channel, unrelated to tokens

    var broadcasterId = await tokenPriceCache.GetBroadcasterIdAsync(httpClient);
    if (broadcasterId is null)
        return Results.Ok();

    // Rounds down: a reward priced at, say, 150 points against a 100-points-per-token rate
    // credits 1 token, not 1.5 - the leftover is the cost of a price that doesn't divide evenly,
    // same as change a vending machine doesn't give back.
    var tokens = rewardCost / prices.PointsPerToken;
    if (tokens > 0)
        await balanceStore.CreditAsync(balanceKey(broadcasterId, userId), tokens);

    return Results.Ok();
});

// ---- Bits: buy tokens ----
//
// A Bits purchase is verified straight from the extension frontend: it already holds the signed
// transactionReceipt(s) Twitch handed it in onTransactionComplete, and posts them here itself. No
// broadcaster OAuth needed for this path at all - only the extension's own shared secret
// (TWITCH_EXTENSION_SECRET), used to check those signatures actually came from Twitch rather than
// being invented by the caller.
app.MapPost("/api/bits-purchase/{streamKey}", async (string streamKey, HttpContext context) =>
{
    if (!keyPattern.IsMatch(streamKey))
        return Results.BadRequest();
    if (extensionSecret is null)
        return Results.Text("Bits isn't configured on this relay yet - set TWITCH_EXTENSION_SECRET (see CLAUDE.md).", statusCode: StatusCodes.Status501NotImplemented);
    if (tokenPriceCache is null)
        return Results.Text("Token pricing isn't configured on this relay yet - set TWITCH_EXTENSION_CLIENT_ID, TWITCH_EXTENSION_CLIENT_SECRET and TWITCH_BROADCASTER_LOGIN (see CLAUDE.md).", statusCode: StatusCodes.Status501NotImplemented);

    var prices = await tokenPriceCache.GetAsync(httpClient);
    if (prices is null || prices.BitsPerToken <= 0)
        return Results.Text("Bits aren't priced in config.html yet.", statusCode: StatusCodes.Status501NotImplemented);

    using var reader = new StreamReader(context.Request.Body);
    var body = await reader.ReadToEndAsync();

    JsonDocument doc;
    try { doc = JsonDocument.Parse(body); }
    catch (JsonException) { return Results.BadRequest(); }

    using (doc)
    {
        var root = doc.RootElement;
        if (!root.TryGetProperty("receipts", out var receiptsEl) || receiptsEl.ValueKind != JsonValueKind.Array)
            return Results.BadRequest();

        var now = DateTime.UtcNow;
        foreach (var stale in usedBitsTransactionIds.Where(kv => now - kv.Value > TimeSpan.FromHours(24)).Select(kv => kv.Key).ToList())
            usedBitsTransactionIds.TryRemove(stale, out _);

        var verifiedTotal = 0;
        string? userId = null;
        foreach (var receiptEl in receiptsEl.EnumerateArray())
        {
            if (receiptEl.ValueKind != JsonValueKind.String)
                continue;
            var verified = BitsReceipt.TryVerify(receiptEl.GetString() ?? "", extensionSecret);
            if (verified is null)
                continue;
            // A transaction id can only ever pay for one credit - otherwise the same receipt
            // could be replayed to top up the balance again for free.
            if (!usedBitsTransactionIds.TryAdd(verified.TransactionId, now))
                continue;
            verifiedTotal += verified.Amount;
            userId ??= verified.UserId; // every receipt in one purchase is the same viewer
        }

        if (userId is null || verifiedTotal <= 0)
            return Results.BadRequest();

        var broadcasterId = await tokenPriceCache.GetBroadcasterIdAsync(httpClient);
        if (broadcasterId is null)
            return Results.StatusCode(StatusCodes.Status502BadGateway);

        // Rounds down: paying for 250 bits at 100-bits-per-token credits 2 tokens, not 2.5 - the
        // leftover isn't refunded, same as the reward-redemption side in /eventsub/callback.
        var tokens = verifiedTotal / prices.BitsPerToken;
        var balance = await balanceStore.CreditAsync(balanceKey(broadcasterId, userId), tokens);

        return Results.Json(new { credited = tokens, balance });
    }
});

// ---- Spend tokens: one combined summon, however many packs were picked ----
//
// This is the step that actually needed a token economy in the first place: neither Bits nor
// Channel Points can charge for an arbitrary viewer-picked combo in one native transaction, but
// debiting a balance *we* track has no such limit. Authorized by the viewer's own onAuthorized
// JWT, not a control key or a payment receipt - proof here is "who is asking", checked against a
// balance that was only ever credited by a verified Bits/Channel-Points event.
app.MapPost("/api/summon/{streamKey}", async (string streamKey, HttpContext context) =>
{
    if (!keyPattern.IsMatch(streamKey))
        return Results.BadRequest();
    if (extensionSecret is null)
        return Results.Text("Summoning isn't configured on this relay yet - set TWITCH_EXTENSION_SECRET (see CLAUDE.md).", statusCode: StatusCodes.Status501NotImplemented);
    if (tokenPriceCache is null)
        return Results.Text("Token pricing isn't configured on this relay yet - set TWITCH_EXTENSION_CLIENT_ID, TWITCH_EXTENSION_CLIENT_SECRET and TWITCH_BROADCASTER_LOGIN (see CLAUDE.md).", statusCode: StatusCodes.Status501NotImplemented);
    if (!streamKeyToControlKey.TryGetValue(streamKey, out var controlKey) || !commandWriters.TryGetValue(controlKey, out var writer))
        return Results.NotFound();
    if (!snapshots.TryGetValue(streamKey, out var snapshot))
        return Results.NotFound();

    var broadcasterId = await tokenPriceCache.GetBroadcasterIdAsync(httpClient);
    if (broadcasterId is null)
        return Results.StatusCode(StatusCodes.Status502BadGateway);

    using var reader = new StreamReader(context.Request.Body);
    var body = await reader.ReadToEndAsync();

    JsonDocument doc;
    try { doc = JsonDocument.Parse(body); }
    catch (JsonException) { return Results.BadRequest(); }

    using (doc)
    {
        var root = doc.RootElement;

        var authToken = root.TryGetProperty("authToken", out var authEl) && authEl.ValueKind == JsonValueKind.String ? authEl.GetString() ?? "" : "";
        var viewerId = tryGetViewerUserId(authToken, extensionSecret);
        if (viewerId is null)
            return Results.Text("Could not verify your Twitch identity - share it with the extension to spend tokens.", statusCode: StatusCodes.Status401Unauthorized);
        var key = balanceKey(broadcasterId, viewerId);

        if (!root.TryGetProperty("packs", out var packsEl) || packsEl.ValueKind != JsonValueKind.Array)
            return Results.BadRequest();

        var requestedIds = new List<string>();
        foreach (var item in packsEl.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                continue;
            var id = item.GetString() ?? "";
            if (id.Length is > 0 and <= 32 && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') && !requestedIds.Contains(id))
                requestedIds.Add(id);
        }
        if (requestedIds.Count == 0)
            return Results.BadRequest();

        // Cost is looked up here, from the streamer's own current packs, rather than trusted from
        // the request - a viewer's browser saying "this combo costs 10" would otherwise be free
        // to lie about what it owes.
        var availablePacks = readPacksFromSnapshot(snapshot.Json);
        var requiredTotal = 0;
        var resolvedIds = new List<string>();
        foreach (var id in requestedIds)
        {
            var pack = availablePacks.FirstOrDefault(p => p.Id == id);
            if (pack.Id is null)
                continue; // deleted mid-request, or a forged id - just drop it rather than fail the whole summon
            requiredTotal += pack.Cost;
            resolvedIds.Add(id);
        }
        if (resolvedIds.Count == 0)
            return Results.BadRequest();

        if (!await balanceStore.TrySpendAsync(key, requiredTotal))
        {
            var balance = await balanceStore.GetBalanceAsync(key);
            return Results.Json(new { error = "insufficient_balance", required = requiredTotal, balance }, statusCode: StatusCodes.Status402PaymentRequired);
        }

        var rawMessage = root.TryGetProperty("message", out var messageEl) && messageEl.ValueKind == JsonValueKind.String
            ? (messageEl.GetString() ?? "")
            : "";

        if (!writer.TryWrite(buildSummonCommand(resolvedIds, rawMessage)))
        {
            // The spend already happened - refund it rather than leave a viewer charged for a
            // summon that never reached the overlay (a full outbound queue, in practice).
            await balanceStore.CreditAsync(key, requiredTotal);
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        return Results.Json(new { balance = await balanceStore.GetBalanceAsync(key) });
    }
});

// So the extension can show "You have N tokens" and know whether a selection is affordable
// before the viewer commits to a purchase.
app.MapPost("/api/balance/{streamKey}", async (string streamKey, HttpContext context) =>
{
    if (!keyPattern.IsMatch(streamKey))
        return Results.BadRequest();
    if (extensionSecret is null)
        return Results.Text("Not configured on this relay yet.", statusCode: StatusCodes.Status501NotImplemented);
    if (tokenPriceCache is null)
        return Results.Text("Token pricing isn't configured on this relay yet.", statusCode: StatusCodes.Status501NotImplemented);

    using var reader = new StreamReader(context.Request.Body);
    var body = await reader.ReadToEndAsync();

    string? authToken = null;
    try
    {
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("authToken", out var authEl) && authEl.ValueKind == JsonValueKind.String)
            authToken = authEl.GetString();
    }
    catch (JsonException) { return Results.BadRequest(); }

    var viewerId = tryGetViewerUserId(authToken ?? "", extensionSecret);
    if (viewerId is null)
        return Results.Text("Could not verify your Twitch identity.", statusCode: StatusCodes.Status401Unauthorized);

    var broadcasterId = await tokenPriceCache.GetBroadcasterIdAsync(httpClient);
    if (broadcasterId is null)
        return Results.StatusCode(StatusCodes.Status502BadGateway);

    return Results.Json(new { balance = await balanceStore.GetBalanceAsync(balanceKey(broadcasterId, viewerId)) });
});

app.Run();

// Balances are per broadcaster *and* viewer - never just the viewer, or a relay serving more than
// one channel would let tokens bought on one streamer's channel spend on another's; never keyed
// by stream key either, since regenerating it (the Twitch Integration tab's "Generate" button)
// must not orphan a balance a viewer already paid real Bits or points for.
static string balanceKey(string broadcasterId, string viewerId) => broadcasterId + ":" + viewerId;

// Verifies a viewer's own Twitch.ext.onAuthorized token and returns their *real* user id - never
// the always-present opaque_user_id, even as a fallback. Bits receipts and Channel Points
// redemptions both always carry a real id (spending real Bits or redeeming rewards both require a
// full, identified Twitch account), so a balance is only ever credited under a real id; falling
// back to an opaque one here would let a viewer who hasn't shared their identity read or spend a
// balance keyed under an id it can never actually match.
static string? tryGetViewerUserId(string authToken, byte[] extensionSecret)
{
    if (string.IsNullOrEmpty(authToken))
        return null;

    var payload = Jwt.TryVerifyAndDecode(authToken, extensionSecret);
    if (payload is null)
        return null;

    var userId = payload.Value.TryGetProperty("user_id", out var idEl) ? idEl.GetString() : null;
    return string.IsNullOrEmpty(userId) ? null : userId;
}

// Shared by the Channel Points and Bits paths, which both arrive at "these pack ids, plus maybe
// a message" through completely different verification but need to hand the overlay the exact
// same command shape /api/command already produces.
static string buildSummonCommand(IReadOnlyList<string> packIds, string rawMessage)
{
    var message = sanitizeMessage(rawMessage ?? "");
    var payload = new Dictionary<string, object> { ["type"] = "summon" };
    if (packIds.Count > 0)
        payload["packs"] = packIds.Take(maxPacksPerSummon).ToList();
    if (message.Length > 0)
        payload["message"] = message;
    return JsonSerializer.Serialize(payload);
}

// Pulls id/name/cost back out of a party snapshot's "packs" array - the same shape
// TwitchRelayClient.buildPayload puts there. Never throws: a snapshot that's missing, stale, or
// malformed just yields no packs, which every caller already treats as "nothing matched".
static List<(string Id, string Name, int Cost)> readPacksFromSnapshot(string snapshotJson)
{
    var result = new List<(string, string, int)>();
    try
    {
        using var doc = JsonDocument.Parse(snapshotJson);
        if (!doc.RootElement.TryGetProperty("packs", out var packsEl) || packsEl.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var p in packsEl.EnumerateArray())
        {
            var id = p.TryGetProperty("id", out var idEl) ? (idEl.GetString() ?? "") : "";
            var name = p.TryGetProperty("name", out var nameEl) ? (nameEl.GetString() ?? "") : "";
            var cost = p.TryGetProperty("cost", out var costEl) && costEl.ValueKind == JsonValueKind.Number ? costEl.GetInt32() : 0;
            if (id.Length > 0)
                result.Add((id, name, cost));
        }
    }
    catch (JsonException) { }
    return result;
}

static bool tryNormalizeCommand(string body, out string command)
{
    command = "";
    try
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return false;

        if (!root.TryGetProperty("type", out var typeElement)
            || typeElement.ValueKind != JsonValueKind.String
            || typeElement.GetString() != "summon")
            return false;

        string? resref = null;
        if (root.TryGetProperty("resref", out var resrefElement)
            && resrefElement.ValueKind == JsonValueKind.String)
        {
            var candidate = resrefElement.GetString() ?? "";
            if (candidate.Length is > 0 and <= 8 && candidate.All(c => char.IsLetterOrDigit(c) || c == '_'))
                resref = candidate;
        }

        var amount = 1;
        if (root.TryGetProperty("amount", out var amountElement)
            && amountElement.ValueKind == JsonValueKind.Number
            && amountElement.TryGetInt32(out var parsedAmount))
        {
            amount = Math.Clamp(parsedAmount, 1, 20);
        }

        // Ids of the packs the viewer picked, in the order sent. Capped: the overlay spawns
        // every pack named here, so an unbounded list is an unbounded spawn.
        var packs = new List<string>();
        if (root.TryGetProperty("packs", out var packsElement)
            && packsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in packsElement.EnumerateArray())
            {
                if (packs.Count >= maxPacksPerSummon)
                    break;
                if (item.ValueKind != JsonValueKind.String)
                    continue;

                var id = item.GetString() ?? "";
                if (id.Length is > 0 and <= 32
                    && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
                    && !packs.Contains(id))
                {
                    packs.Add(id);
                }
            }
        }

        var message = "";
        if (root.TryGetProperty("message", out var messageElement)
            && messageElement.ValueKind == JsonValueKind.String)
        {
            message = sanitizeMessage(messageElement.GetString() ?? "");
        }

        var payload = new Dictionary<string, object> { ["type"] = "summon" };
        if (resref is not null)
        {
            payload["resref"] = resref;
            payload["amount"] = amount;
        }
        if (packs.Count > 0)
            payload["packs"] = packs;
        if (message.Length > 0)
            payload["message"] = message;

        command = JsonSerializer.Serialize(payload);
        return true;
    }
    catch (JsonException)
    {
        return false;
    }
}

// Printable ASCII only, whitespace collapsed, length-capped. Quotes and backslashes go too:
// the overlay reads this back with a hand-rolled string scanner rather than a JSON parser, and
// text that can close its own field there could make one command look like another.
static string sanitizeMessage(string raw)
{
    var builder = new StringBuilder(maxMessageLength);
    var lastWasSpace = true;

    foreach (var c in raw)
    {
        if (c is ' ' or '\t' or '\r' or '\n')
        {
            if (!lastWasSpace && builder.Length < maxMessageLength)
                builder.Append(' ');
            lastWasSpace = true;
            continue;
        }

        if (c < 32 || c > 126 || c is '"' or '\\')
            continue;

        if (builder.Length >= maxMessageLength)
            break;

        builder.Append(c);
        lastWasSpace = false;
    }

    return builder.ToString().TrimEnd();
}

static bool tryReadControlKey(string json, out string controlKey)
{
    controlKey = "";
    try
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            return false;
        if (!document.RootElement.TryGetProperty("control", out var element))
            return false;
        if (element.ValueKind != JsonValueKind.String)
            return false;

        controlKey = element.GetString() ?? "";
        return controlKey.Length > 0;
    }
    catch (JsonException)
    {
        return false;
    }
}
