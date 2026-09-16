using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TwitchRelay.Tests;

/// <summary>
/// One relay, wired to the stub Twitch instead of the real one, with its own data directory so
/// no test can see another's balances. Disposing and re-creating one against the same data
/// directory is how a redeploy is modelled - which is the whole point of several of these tests,
/// since a restart is exactly what used to reopen the replay window on a Bits receipt.
/// </summary>
public sealed class RelayHarness : IAsyncDisposable
{
    // The stub is process-wide because the relay reads the Twitch base URLs into statics at
    // startup; tests run serially (see AssemblyInfo) so sharing it is safe.
    private static FakeTwitch? sharedTwitch;
    private static readonly SemaphoreSlim twitchGate = new(1, 1);

    public const string StreamKey = "streamkey0001";
    public const string ControlKey = "controlkey0001";
    public const string BroadcasterLogin = "streamer";
    public const string BroadcasterId = "12345";
    public const string ViewerId = "99001";
    public const int BitsPerToken = 100;
    public const int PointsPerToken = 500;
    public const string RewardName = "Summon Token";

    /// <summary>
    /// The secret Twitch signs EventSub deliveries with. The relay normally picks this for itself
    /// on first use and keeps it on disk; seeding the file up front is what lets a test sign a
    /// notification the relay will accept - and is a reminder that losing that file silently
    /// breaks every subscription already registered with Twitch.
    /// </summary>
    public const string WebhookSecret = "0123456789abcdef0123456789abcdef";

    /// <summary>Where the relay is told its own /oauth/callback lives.</summary>
    public const string OAuthRedirectUri = "https://relay.test/oauth/callback";
    public const string EventSubCallbackUrl = "https://relay.test/eventsub/callback";

    /// <summary>The extension's JWT signing secret, as the relay is configured with it.</summary>
    public static readonly byte[] ExtensionSecret = Encoding.UTF8.GetBytes("extension-signing-secret-32-bytes");

    /// <summary>A *different* valid-looking secret, for forgery tests.</summary>
    public static readonly byte[] WrongSecret = Encoding.UTF8.GetBytes("a-secret-that-is-not-the-real-one");

    private readonly WebApplicationFactory<Program> factory;

    public FakeTwitch Twitch { get; }
    public HttpClient Client { get; }
    public string DataDir { get; }

    private RelayHarness(WebApplicationFactory<Program> factory, FakeTwitch twitch, string dataDir)
    {
        this.factory = factory;
        Twitch = twitch;
        DataDir = dataDir;
        // AllowAutoRedirect off: /oauth/authorize answers with a redirect to Twitch, and that
        // redirect - specifically the state it carries - is the thing under test, not wherever it
        // points. Following it would just fetch the stub's 404.
        Client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    public static async Task<RelayHarness> StartAsync(string? dataDir = null)
    {
        await twitchGate.WaitAsync();
        try
        {
            if (sharedTwitch is null)
            {
                sharedTwitch = await FakeTwitch.StartAsync();
                Environment.SetEnvironmentVariable("TWITCH_HELIX_BASE_URL", sharedTwitch.BaseUrl + "/helix");
                Environment.SetEnvironmentVariable("TWITCH_ID_BASE_URL", sharedTwitch.BaseUrl);
            }
        }
        finally
        {
            twitchGate.Release();
        }

        sharedTwitch.Logins[BroadcasterLogin] = BroadcasterId;
        // Whoever completes /oauth/callback is this broadcaster, unless a test says otherwise.
        sharedTwitch.AuthenticatedUserId = BroadcasterId;
        sharedTwitch.Subscriptions.Clear();
        sharedTwitch.SetPrices(BroadcasterId, BitsPerToken, PointsPerToken, RewardName);

        dataDir ??= Path.Combine(Path.GetTempPath(), "relay-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);

        // Seeded rather than generated, so the tests can sign what Twitch would sign.
        var secretPath = Path.Combine(dataDir, "eventsub-secret.txt");
        if (!File.Exists(secretPath))
            File.WriteAllText(secretPath, WebhookSecret);

        Environment.SetEnvironmentVariable("RELAY_DATA_DIR", dataDir);
        Environment.SetEnvironmentVariable("TWITCH_EXTENSION_SECRET", Convert.ToBase64String(ExtensionSecret));
        Environment.SetEnvironmentVariable("TWITCH_EXTENSION_CLIENT_ID", "fake-extension-client");
        Environment.SetEnvironmentVariable("TWITCH_EXTENSION_CLIENT_SECRET", "fake-extension-secret");
        // The separate OAuth "Application" the Channel Points path needs - a different credential
        // from the extension's own, see CLAUDE.md.
        Environment.SetEnvironmentVariable("TWITCH_CLIENT_ID", "fake-app-client");
        Environment.SetEnvironmentVariable("TWITCH_CLIENT_SECRET", "fake-app-client-secret");
        Environment.SetEnvironmentVariable("TWITCH_OAUTH_REDIRECT_URI", OAuthRedirectUri);

        var factory = new WebApplicationFactory<Program>();
        return new RelayHarness(factory, sharedTwitch, dataDir);
    }

    /// <summary>
    /// Sets this harness's broadcaster to the default prices plus a token ceiling. Must be called
    /// before anything reads prices for them, since the relay caches the first read.
    /// </summary>
    public void SetTokenLimit(int maxTokenBalance)
        => Twitch.SetPrices(BroadcasterId, BitsPerToken, PointsPerToken, RewardName, maxTokenBalance);

    /// <summary>A viewer's onAuthorized token for this harness's channel, unless told otherwise.</summary>
    public static string ViewerToken(string? channelId = null, string? userId = null)
        => FakeTwitch.ViewerToken(ExtensionSecret, channelId ?? BroadcasterId, userId ?? ViewerId);

    public async Task<FakeOverlay> ConnectOverlayAsync(
        string streamKey = StreamKey, string controlKey = ControlKey, string? login = BroadcasterLogin)
    {
        var client = factory.Server.CreateWebSocketClient();
        var uri = new UriBuilder(factory.Server.BaseAddress) { Scheme = "ws", Path = "/ws/ingest/" + streamKey }.Uri;
        var socket = await client.ConnectAsync(uri, CancellationToken.None);
        var overlay = new FakeOverlay(socket, this, streamKey);
        if (login is not null)
            await overlay.HandshakeAsync(controlKey, login);
        return overlay;
    }

    public Task<HttpResponseMessage> PostBitsAsync(object body, string streamKey = StreamKey)
        => Client.PostAsJsonAsync("/api/bits-purchase/" + streamKey, body);

    public Task<HttpResponseMessage> PostSummonAsync(object body, string streamKey = StreamKey)
        => Client.PostAsJsonAsync("/api/summon/" + streamKey, body);

    /// <summary>
    /// Delivers an EventSub notification the way Twitch would - signed over
    /// (message id + timestamp + body), which is the only thing this public endpoint can
    /// authenticate on. Every parameter is overridable precisely so the tests can get it wrong.
    /// </summary>
    public Task<HttpResponseMessage> PostEventSubAsync(
        string body,
        string messageType = "notification",
        string? messageId = null,
        DateTimeOffset? timestamp = null,
        string? secret = null,
        string? signature = null)
    {
        messageId ??= Guid.NewGuid().ToString("N");
        var stamp = (timestamp ?? DateTimeOffset.UtcNow).ToString("O");

        var request = new HttpRequestMessage(HttpMethod.Post, "/eventsub/callback")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Twitch-Eventsub-Message-Type", messageType);
        request.Headers.Add("Twitch-Eventsub-Message-Id", messageId);
        request.Headers.Add("Twitch-Eventsub-Message-Timestamp", stamp);
        request.Headers.Add("Twitch-Eventsub-Message-Signature",
            signature ?? FakeTwitch.EventSubSignature(secret ?? WebhookSecret, messageId, stamp, body));

        return Client.SendAsync(request);
    }

    /// <summary>A viewer redeeming the streamer's token reward, delivered and signed.</summary>
    public Task<HttpResponseMessage> RedeemAsync(
        int points, string rewardTitle = RewardName, string? userId = null,
        string? broadcasterId = null, string? messageId = null, string? redemptionId = null)
        => PostEventSubAsync(
            FakeTwitch.RedemptionBody(broadcasterId ?? BroadcasterId, userId ?? ViewerId, rewardTitle, points,
                redemptionId: redemptionId),
            messageId: messageId);

    /// <summary>
    /// The streamer resolving a redemption that was sitting in their queue - refunding the points
    /// ("CANCELED") or accepting it ("FULFILLED"). Twitch reports this as a separate event naming
    /// only the redemption id.
    /// </summary>
    public Task<HttpResponseMessage> ResolveRedemptionAsync(
        string redemptionId, string status, string rewardTitle = RewardName, int points = 0,
        string? userId = null, string? broadcasterId = null, string? messageId = null)
        => PostEventSubAsync(
            FakeTwitch.RedemptionBody(broadcasterId ?? BroadcasterId, userId ?? ViewerId, rewardTitle, points,
                subscriptionType: "channel.channel_points_custom_reward_redemption.update",
                redemptionId: redemptionId, status: status),
            messageId: messageId);

    public async Task<int> GetBalanceAsync(string? authToken = null)
    {
        var response = await Client.PostAsJsonAsync("/api/balance/" + StreamKey, new { authToken = authToken ?? ViewerToken() });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("balance").GetInt32();
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await factory.DisposeAsync();
    }
}

/// <summary>
/// Stands in for the Radar app's own WebSocket connection - the handshake that claims a stream
/// key, the party/pack snapshots that tell the relay what a pack costs, and the commands that
/// come back down when a viewer spends tokens.
/// </summary>
public sealed class FakeOverlay : IAsyncDisposable
{
    private readonly WebSocket socket;
    private readonly RelayHarness harness;
    private readonly string streamKey;

    public FakeOverlay(WebSocket socket, RelayHarness harness, string streamKey)
    {
        this.socket = socket;
        this.harness = harness;
        this.streamKey = streamKey;
    }

    public Task HandshakeAsync(string controlKey, string login)
        => SendAsync(JsonSerializer.Serialize(new { control = controlKey, broadcasterLogin = login }));

    public Task SendAsync(string json)
        => socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);

    /// <summary>
    /// Sends if the socket still accepts it. A connection the relay has refused may already be
    /// torn down by the time a hostile test gets to write - "the write itself was rejected" is
    /// just as good an outcome as "the write was ignored", and either way what matters is what
    /// the relay is serving afterwards.
    /// </summary>
    public async Task TrySendAsync(string json)
    {
        try
        {
            await SendAsync(json);
        }
        catch (WebSocketException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Publishes a pack list in the shape TwitchRelayClient.buildPayload produces, then waits
    /// until the relay is actually serving it - the socket write returns before the relay has
    /// processed it, and every summon test depends on the snapshot being in place first.
    /// </summary>
    public async Task PublishPacksAsync(params (string Id, int Cost, bool Available)[] packs)
    {
        var json = JsonSerializer.Serialize(new
        {
            party = Array.Empty<object>(),
            level = 10,
            packs = packs.Select(p => new { id = p.Id, name = p.Id, from = 1, to = 40, cost = p.Cost, available = p.Available })
        });
        await SendAsync(json);
        await WaitForSnapshotAsync(json);
    }

    /// <summary>Publishes raw JSON as a snapshot - for the shapes a real overlay would never send.</summary>
    public async Task PublishRawAsync(string json)
    {
        await SendAsync(json);
        await WaitForSnapshotAsync(json);
    }

    private async Task WaitForSnapshotAsync(string expected)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var response = await harness.Client.GetAsync("/api/character/" + streamKey);
            if (response.IsSuccessStatusCode && await response.Content.ReadAsStringAsync() == expected)
                return;
            await Task.Delay(20);
        }
        throw new TimeoutException("The relay never served the snapshot that was just published.");
    }

    /// <summary>
    /// The next command the relay pushes down, or null if none arrives - "null" being the
    /// assertion that matters whenever a summon should have been refused.
    /// </summary>
    public async Task<string?> ReceiveCommandAsync(TimeSpan? within = null)
    {
        var buffer = new byte[8192];
        using var timeout = new CancellationTokenSource(within ?? TimeSpan.FromSeconds(2));
        try
        {
            var result = await socket.ReceiveAsync(buffer, timeout.Token);
            return Encoding.UTF8.GetString(buffer, 0, result.Count);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (WebSocketException)
        {
            return null;
        }
    }

    /// <summary>True once the relay has hung up on this connection - how a refused takeover reads.</summary>
    public async Task<bool> WasClosedByRelayAsync()
    {
        var buffer = new byte[256];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            var result = await socket.ReceiveAsync(buffer, timeout.Token);
            return result.MessageType == WebSocketMessageType.Close;
        }
        catch (OperationCanceledException) { return false; }
        catch (WebSocketException) { return true; }
        // TestServer's in-memory socket reports a hang-up this way rather than as a
        // WebSocketException; a real Kestrel connection would raise the one above.
        catch (IOException) { return true; }
        catch (ObjectDisposedException) { return true; }
    }

    public ValueTask DisposeAsync()
    {
        socket.Dispose();
        return ValueTask.CompletedTask;
    }
}
