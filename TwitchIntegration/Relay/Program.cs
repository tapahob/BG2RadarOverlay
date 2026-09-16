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

// This relay can serve several streamers at once - every stream key is scoped to whichever
// overlay's handshake declared it, never assumed to be "the" one streamer. Both maps are
// populated from that same handshake and cleaned up together when the connection drops.
//
// Bits/summon/balance requests name a stream key (a viewer's browser knows its own), but need a
// *control key* to actually reach an overlay, and a *broadcaster login* to know whose token
// prices/balances apply - the handshake is the only place all three ever come together.
var streamKeyToControlKey = new ConcurrentDictionary<string, string>();
var streamKeyToBroadcasterLogin = new ConcurrentDictionary<string, string>();

var keyPattern = new Regex("^[a-z0-9]{8,64}$");
var broadcasterIdPattern = new Regex("^[0-9]{1,20}$");
var staleAfter = TimeSpan.FromSeconds(15);

// Kept in step with GameSpawnBridge.MaxMessageLength on the overlay side, which cuts again at
// the mailbox. Both ends clamp: this relay is the only thing between a viewer and the game, but
// a stale or third-party relay must not be able to overrun the buffer either.
const int maxMessageLength = 95;

// A viewer can only tick as many tiles as the streamer has packs, so this is a backstop against
// a forged command rather than a limit anyone should meet.
const int maxPacksPerSummon = 8;

// One Bits purchase produces one receipt; the array exists only because the shape allows several.
// Capped so an arbitrarily long list can't turn one request into unbounded signature checking.
const int maxReceiptsPerPurchase = 10;

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
// OAuth grant). This is the one credential genuinely shared across every streamer this relay
// serves - it's extension-wide, not tied to any one broadcaster - which is exactly why
// ExtensionConfigCache itself is what's keyed per streamer, not this. See ExtensionConfigCache
// for why a server needs this at all instead of just reading Twitch.ext.configuration directly.
var extensionClientId = Environment.GetEnvironmentVariable("TWITCH_EXTENSION_CLIENT_ID");
var extensionClientSecret = Environment.GetEnvironmentVariable("TWITCH_EXTENSION_CLIENT_SECRET");
ExtensionConfigCache? tokenPriceCache = null;
if (!string.IsNullOrEmpty(extensionClientId) && !string.IsNullOrEmpty(extensionClientSecret))
    tokenPriceCache = new ExtensionConfigCache(extensionClientId, extensionClientSecret, TimeSpan.FromSeconds(60));

// Everything that has to outlive a deployment: token balances viewers paid real Bits for, each
// streamer's OAuth tokens, the EventSub secret, spent transaction ids. Under the app directory by
// default, which is also the directory a redeploy replaces - so RELAY_DATA_DIR exists to put it
// somewhere a deploy cannot reach (see CLAUDE.md).
var dataDir = Environment.GetEnvironmentVariable("RELAY_DATA_DIR") is { Length: > 0 } configuredDataDir
    ? configuredDataDir
    : Path.Combine(AppContext.BaseDirectory, "data");
var tokenStore = new TokenStore(Path.Combine(dataDir, "oauth-tokens.json"));
var webhookSecretStore = new WebhookSecretStore(Path.Combine(dataDir, "eventsub-secret.txt"));
var balanceStore = new BalanceStore(Path.Combine(dataDir, "token-balances.json"));
// What each Channel Points redemption paid out, so a refund can take back exactly that much.
var redemptionLedger = new RedemptionLedger(Path.Combine(dataDir, "credited-redemptions.json"));
var httpClient = new HttpClient();

// Twitch retries webhook deliveries and can send the same notification more than once - recorded
// here so a redemption already credited doesn't get credited twice. Bits transaction ids get the
// same treatment, in a separate set, since they arrive over a different path.
//
// Both are on disk, not in memory: redeploying this relay is routine, and a set that empties on
// restart means every receipt a viewer's browser is still holding becomes a fresh free top-up
// the moment the process comes back.
var seenEventSubMessageIds = new ReplayGuard(Path.Combine(dataDir, "seen-eventsub-messages.json"));
var usedBitsTransactionIds = new ReplayGuard(Path.Combine(dataDir, "used-bits-transactions.json"));

// Answers for /api/eventsub-status, which is public and would otherwise hit Helix twice per
// request. A streamer watching config.html for the subscription to go live polls it; anyone else
// can too.
var eventSubStatusCache = new ConcurrentDictionary<string, (EventSubStatus Answer, DateTime CheckedUtc)>();
var eventSubStatusTtl = TimeSpan.FromSeconds(15);

// How far back a signed EventSub delivery is still accepted. Twitch's own guidance is to reject
// anything older than 10 minutes; without it a captured notification stays replayable forever,
// and there would be no bound on how long the seen-ids set has to remember anything either.
var eventSubMaxAge = TimeSpan.FromMinutes(10);

// How long a redemption stays refundable as far as this relay is concerned. A redemption sitting
// in a streamer's request queue can be cancelled whenever they get to it, which in practice is
// minutes to days; past this the ledger entry is dropped and a refund simply takes nothing back.
var refundWindow = TimeSpan.FromDays(30);

// Signed deliveries that failed their signature check, and when the last one was. This is the
// symptom of the one failure mode that is otherwise completely silent: if the relay's webhook
// secret is ever lost, it generates a fresh one, every subscription already registered with
// Twitch keeps signing with the old one, and every redemption from then on is rejected here
// while config.html goes on reporting the subscription as healthy - because, at Twitch's end, it
// is. Surfacing the count is what turns that into something a streamer can see and act on.
var signatureFailures = 0;
DateTime? lastSignatureFailureUtc = null;

// Several different streamers can each be running their own one-time /oauth/authorize setup
// around the same time, so this holds every state value currently in flight (not just one),
// pruned of anything older than 10 minutes - long enough for someone to actually click through
// Twitch's consent screen, short enough that a stale, unused state can't be replayed later.
var pendingOAuthStates = new ConcurrentDictionary<string, DateTime>();

app.MapGet("/health", () => Results.Text("OK"));

// config.html's view of whether a streamer's Channel Points setup is complete. `Authorized` is
// the simple yes/no it keys off; the rest says *what* is missing, so "you set this up before
// refunds were handled" reads differently from "you never set this up at all".


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
    string? broadcasterLogin = null;

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

            // The overlay's first message is a handshake carrying its control key (and, since
            // this relay can serve several streamers, which Twitch channel this stream key
            // belongs to). Only worth parsing until that arrives - everything after it is a
            // snapshot, stored verbatim.
            if (controlKey is null)
            {
                if (tryReadHandshake(json, out var declared, out var declaredLogin) && keyPattern.IsMatch(declared))
                {
                    // The stream key is public by design (see the note at the top of this file) -
                    // it travels to every viewer's browser in the extension's broadcaster config.
                    // So it cannot be what decides who gets to *write* this stream's snapshot:
                    // that snapshot is where /api/summon reads pack costs from, and anyone who
                    // could replace it could price every pack at zero and summon for free, or
                    // point the stream key at a control key of their own and cut the real overlay
                    // off. A stream key already bound to a live connection may therefore only be
                    // taken over by a handshake presenting the same control key - which the real
                    // overlay always has and nobody else ever sees.
                    if (streamKeyToControlKey.TryGetValue(streamKey, out var boundControlKey)
                        && boundControlKey != declared
                        && commandWriters.ContainsKey(boundControlKey))
                    {
                        break;
                    }

                    controlKey = declared;
                    commandWriters[controlKey] = commands.Writer;
                    // Links this stream key to the control key that can actually reach it, and to
                    // whose channel it is - a viewer's browser (Bits) or an EventSub notification
                    // (Channel Points) only ever names a stream key or a Twitch broadcaster id,
                    // never both at once, so this is the one place they're tied together.
                    streamKeyToControlKey[streamKey] = controlKey;
                    if (declaredLogin.Length > 0)
                    {
                        broadcasterLogin = declaredLogin;
                        streamKeyToBroadcasterLogin[streamKey] = broadcasterLogin;
                    }
                    continue;
                }
            }

            // Only a connection that has identified itself with a control key may publish a
            // snapshot; an un-handshaked socket knowing nothing but the public stream key gets
            // its messages dropped rather than allowed to dictate what packs cost.
            if (controlKey is null)
                continue;

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
            // Only if it's still *this* connection's writer: an overlay that reconnects with the
            // same control key has already replaced it by now, and removing it blindly would
            // leave the live connection unaddressable - no summon a viewer paid for would arrive.
            commandWriters.TryRemove(new KeyValuePair<string, ChannelWriter<string>>(controlKey, commands.Writer));

            // The stream key links can't be matched on value the same way - a reconnecting
            // overlay presents the *same* control key and login, so comparing those would happily
            // unlink the connection that just replaced this one. Whether the control key is still
            // registered above is the thing that actually distinguishes the two cases: if it is,
            // someone live is using this link and it stays. Dropping it here is what used to
            // leave a reconnected overlay looking, to every viewer, like a stream that had gone
            // away - /api/summon and /api/bits-purchase both answer "not found" without it.
            if (!commandWriters.ContainsKey(controlKey))
            {
                streamKeyToControlKey.TryRemove(new KeyValuePair<string, string>(streamKey, controlKey));
                if (broadcasterLogin is not null)
                    streamKeyToBroadcasterLogin.TryRemove(new KeyValuePair<string, string>(streamKey, broadcasterLogin));
            }
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

    // Prune anything stale before adding - a state nobody ever came back for shouldn't linger
    // forever, and this is the one place that naturally runs often enough to do the sweeping.
    var cutoff = DateTime.UtcNow.AddMinutes(-10);
    foreach (var stale in pendingOAuthStates.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList())
        pendingOAuthStates.TryRemove(stale, out _);

    var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    pendingOAuthStates[state] = DateTime.UtcNow;

    var url = TwitchEndpoints.Id + "/oauth2/authorize"
        + "?client_id=" + Uri.EscapeDataString(twitchClientId)
        + "&redirect_uri=" + Uri.EscapeDataString(oauthRedirectUri)
        + "&response_type=code"
        + "&scope=" + Uri.EscapeDataString("channel:read:redemptions")
        + "&state=" + state;
    return Results.Redirect(url);
});

app.MapGet("/oauth/callback", async (HttpContext context) =>
{
    if (string.IsNullOrEmpty(twitchClientId) || string.IsNullOrEmpty(twitchClientSecret) || string.IsNullOrEmpty(oauthRedirectUri))
        return Results.Text("Channel Points isn't configured on this relay.", statusCode: StatusCodes.Status501NotImplemented);

    var query = context.Request.Query;
    var state = query["state"].ToString();
    var code = query["code"].ToString();

    // Removed on first use regardless of outcome - one state authorizes one attempt, by one
    // streamer, once. This is also what keeps two streamers authorizing at the same time from
    // being able to interfere with each other: each has their own state value.
    if (state.Length == 0 || !pendingOAuthStates.TryRemove(state, out _))
        return Results.Text("Authorization state mismatch or expired - start again from /oauth/authorize.", statusCode: StatusCodes.Status400BadRequest);

    if (string.IsNullOrEmpty(code))
    {
        var error = query["error_description"].ToString();
        return Results.Text("Authorization was not granted" + (error.Length > 0 ? ": " + error : "."), statusCode: StatusCodes.Status400BadRequest);
    }

    using var tokenResponse = await httpClient.PostAsync(TwitchEndpoints.Id + "/oauth2/token", new FormUrlEncodedContent(new Dictionary<string, string>
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

    JsonDocument tokenDoc;
    try { tokenDoc = JsonDocument.Parse(tokenBody); }
    catch (JsonException) { return Results.Text("Twitch's token response could not be read.", statusCode: StatusCodes.Status502BadGateway); }

    using var tokenDocScope = tokenDoc;
    var tokenRoot = tokenDoc.RootElement;
    // TryGetProperty throughout: a streamer setting this up should see "something went wrong at
    // Twitch", not a 500 from a field that wasn't there.
    var accessToken = tokenRoot.TryGetProperty("access_token", out var atEl) ? (atEl.GetString() ?? "") : "";
    var refreshToken = tokenRoot.TryGetProperty("refresh_token", out var rtEl) ? (rtEl.GetString() ?? "") : "";
    var expiresIn = tokenRoot.TryGetProperty("expires_in", out var expEl) && expEl.ValueKind == JsonValueKind.Number
        && expEl.TryGetInt32(out var parsedExpiresIn) ? parsedExpiresIn : 3600;
    if (accessToken.Length == 0)
        return Results.Text("Twitch did not return an access token.", statusCode: StatusCodes.Status502BadGateway);

    // Whose tokens these are is only knowable *after* exchanging the code - the access token
    // itself, plus whoever it belongs to, is what tells us. Everything from here on (where the
    // tokens get filed, whose EventSub subscription gets created) is keyed off that id, so two
    // different streamers each running this same flow never collide.
    var broadcasterId = await TwitchApi.ResolveBroadcasterIdAsync(httpClient, accessToken, twitchClientId);
    if (broadcasterId is null)
        return Results.Text("Authorized, but could not resolve your Twitch user id - Channel Points redemptions won't be picked up yet.", statusCode: StatusCodes.Status502BadGateway);

    await tokenStore.SaveAsync(broadcasterId, accessToken, refreshToken, expiresIn);

    // Webhook-transport EventSub subscriptions must be created with an app access token, not the
    // broadcaster's own user token above (Twitch rejects it: "auth must use app access token to
    // create webhook subscription") - the broadcaster is still identified via `condition`, this
    // just changes whose token authorizes the *creation* of the subscription.
    var appAccessToken = await TwitchApi.GetAppAccessTokenAsync(httpClient, twitchClientId, twitchClientSecret);
    if (appAccessToken is null)
        return Results.Text("Authorized, but could not obtain an app access token to create the EventSub subscription.", statusCode: StatusCodes.Status502BadGateway);

    var webhookSecret = await webhookSecretStore.GetOrCreateAsync();
    var callbackUrl = new Uri(new Uri(oauthRedirectUri), "/eventsub/callback").ToString();
    var (ok, status) = await TwitchApi.EnsureRedemptionSubscriptionsAsync(httpClient, appAccessToken, twitchClientId, broadcasterId, callbackUrl, webhookSecret);

    // This broadcaster's status just changed - drop the cached answer so config.html doesn't keep
    // showing an Authorize button they have already finished with.
    eventSubStatusCache.TryRemove(broadcasterId, out _);

    return Results.Text(
        ok ? $"Authorized as broadcaster {broadcasterId}. EventSub subscription: {status}. Channel Points redemptions are live."
           : $"Authorized, but the EventSub subscription could not be created: {status}",
        statusCode: ok ? StatusCodes.Status200OK : StatusCodes.Status502BadGateway);
});

// Lets config.html show "Channel Points authorized" vs an Authorize button, using the
// broadcaster's numeric id Twitch's own postMessage bridge already hands it (Twitch.ext.onAuthorized's
// auth.channelId) - no separate lookup needed on the extension side.
app.MapGet("/api/eventsub-status/{broadcasterId}", async (string broadcasterId) =>
{
    if (string.IsNullOrEmpty(twitchClientId) || string.IsNullOrEmpty(twitchClientSecret) || string.IsNullOrEmpty(oauthRedirectUri))
        return Results.Json(new { authorized = false, configured = false });

    if (!broadcasterIdPattern.IsMatch(broadcasterId))
        return Results.BadRequest();

    // Nothing authenticates this route - it only ever reports whether a subscription exists, and
    // config.html asks before the broadcaster has anything to authenticate with. What it *does*
    // do is spend two Helix calls, out of a quota shared by every streamer on this relay, and
    // those same Helix calls are what the Bits and Channel Points paths need to look prices up.
    // So the answer is cached briefly per broadcaster: hammering this can no longer starve the
    // credit paths of the quota they depend on.
    if (eventSubStatusCache.TryGetValue(broadcasterId, out var cachedStatus)
        && DateTime.UtcNow - cachedStatus.CheckedUtc < eventSubStatusTtl)
    {
        // The delivery-rejection counters are read live rather than from the cache: they are the
        // signal that something has gone wrong since, and a stale zero would hide exactly that.
        return Results.Json(cachedStatus.Answer with
        {
            RejectedDeliveries = signatureFailures,
            LastRejectedDeliveryUtc = lastSignatureFailureUtc
        });
    }

    var appAccessToken = await TwitchApi.GetAppAccessTokenAsync(httpClient, twitchClientId, twitchClientSecret);
    if (appAccessToken is null)
        return Results.StatusCode(StatusCodes.Status502BadGateway);

    var callbackUrl = new Uri(new Uri(oauthRedirectUri), "/eventsub/callback").ToString();
    var existing = await TwitchApi.FindRedemptionSubscriptionsAsync(httpClient, appAccessToken, twitchClientId, broadcasterId, callbackUrl);
    if (existing is null)
        return Results.StatusCode(StatusCodes.Status502BadGateway);

    // Authorized means *both* subscriptions are in place. A streamer who set this up before
    // refunds were handled has only the first, and reads as partial rather than authorized -
    // which is what prompts them to re-authorize and pick up the second one.
    var missing = TwitchApi.RedemptionTypes.Where(type => !existing.ContainsKey(type)).ToArray();
    var status = existing.TryGetValue(TwitchApi.RedemptionAddType, out var addSub) ? addSub.Status : null;
    var answer = new EventSubStatus(
        Authorized: missing.Length == 0,
        Configured: true,
        Status: status,
        Missing: missing,
        RedemptionsCredit: existing.ContainsKey(TwitchApi.RedemptionAddType),
        RefundsClawBack: existing.ContainsKey(TwitchApi.RedemptionUpdateType),
        RejectedDeliveries: signatureFailures,
        LastRejectedDeliveryUtc: lastSignatureFailureUtc);

    eventSubStatusCache[broadcasterId] = (answer, DateTime.UtcNow);
    return Results.Json(answer);
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
    byte[] expectedBytes;
    using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(webhookSecret)))
        expectedBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(messageId + timestamp + rawBody));
    var expected = "sha256=" + Convert.ToHexString(expectedBytes).ToLowerInvariant();

    if (signature.Length != expected.Length
        || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(signature)))
    {
        // Counted, not attributed: the body is unverified, so nothing in it - including which
        // channel it claims to be for - is worth believing. A bare count is still enough to tell
        // a streamer "deliveries are being rejected, re-authorize", which is the actionable part.
        Interlocked.Increment(ref signatureFailures);
        lastSignatureFailureUtc = DateTime.UtcNow;
        app.Logger.LogWarning(
            "Rejected an EventSub delivery whose signature did not verify. If Channel Points "
            + "redemptions have stopped crediting, this relay's webhook secret no longer matches "
            + "the one Twitch holds - the streamer needs to re-authorize, which re-creates the "
            + "subscription with the current secret.");
        return Results.Unauthorized();
    }

    // The signature covers the timestamp, so a genuine-but-old delivery still verifies forever -
    // which is all a replay needs. Twitch's own guidance is to reject anything more than ten
    // minutes old, and that is also what makes the seen-ids set below finite: nothing outside
    // this window has to be remembered, because nothing outside it is accepted.
    if (!DateTimeOffset.TryParse(timestamp, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var sentAt)
        || DateTimeOffset.UtcNow - sentAt > eventSubMaxAge
        || sentAt - DateTimeOffset.UtcNow > TimeSpan.FromMinutes(1))
        return Results.Unauthorized();

    JsonDocument doc;
    try { doc = JsonDocument.Parse(rawBody); }
    catch (JsonException) { return Results.BadRequest(); }
    using var notificationScope = doc;
    var root = doc.RootElement;
    if (root.ValueKind != JsonValueKind.Object)
        return Results.BadRequest();

    if (messageType == "webhook_callback_verification")
        return Results.Text(root.TryGetProperty("challenge", out var challengeEl) ? (challengeEl.GetString() ?? "") : "", "text/plain");

    if (messageType != "notification")
        return Results.Ok(); // revocation, or a message type this relay doesn't act on

    // Twitch retries deliveries and can send the same notification more than once - a message
    // already handled must not credit its tokens again. Claimed for as long as the freshness
    // window above would still accept it, and on disk, so a restart in between doesn't reopen it.
    if (!await seenEventSubMessageIds.TryClaimAsync(messageId, DateTime.UtcNow + eventSubMaxAge + TimeSpan.FromMinutes(1)))
        return Results.Ok();

    if (!root.TryGetProperty("subscription", out var subEl) || !subEl.TryGetProperty("type", out var subTypeEl))
        return Results.Ok();
    var subscriptionType = subTypeEl.GetString() ?? "";
    if (subscriptionType is not (TwitchApi.RedemptionAddType or TwitchApi.RedemptionUpdateType))
        return Results.Ok();

    if (!root.TryGetProperty("event", out var ev) || ev.ValueKind != JsonValueKind.Object)
        return Results.Ok();

    // Twitch's own id for this redemption, the same across the add and any later update - the
    // only thing tying a refund back to what it originally paid out.
    var redemptionId = ev.TryGetProperty("id", out var redemptionIdEl) ? (redemptionIdEl.GetString() ?? "") : "";

    // ---- A redemption the streamer refunded: take back exactly what it credited ----
    if (subscriptionType == TwitchApi.RedemptionUpdateType)
    {
        // FULFILLED means the streamer accepted it - nothing to undo. Only a cancellation gives
        // the points back, and only then should the tokens go back too. (Twitch sends these
        // upper-cased on the update event and lower-cased on the add, hence the loose compare.)
        var updatedStatus = ev.TryGetProperty("status", out var statusEl) ? (statusEl.GetString() ?? "") : "";
        if (!string.Equals(updatedStatus, "canceled", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(updatedStatus, "cancelled", StringComparison.OrdinalIgnoreCase))
            return Results.Ok();

        // Taken, not read: a redemption can only be refunded once, so the record goes away with
        // it. Nothing to take back means the redemption never credited anything in the first
        // place - some other reward, or one from before this relay was keeping the ledger.
        var credited = await redemptionLedger.TakeAsync(redemptionId);
        if (credited is null)
            return Results.Ok();

        await balanceStore.DebitAsync(balanceKey(credited.BroadcasterId, credited.UserId), credited.Tokens);
        return Results.Ok();
    }

    if (!ev.TryGetProperty("reward", out var rewardEl) || rewardEl.ValueKind != JsonValueKind.Object)
        return Results.Ok();

    var rewardTitle = rewardEl.TryGetProperty("title", out var titleEl) ? (titleEl.GetString() ?? "") : "";
    // TryGetInt32, not GetInt32: the latter throws - not a JsonException - on a number that
    // doesn't fit an int, and an unhandled throw on a public webhook endpoint is a 500 anyone
    // who can reach it can trigger.
    var rewardCost = rewardEl.TryGetProperty("cost", out var costEl) && costEl.ValueKind == JsonValueKind.Number
        && costEl.TryGetInt32(out var parsedCost) ? parsedCost : 0;
    var userId = ev.TryGetProperty("user_id", out var userIdEl) ? (userIdEl.GetString() ?? "") : "";
    // Twitch hands this over directly on every redemption event - unlike the streamKey-originated
    // routes below, this path never needs to resolve a login to an id at all, and it's this field
    // (not any assumption about "the" broadcaster) that tells several streamers' redemptions apart.
    var broadcasterId = ev.TryGetProperty("broadcaster_user_id", out var bIdEl) ? (bIdEl.GetString() ?? "") : "";

    if (tokenPriceCache is null || userId.Length == 0 || rewardCost <= 0 || broadcasterId.Length == 0)
        return Results.Ok();

    var prices = await tokenPriceCache.GetPricesAsync(httpClient, broadcasterId);
    if (prices is null || prices.PointsPerToken <= 0 || prices.RewardName.Trim().Length == 0)
        return Results.Ok(); // Channel Points isn't priced/named in this broadcaster's config.html yet

    // Matched by name, not a stored id - the one Custom Reward that sells tokens just has to be
    // titled whatever she typed into config.html's "Token Reward Name" field.
    if (!string.Equals(rewardTitle.Trim(), prices.RewardName.Trim(), StringComparison.OrdinalIgnoreCase))
        return Results.Ok(); // some other reward on her channel, unrelated to tokens

    // Rounds down: a reward priced at, say, 150 points against a 100-points-per-token rate
    // credits 1 token, not 1.5 - the leftover is the cost of a price that doesn't divide evenly,
    // same as change a vending machine doesn't give back.
    var tokens = rewardCost / prices.PointsPerToken;
    if (tokens > 0)
    {
        try
        {
            // The streamer's token ceiling applies here and *only* here. Channel Points are
            // earned by watching, so a redemption that can't fit under the ceiling costs the
            // viewer nothing real - whereas Bits are money, and refusing part of a purchase
            // would mean taking payment for tokens never handed over. So Bits credit in full
            // and can carry a viewer past the ceiling; points then simply stop crediting until
            // they have spent back down under it.
            var credited = await balanceStore.CreditUpToAsync(
                balanceKey(broadcasterId, userId), tokens, prices.MaxTokenBalance);

            // What was actually given, not what was asked for - a refund of a redemption that
            // only half fit must take back only the half that landed.
            await redemptionLedger.RecordAsync(redemptionId, broadcasterId, userId, credited, DateTime.UtcNow + refundWindow);
        }
        catch (IOException)
        {
            // The message id was claimed before the credit was written, so a retry from Twitch
            // would otherwise be turned away as a duplicate - and the viewer would have spent
            // their points for nothing. Give the id back and answer with something Twitch will
            // retry rather than a 200 that ends the matter.
            await seenEventSubMessageIds.ReleaseAsync(messageId);
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }

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
    // Whether *this* stream key is even known to us is checked before whether Twitch is
    // configured on the relay at all - an unknown/disconnected stream key should read as "not
    // found", not the same generic "not configured" a viewer of a properly connected stream
    // would see if she genuinely hasn't set prices up yet.
    if (!streamKeyToBroadcasterLogin.TryGetValue(streamKey, out var login))
        return Results.NotFound();
    if (extensionSecret is null)
        return Results.Text("Bits isn't configured on this relay yet - set TWITCH_EXTENSION_SECRET (see CLAUDE.md).", statusCode: StatusCodes.Status501NotImplemented);
    if (tokenPriceCache is null)
        return Results.Text("Token pricing isn't configured on this relay yet - set TWITCH_EXTENSION_CLIENT_ID and TWITCH_EXTENSION_CLIENT_SECRET (see CLAUDE.md).", statusCode: StatusCodes.Status501NotImplemented);

    var broadcasterId = await tokenPriceCache.ResolveBroadcasterIdAsync(httpClient, login);
    if (broadcasterId is null)
        return Results.StatusCode(StatusCodes.Status502BadGateway);

    var prices = await tokenPriceCache.GetPricesAsync(httpClient, broadcasterId);
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
        if (root.ValueKind != JsonValueKind.Object)
            return Results.BadRequest();
        if (!root.TryGetProperty("receipts", out var receiptsEl) || receiptsEl.ValueKind != JsonValueKind.Array)
            return Results.BadRequest();

        // A receipt verifies against the *extension's* signing secret, which is one value shared
        // by every channel the extension runs on - so "this signature is genuine" says nothing
        // about *whose channel* the Bits were spent on. Without binding the purchase to a
        // channel, a viewer could spend Bits on a streamer who prices tokens dearly and cash the
        // receipt in against a different streamer on this same relay who prices them cheaply.
        // The viewer's own onAuthorized token is what supplies that binding: Twitch issues it
        // per channel, and its channel_id is signed.
        var purchaseAuth = root.TryGetProperty("authToken", out var purchaseAuthEl) && purchaseAuthEl.ValueKind == JsonValueKind.String
            ? purchaseAuthEl.GetString() ?? ""
            : "";
        var purchaser = tryGetViewerIdentity(purchaseAuth, extensionSecret);
        if (purchaser is null || purchaser.Value.ChannelId != broadcasterId)
            return Results.Text("Could not verify which channel this purchase was made on.", statusCode: StatusCodes.Status401Unauthorized);

        var verifiedTotal = 0;
        string? userId = null;
        // Who the receipts say paid, whether or not anything new came of them - a retry of an
        // already-credited purchase still has to report the right viewer's balance back, and a
        // viewer who never shared her identity has no user id anywhere except in the receipt.
        string? receiptUserId = null;
        var claimed = new List<string>();
        var alreadyCredited = 0;
        var considered = 0;
        foreach (var receiptEl in receiptsEl.EnumerateArray())
        {
            // One purchase is one receipt in practice; the cap is there so an arbitrarily long
            // array can't be used to make this endpoint do unbounded HMAC work, nor to sum its
            // way past int.MaxValue.
            if (++considered > maxReceiptsPerPurchase)
                break;
            if (receiptEl.ValueKind != JsonValueKind.String)
                continue;
            var verified = BitsReceipt.TryVerify(receiptEl.GetString() ?? "", extensionSecret);
            if (verified is null)
                continue;
            // Whoever is asking must be who paid, whenever the request can say who is asking -
            // a viewer who hasn't shared their identity has no user_id in her token, and the
            // receipt's own id is then the only one there is. Otherwise a receipt seen once -
            // they are handed to the browser, not kept server-side - could be cashed in by
            // someone else.
            if (verified.UserId.Length == 0)
                continue;
            if (purchaser.Value.UserId.Length > 0 && verified.UserId != purchaser.Value.UserId)
                continue;
            // Some receipt shapes carry the channel themselves; when one does, it has to agree
            // with the channel the viewer is actually watching.
            if (verified.ChannelId.Length > 0 && verified.ChannelId != broadcasterId)
                continue;
            receiptUserId ??= verified.UserId;
            // A transaction id can only ever pay for one credit - otherwise the same receipt
            // could be replayed to top up the balance again for free. Recorded on disk until
            // after the receipt's own expiry, so neither a restart nor the passage of time
            // reopens the window.
            if (!await usedBitsTransactionIds.TryClaimAsync(verified.TransactionId, verified.ExpiresAtUtc.AddMinutes(5)))
            {
                // Genuine, theirs, and already paid out. That is the normal shape of a retry -
                // the extension holds onto a receipt until this relay confirms it, so a purchase
                // whose first POST was lost gets sent again - and it has to read as settled, not
                // as an error, or the client would retry it forever.
                alreadyCredited++;
                continue;
            }
            claimed.Add(verified.TransactionId);
            verifiedTotal += verified.Amount;
            userId ??= verified.UserId; // every receipt in one purchase is the same viewer
        }

        if (userId is null || verifiedTotal <= 0)
        {
            foreach (var id in claimed)
                await usedBitsTransactionIds.ReleaseAsync(id);

            // Nothing new to credit, but every receipt offered was one this relay had already
            // honoured - so the viewer is square, and says so with the balance they ended up
            // with. Nothing is credited twice to get here: the claim above is what refused it.
            if (alreadyCredited > 0 && receiptUserId is not null)
                return Results.Json(new { credited = 0, duplicate = true, balance = await balanceStore.GetBalanceAsync(balanceKey(broadcasterId, receiptUserId)) });

            return Results.BadRequest();
        }

        // Rounds down: paying for 250 bits at 100-bits-per-token credits 2 tokens, not 2.5 - the
        // leftover isn't refunded, same as the reward-redemption side in /eventsub/callback.
        var tokens = verifiedTotal / prices.BitsPerToken;
        try
        {
            var balance = await balanceStore.CreditAsync(balanceKey(broadcasterId, userId), tokens);
            return Results.Json(new { credited = tokens, duplicate = false, balance });
        }
        catch (IOException)
        {
            // The receipts were marked spent before the credit was written; if the write failed,
            // hand them back rather than leave a viewer having paid for nothing they can retry.
            foreach (var id in claimed)
                await usedBitsTransactionIds.ReleaseAsync(id);
            throw;
        }
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
    // Whether *this* stream key is even known to us is checked before whether Twitch is
    // configured on the relay at all - an unknown/disconnected stream key should read as "not
    // found", not the same generic "not configured" a viewer of a properly connected stream
    // would see if she genuinely hasn't set this up yet.
    if (!streamKeyToControlKey.TryGetValue(streamKey, out var controlKey) || !commandWriters.TryGetValue(controlKey, out var writer))
        return Results.NotFound();
    if (!snapshots.TryGetValue(streamKey, out var snapshot))
        return Results.NotFound();
    if (!streamKeyToBroadcasterLogin.TryGetValue(streamKey, out var login))
        return Results.NotFound();
    if (extensionSecret is null)
        return Results.Text("Summoning isn't configured on this relay yet - set TWITCH_EXTENSION_SECRET (see CLAUDE.md).", statusCode: StatusCodes.Status501NotImplemented);
    if (tokenPriceCache is null)
        return Results.Text("Token pricing isn't configured on this relay yet - set TWITCH_EXTENSION_CLIENT_ID and TWITCH_EXTENSION_CLIENT_SECRET (see CLAUDE.md).", statusCode: StatusCodes.Status501NotImplemented);

    var broadcasterId = await tokenPriceCache.ResolveBroadcasterIdAsync(httpClient, login);
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
        var viewer = tryGetViewerIdentity(authToken, extensionSecret);
        if (viewer is null)
            return Results.Text("Could not verify your Twitch identity - share it with the extension to spend tokens.", statusCode: StatusCodes.Status401Unauthorized);
        // The signing secret is extension-wide, so a token minted on any channel verifies here.
        // Requiring the channel it was issued for to be the one being summoned into keeps a
        // token from one channel from acting on another's game.
        if (viewer.Value.ChannelId != broadcasterId)
            return Results.Text("That Twitch session belongs to a different channel.", statusCode: StatusCodes.Status401Unauthorized);
        if (viewer.Value.UserId.Length == 0)
            return Results.Text("Share your Twitch identity with the extension to spend tokens.", statusCode: StatusCodes.Status401Unauthorized);
        var key = balanceKey(broadcasterId, viewer.Value.UserId);

        if (!root.TryGetProperty("packs", out var packsEl) || packsEl.ValueKind != JsonValueKind.Array)
            return Results.BadRequest();

        var requestedIds = new List<string>();
        foreach (var item in packsEl.EnumerateArray())
        {
            // Truncated here, before anything is costed - buildSummonCommand only ever forwards
            // the first maxPacksPerSummon ids, so charging for a longer list would bill a viewer
            // for packs the overlay is never told to spawn.
            if (requestedIds.Count >= maxPacksPerSummon)
                break;
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
            // The overlay drops packs outside the protagonist's level band on arrival
            // (TwitchRelayClient.enqueueById), so charging for one here would take tokens for a
            // summon that provably never happens. Dropped on the same terms as an unknown id.
            if (!pack.Available)
                continue;
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
    if (!streamKeyToBroadcasterLogin.TryGetValue(streamKey, out var login))
        return Results.NotFound();
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

    var viewer = tryGetViewerIdentity(authToken ?? "", extensionSecret);
    if (viewer is null)
        return Results.Text("Could not verify your Twitch identity.", statusCode: StatusCodes.Status401Unauthorized);

    var broadcasterId = await tokenPriceCache.ResolveBroadcasterIdAsync(httpClient, login);
    if (broadcasterId is null)
        return Results.StatusCode(StatusCodes.Status502BadGateway);

    if (viewer.Value.ChannelId != broadcasterId)
        return Results.Text("That Twitch session belongs to a different channel.", statusCode: StatusCodes.Status401Unauthorized);
    if (viewer.Value.UserId.Length == 0)
        return Results.Text("Could not verify your Twitch identity.", statusCode: StatusCodes.Status401Unauthorized);

    return Results.Json(new { balance = await balanceStore.GetBalanceAsync(balanceKey(broadcasterId, viewer.Value.UserId)) });
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
//
// channel_id comes back with it and every caller checks it, because the signature alone cannot:
// the extension's signing secret is a single relay-wide value (see CLAUDE.md), so a token issued
// on *any* channel running this extension verifies here. Which channel it was issued for is the
// only thing in the token that distinguishes one streamer's viewer from another's.
//
// UserId comes back empty for a viewer who hasn't shared their identity yet - buying is still
// allowed in that state (the receipt carries the real id, and spending will ask for identity
// later), so only the callers that actually spend or read a balance insist on it.
static (string UserId, string ChannelId)? tryGetViewerIdentity(string authToken, byte[] extensionSecret)
{
    if (string.IsNullOrEmpty(authToken))
        return null;

    var payload = Jwt.TryVerifyAndDecode(authToken, extensionSecret);
    if (payload is null)
        return null;

    var userId = payload.Value.TryGetProperty("user_id", out var idEl) ? (idEl.GetString() ?? "") : "";
    var channelId = payload.Value.TryGetProperty("channel_id", out var chanEl) ? chanEl.GetString() : null;
    if (string.IsNullOrEmpty(channelId))
        return null;

    return (userId, channelId);
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
static List<(string Id, string Name, int Cost, bool Available)> readPacksFromSnapshot(string snapshotJson)
{
    var result = new List<(string, string, int, bool)>();
    try
    {
        using var doc = JsonDocument.Parse(snapshotJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            return result;
        if (!doc.RootElement.TryGetProperty("packs", out var packsEl) || packsEl.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var p in packsEl.EnumerateArray())
        {
            if (p.ValueKind != JsonValueKind.Object)
                continue;
            var id = p.TryGetProperty("id", out var idEl) ? (idEl.GetString() ?? "") : "";
            var name = p.TryGetProperty("name", out var nameEl) ? (nameEl.GetString() ?? "") : "";
            // TryGetInt32 rather than GetInt32: the latter throws on a number that doesn't fit,
            // and that throw is not a JsonException, so it would escape this catch entirely.
            // A price below zero is refused outright rather than clamped - TrySpendAsync treats
            // any non-positive amount as free, so a negative total is a free summon, and one
            // negative pack in a combo pays for the rest of it.
            var cost = p.TryGetProperty("cost", out var costEl) && costEl.ValueKind == JsonValueKind.Number
                && costEl.TryGetInt32(out var parsedCost) && parsedCost >= 0 ? parsedCost : -1;
            var available = !p.TryGetProperty("available", out var availEl) || availEl.ValueKind != JsonValueKind.False;
            if (id.Length > 0 && cost >= 0)
                result.Add((id, name, cost, available));
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

// broadcasterLogin is optional in the handshake JSON (older overlay builds won't send it) -
// missing or empty just means this relay can't credit/spend tokens for this stream key yet,
// not that the handshake itself failed; the control key is still what makes the connection real.
static bool tryReadHandshake(string json, out string controlKey, out string broadcasterLogin)
{
    controlKey = "";
    broadcasterLogin = "";
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

        if (document.RootElement.TryGetProperty("broadcasterLogin", out var loginElement)
            && loginElement.ValueKind == JsonValueKind.String)
        {
            broadcasterLogin = (loginElement.GetString() ?? "").Trim().ToLowerInvariant();
        }

        return controlKey.Length > 0;
    }
    catch (JsonException)
    {
        return false;
    }
}

/// <summary>What /api/eventsub-status answers with - see the note at its registration.</summary>
internal sealed record EventSubStatus(
    bool Authorized,
    bool Configured,
    string? Status,
    string[] Missing,
    bool RedemptionsCredit,
    bool RefundsClawBack,
    int RejectedDeliveries,
    DateTime? LastRejectedDeliveryUtc);
