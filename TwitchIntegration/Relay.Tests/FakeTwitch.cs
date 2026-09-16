using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
// The test project isn't a Web SDK project, so ASP.NET's implicit usings don't apply - the
// FrameworkReference makes these types available, but they still have to be named.
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TwitchRelay.Tests;

/// <summary>
/// Everything the relay's payment paths reach out to Twitch for, standing in one place: the
/// Helix calls that resolve a channel login and read what a streamer saved in config.html, and
/// the token signing that turns "a viewer paid" into something the relay will believe.
///
/// This exists because neither half can be exercised for real without spending actual Bits on an
/// actual channel. Both are pure enough to fake exactly: Helix is two JSON documents, and a Bits
/// receipt is an HS256 JWT signed with the extension secret - the same secret the relay is
/// configured with here, so a receipt minted below is indistinguishable from one Twitch issued.
/// Which is the point: the tests that matter are the ones where it *is* distinguishable, and the
/// relay has to say no.
/// </summary>
public sealed class FakeTwitch : IAsyncDisposable
{
    private readonly WebApplication app;

    /// <summary>Base URL of the stub, for TWITCH_HELIX_BASE_URL / TWITCH_ID_BASE_URL.</summary>
    public string BaseUrl { get; }

    /// <summary>login -> numeric broadcaster id, as Helix GET /users would answer.</summary>
    public Dictionary<string, string> Logins { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>broadcaster id -> the JSON config.html saved for them (token prices).</summary>
    public Dictionary<string, string> Configs { get; } = new();

    /// <summary>
    /// Who GET /helix/users answers for with no login - i.e. whoever the access token belongs to.
    /// That is how /oauth/callback learns which broadcaster just authorized: it is knowable only
    /// after exchanging the code, never before.
    /// </summary>
    public string AuthenticatedUserId { get; set; } = "";

    /// <summary>How many times Helix was called, so a test can prove the relay is caching.</summary>
    public int HelixCallCount;

    /// <summary>
    /// EventSub subscriptions the relay has asked Twitch to create - the Channel Points setup
    /// flow's only observable output, and what proves re-running it doesn't pile up duplicates
    /// (Twitch would otherwise deliver every redemption twice, and credit it twice).
    /// </summary>
    public List<CreatedSubscription> Subscriptions { get; } = new();

    public sealed record CreatedSubscription(string Id, string Type, string BroadcasterId, string Callback, string Secret);

    /// <summary>Subscriptions deleted over the API, so a test can see a re-create as a replace.</summary>
    public List<string> DeletedSubscriptionIds { get; } = new();

    private FakeTwitch(WebApplication app, string baseUrl)
    {
        this.app = app;
        BaseUrl = baseUrl;
    }

    public static async Task<FakeTwitch> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();

        FakeTwitch? self = null;

        // Serves the client_credentials grant (app access token) and the authorization_code
        // exchange the Channel Points setup flow runs. refresh_token is always present because
        // that is what Twitch does for a user grant; the app grant carrying one too is harmless.
        app.MapPost("/oauth2/token", () => Results.Json(new
        {
            access_token = "fake-app-token",
            refresh_token = "fake-refresh-token",
            expires_in = 3600
        }));

        // ---- EventSub, as the Channel Points setup flow uses it ----

        app.MapGet("/helix/eventsub/subscriptions", (HttpContext ctx) =>
        {
            Interlocked.Increment(ref self!.HelixCallCount);
            var type = ctx.Request.Query["type"].ToString();
            var matching = self.Subscriptions
                .Where(sub => type.Length == 0 || sub.Type == type)
                .Select(sub => new
                {
                    id = sub.Id,
                    type = sub.Type,
                    status = "enabled",
                    condition = new { broadcaster_user_id = sub.BroadcasterId },
                    transport = new { method = "webhook", callback = sub.Callback }
                });
            return Results.Json(new { data = matching });
        });

        app.MapPost("/helix/eventsub/subscriptions", async (HttpContext ctx) =>
        {
            Interlocked.Increment(ref self!.HelixCallCount);
            using var body = await JsonDocument.ParseAsync(ctx.Request.Body);
            var root = body.RootElement;
            var transport = root.GetProperty("transport");
            self.Subscriptions.Add(new CreatedSubscription(
                "sub-" + Guid.NewGuid().ToString("N")[..8],
                root.GetProperty("type").GetString() ?? "",
                root.GetProperty("condition").GetProperty("broadcaster_user_id").GetString() ?? "",
                transport.GetProperty("callback").GetString() ?? "",
                transport.GetProperty("secret").GetString() ?? ""));
            return Results.Json(new { data = Array.Empty<object>() });
        });

        app.MapDelete("/helix/eventsub/subscriptions", (HttpContext ctx) =>
        {
            Interlocked.Increment(ref self!.HelixCallCount);
            var id = ctx.Request.Query["id"].ToString();
            var removed = self.Subscriptions.RemoveAll(sub => sub.Id == id);
            if (removed == 0)
                return Results.NotFound();
            self.DeletedSubscriptionIds.Add(id);
            return Results.NoContent();
        });

        app.MapGet("/helix/users", (HttpContext ctx) =>
        {
            Interlocked.Increment(ref self!.HelixCallCount);
            var login = ctx.Request.Query["login"].ToString();
            if (login.Length == 0)
            {
                // No login: Helix answers for the bearer token's own user.
                return self.AuthenticatedUserId.Length == 0
                    ? Results.Json(new { data = Array.Empty<object>() })
                    : Results.Json(new { data = new[] { new { id = self.AuthenticatedUserId, login = "authorized-user" } } });
            }
            if (!self.Logins.TryGetValue(login, out var id))
                return Results.Json(new { data = Array.Empty<object>() });
            return Results.Json(new { data = new[] { new { id, login } } });
        });

        app.MapGet("/helix/extensions/configurations", (HttpContext ctx) =>
        {
            Interlocked.Increment(ref self!.HelixCallCount);
            var broadcasterId = ctx.Request.Query["broadcaster_id"].ToString();
            if (!self.Configs.TryGetValue(broadcasterId, out var content))
                return Results.Json(new { data = Array.Empty<object>() });
            return Results.Json(new { data = new[] { new { segment = "broadcaster", content } } });
        });

        await app.StartAsync();
        var address = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!
            .Addresses.First();

        self = new FakeTwitch(app, address);
        return self;
    }

    /// <summary>Prices this broadcaster the way config.html's saved content would.</summary>
    public void SetPrices(string broadcasterId, int bitsPerToken, int pointsPerToken = 0, string rewardName = "",
        int maxTokenBalance = 0)
        => Configs[broadcasterId] = JsonSerializer.Serialize(new { bitsPerToken, pointsPerToken, rewardName, maxTokenBalance });

    public async ValueTask DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
    }

    // ---- Token minting: the same HS256 Twitch uses, with whatever secret the caller passes ----

    /// <summary>
    /// A viewer's Twitch.ext.onAuthorized token. userId empty models a viewer who has not shared
    /// their identity - Twitch leaves user_id out entirely in that case.
    /// </summary>
    public static string ViewerToken(byte[] secret, string channelId, string userId, DateTimeOffset? expires = null)
    {
        var payload = new Dictionary<string, object>
        {
            ["channel_id"] = channelId,
            ["opaque_user_id"] = "U" + (userId.Length > 0 ? userId : "anon"),
            ["role"] = "viewer",
            ["exp"] = (expires ?? DateTimeOffset.UtcNow.AddHours(1)).ToUnixTimeSeconds()
        };
        if (userId.Length > 0)
            payload["user_id"] = userId;
        return Sign(payload, secret);
    }

    /// <summary>
    /// The receipt Twitch.ext.bits.onTransactionComplete hands the frontend. `includeExp: false`
    /// models the shape the relay must refuse outright - a signed token that never expires.
    /// </summary>
    public static string BitsReceiptToken(
        byte[] secret, string transactionId, string userId, int bitsAmount,
        DateTimeOffset? expires = null, bool includeExp = true, string sku = "tokens100",
        string topic = "bits_transaction_receipt")
    {
        var payload = new Dictionary<string, object>
        {
            ["topic"] = topic,
            ["data"] = new Dictionary<string, object>
            {
                ["transactionId"] = transactionId,
                ["time"] = DateTimeOffset.UtcNow.ToString("O"),
                ["userId"] = userId,
                ["productType"] = "BITS_IN_EXTENSION",
                ["product"] = new Dictionary<string, object>
                {
                    ["domainId"] = "twitch.ext.fake",
                    ["sku"] = sku,
                    ["displayName"] = bitsAmount + " Bits",
                    ["cost"] = new Dictionary<string, object> { ["amount"] = bitsAmount, ["type"] = "bits" }
                }
            }
        };
        if (includeExp)
            payload["exp"] = (expires ?? DateTimeOffset.UtcNow.AddHours(1)).ToUnixTimeSeconds();
        return Sign(payload, secret);
    }

    /// <summary>
    /// A Channel Points redemption exactly as Twitch delivers it. The relay has no way to tell
    /// this from the real thing - which is the point: the signature is the only thing standing
    /// between this endpoint and anyone on the internet, so it has to be the only thing that
    /// decides, and the tests that matter are the ones where it doesn't check out.
    /// </summary>
    public static string RedemptionBody(
        string broadcasterId, string userId, string rewardTitle, int rewardCost,
        string subscriptionType = "channel.channel_points_custom_reward_redemption.add",
        string? redemptionId = null, string status = "unfulfilled")
        => JsonSerializer.Serialize(new
        {
            subscription = new
            {
                id = "sub-1",
                type = subscriptionType,
                version = "1",
                status = "enabled",
                condition = new { broadcaster_user_id = broadcasterId }
            },
            @event = new
            {
                id = redemptionId ?? "redemption-" + Guid.NewGuid().ToString("N"),
                broadcaster_user_id = broadcasterId,
                broadcaster_user_login = "streamer",
                user_id = userId,
                user_login = "viewer",
                user_input = "",
                status,
                redeemed_at = DateTimeOffset.UtcNow.ToString("O"),
                reward = new { id = "reward-1", title = rewardTitle, prompt = "", cost = rewardCost }
            }
        });

    /// <summary>The HMAC Twitch puts in Twitch-Eventsub-Message-Signature.</summary>
    public static string EventSubSignature(string secret, string messageId, string timestamp, string body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(messageId + timestamp + body));
        return "sha256=" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string Sign(Dictionary<string, object> payload, byte[] secret)
    {
        var header = Base64Url(Encoding.UTF8.GetBytes("""{"alg":"HS256","typ":"JWT"}"""));
        var body = Base64Url(JsonSerializer.SerializeToUtf8Bytes(payload));
        using var hmac = new HMACSHA256(secret);
        var signature = Base64Url(hmac.ComputeHash(Encoding.ASCII.GetBytes(header + "." + body)));
        return header + "." + body + "." + signature;
    }

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
