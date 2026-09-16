using System.Text.Json;

namespace TwitchRelay;

/// <summary>
/// Where Twitch lives. Real Twitch unless overridden, and the override exists so the payment
/// paths can be exercised end to end against a stub - crediting Bits and reading a streamer's
/// prices both go through Helix, and there is otherwise no way to test either without spending
/// real Bits on a real channel. Read once at startup; leave both unset in production.
/// </summary>
public static class TwitchEndpoints
{
    public static readonly string Helix =
        (Environment.GetEnvironmentVariable("TWITCH_HELIX_BASE_URL") ?? "https://api.twitch.tv/helix").TrimEnd('/');

    public static readonly string Id =
        (Environment.GetEnvironmentVariable("TWITCH_ID_BASE_URL") ?? "https://id.twitch.tv").TrimEnd('/');
}

/// <summary>
/// Persists each broadcaster's OAuth tokens for the Channel Points / EventSub path - one entry
/// per streamer who has completed GET /oauth/authorize -> /oauth/callback, keyed by their numeric
/// Twitch id (stable, unlike a stream key they can regenerate). Every streamer this relay serves
/// does that flow independently against the *same* registered Application below and ends up with
/// their own row here; refreshed automatically, per broadcaster, before each one's tokens expire.
///
/// This is a *different* Twitch application from the Extension itself: Extensions get their own
/// client id (see CLAUDE.md) meant only for the Extensions Manager's own flows - onAuthorized,
/// configuration.set(), Bits. Calling Helix / creating an EventSub subscription on a broadcaster's
/// behalf needs a normal OAuth "Application" registered separately, with its own client id/secret
/// (TWITCH_CLIENT_ID / TWITCH_CLIENT_SECRET) and an OAuth Redirect URL pointing at this relay's
/// /oauth/callback - one Application, shared by every broadcaster who authorizes against it.
/// </summary>
public sealed class TokenStore
{
    private readonly string path;
    private readonly SemaphoreSlim gate = new(1, 1);
    private Dictionary<string, StoredTokens>? cached;

    public TokenStore(string path)
    {
        this.path = path;
    }

    private sealed class StoredTokens
    {
        public string AccessToken { get; set; } = "";
        public string RefreshToken { get; set; } = "";
        public DateTime ExpiresAtUtc { get; set; }
    }

    public async Task SaveAsync(string broadcasterId, string accessToken, string refreshToken, int expiresInSeconds)
    {
        await gate.WaitAsync();
        try
        {
            var all = await loadAsync();
            all[broadcasterId] = new StoredTokens
            {
                AccessToken = accessToken,
                RefreshToken = refreshToken,
                // A minute of slack so a call starting just before expiry doesn't fail mid-flight.
                ExpiresAtUtc = DateTime.UtcNow.AddSeconds(Math.Max(0, expiresInSeconds - 60))
            };
            await persistAsync(all);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// A valid access token for this broadcaster, refreshing first if it's expired or close to
    /// it. Null if this broadcaster hasn't completed /oauth/authorize yet (or their refresh token
    /// has since been revoked) - callers treat that as "Channel Points isn't set up for them",
    /// not as an error to surface to a viewer, and it never affects any other broadcaster's row.
    /// </summary>
    public async Task<string?> GetValidAccessTokenAsync(string broadcasterId, HttpClient http, string clientId, string clientSecret)
    {
        await gate.WaitAsync();
        try
        {
            var all = await loadAsync();
            if (!all.TryGetValue(broadcasterId, out var tokens))
                return null;

            if (DateTime.UtcNow < tokens.ExpiresAtUtc)
                return tokens.AccessToken;

            // Twitch access tokens live a few hours; this relay process can easily outlive that,
            // so refreshing has to happen automatically here rather than only at startup.
            using var response = await http.PostAsync(TwitchEndpoints.Id + "/oauth2/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = tokens.RefreshToken
            }));

            if (!response.IsSuccessStatusCode)
            {
                // This broadcaster's refresh token can be revoked (she removed the app's access,
                // or reset her password) - there is no recovering without /oauth/authorize again,
                // and it says nothing about any other broadcaster's tokens.
                return null;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            var refreshed = new StoredTokens
            {
                AccessToken = root.GetProperty("access_token").GetString() ?? "",
                RefreshToken = root.TryGetProperty("refresh_token", out var r) ? (r.GetString() ?? tokens.RefreshToken) : tokens.RefreshToken,
                ExpiresAtUtc = DateTime.UtcNow.AddSeconds(Math.Max(0, (root.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600) - 60))
            };
            all[broadcasterId] = refreshed;
            await persistAsync(all);
            return refreshed.AccessToken;
        }
        finally
        {
            gate.Release();
        }
    }

    private Task persistAsync(Dictionary<string, StoredTokens> all)
        => AtomicFile.WriteAllTextAsync(path, JsonSerializer.Serialize(all));

    private async Task<Dictionary<string, StoredTokens>> loadAsync()
    {
        if (cached is not null)
            return cached;

        if (!File.Exists(path))
        {
            cached = new Dictionary<string, StoredTokens>();
            return cached;
        }

        try
        {
            cached = JsonSerializer.Deserialize<Dictionary<string, StoredTokens>>(await File.ReadAllTextAsync(path))
                      ?? new Dictionary<string, StoredTokens>();
        }
        catch (JsonException)
        {
            // Moved aside rather than silently replaced - losing these means every streamer has
            // to run /oauth/authorize again, so the broken copy is worth keeping.
            AtomicFile.Quarantine(path);
            cached = new Dictionary<string, StoredTokens>();
        }
        return cached;
    }
}

/// <summary>
/// A random secret this relay picks for itself and hands to Twitch when creating the EventSub
/// webhook subscription (transport.secret) - Twitch signs every notification with it so the
/// webhook endpoint can tell a genuine Twitch delivery from anything else posted to that public
/// URL. Nothing to configure: generated once on first use and kept on disk so it survives
/// restarts (an existing subscription's secret can't be changed without recreating it).
/// </summary>
public sealed class WebhookSecretStore
{
    private readonly string path;
    private readonly SemaphoreSlim gate = new(1, 1);
    private string? cached;

    public WebhookSecretStore(string path)
    {
        this.path = path;
    }

    public async Task<string> GetOrCreateAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (cached is not null)
                return cached;

            if (File.Exists(path))
            {
                cached = (await File.ReadAllTextAsync(path)).Trim();
                if (cached.Length > 0)
                    return cached;
            }

            // Twitch requires 10-100 bytes for a webhook secret; 32 random bytes hex-encoded
            // (64 chars) sits comfortably inside that.
            cached = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            await AtomicFile.WriteAllTextAsync(path, cached);
            return cached;
        }
        finally
        {
            gate.Release();
        }
    }
}

/// <summary>
/// The handful of Helix calls the Channel Points path needs: who the authorized broadcaster is,
/// and making sure exactly one EventSub subscription exists for their redemptions.
/// </summary>
public static class TwitchApi
{
    /// <summary>
    /// Twitch requires webhook-transport EventSub subscriptions to be created with an app access
    /// token (client_credentials), not a user token - even though the subscription is scoped to
    /// one broadcaster via its `condition`. Fetched fresh each time this is called (only happens
    /// once per streamer, during /oauth/callback), so there's nothing to cache or invalidate.
    /// </summary>
    public static async Task<string?> GetAppAccessTokenAsync(HttpClient http, string clientId, string clientSecret)
    {
        using var response = await http.PostAsync(TwitchEndpoints.Id + "/oauth2/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["grant_type"] = "client_credentials"
        }));

        if (!response.IsSuccessStatusCode)
            return null;

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("access_token").GetString();
    }

    public static async Task<string?> ResolveBroadcasterIdAsync(HttpClient http, string accessToken, string clientId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, TwitchEndpoints.Helix + "/users");
        request.Headers.Add("Authorization", "Bearer " + accessToken);
        request.Headers.Add("Client-Id", clientId);

        using var response = await http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
            return null;

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = doc.RootElement.GetProperty("data");
        return data.GetArrayLength() > 0 ? data[0].GetProperty("id").GetString() : null;
    }

    /// <summary>
    /// A redemption being created - a viewer spending their points. This is what credits tokens.
    /// </summary>
    public const string RedemptionAddType = "channel.channel_points_custom_reward_redemption.add";

    /// <summary>
    /// A redemption changing state afterwards - accepted, or refunded out of the streamer's
    /// request queue. Only the refund matters here, and it is why this second subscription exists
    /// at all: without it, points handed back to a viewer leave the tokens they bought in place.
    /// </summary>
    public const string RedemptionUpdateType = "channel.channel_points_custom_reward_redemption.update";

    public static readonly string[] RedemptionTypes = { RedemptionAddType, RedemptionUpdateType };

    public sealed record ExistingSubscription(string Id, string Type, string Status);

    /// <summary>
    /// Every redemption subscription this relay already has for a broadcaster, keyed by type.
    /// Both the setup flow and config.html's status check read this - one to know what still
    /// needs creating, the other to tell the streamer whether they are done.
    /// </summary>
    public static async Task<Dictionary<string, ExistingSubscription>?> FindRedemptionSubscriptionsAsync(
        HttpClient http, string appAccessToken, string clientId, string broadcasterId, string callbackUrl)
    {
        var found = new Dictionary<string, ExistingSubscription>();

        foreach (var type in RedemptionTypes)
        {
            using var listRequest = new HttpRequestMessage(HttpMethod.Get,
                TwitchEndpoints.Helix + "/eventsub/subscriptions?type=" + Uri.EscapeDataString(type));
            listRequest.Headers.Add("Authorization", "Bearer " + appAccessToken);
            listRequest.Headers.Add("Client-Id", clientId);

            using var listResponse = await http.SendAsync(listRequest);
            if (!listResponse.IsSuccessStatusCode)
                return null; // can't tell - the caller must not read that as "nothing exists"

            using var listDoc = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
            if (!listDoc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var sub in data.EnumerateArray())
            {
                var subType = sub.TryGetProperty("type", out var t) ? t.GetString() : null;
                var condBroadcaster = sub.TryGetProperty("condition", out var cond)
                    && cond.TryGetProperty("broadcaster_user_id", out var b) ? b.GetString() : null;
                var transportCallback = sub.TryGetProperty("transport", out var transport)
                    && transport.TryGetProperty("callback", out var c) ? c.GetString() : null;
                var status = sub.TryGetProperty("status", out var st) ? st.GetString() : null;
                var id = sub.TryGetProperty("id", out var i) ? i.GetString() : null;

                if (subType == type && condBroadcaster == broadcasterId && transportCallback == callbackUrl
                    && id is not null && status is "enabled" or "webhook_callback_verification_pending")
                {
                    found[type] = new ExistingSubscription(id, type, status);
                }
            }
        }

        return found;
    }

    private static async Task<bool> deleteSubscriptionAsync(
        HttpClient http, string appAccessToken, string clientId, string subscriptionId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete,
            TwitchEndpoints.Helix + "/eventsub/subscriptions?id=" + Uri.EscapeDataString(subscriptionId));
        request.Headers.Add("Authorization", "Bearer " + appAccessToken);
        request.Headers.Add("Client-Id", clientId);

        using var response = await http.SendAsync(request);
        return response.IsSuccessStatusCode;
    }

    /// <summary>
    /// Makes this broadcaster's redemption subscriptions match what the relay currently holds -
    /// both event types, on the current callback, signed with the current webhook secret.
    ///
    /// Existing ones are deleted and re-created rather than left alone, and that is the point:
    /// Twitch never discloses the secret a subscription was created with, so there is no way to
    /// check whether it still matches ours. If this relay's secret was ever lost and regenerated,
    /// every delivery fails its signature check here while Twitch goes on reporting the
    /// subscription as enabled - and leaving a healthy-looking subscription in place is exactly
    /// what made that unrecoverable. Re-authorizing is a deliberate act by the streamer, so it is
    /// the right place to make repair the default rather than something they have to know to ask
    /// for. Duplicates are impossible either way: whatever was there is gone first.
    /// </summary>
    public static async Task<(bool Ok, string Status)> EnsureRedemptionSubscriptionsAsync(
        HttpClient http, string accessToken, string clientId,
        string broadcasterId, string callbackUrl, string webhookSecret)
    {
        var existing = await FindRedemptionSubscriptionsAsync(http, accessToken, clientId, broadcasterId, callbackUrl);
        if (existing is null)
            return (false, "could not read existing subscriptions from Twitch");

        var replaced = 0;
        foreach (var subscription in existing.Values)
        {
            if (await deleteSubscriptionAsync(http, accessToken, clientId, subscription.Id))
                replaced++;
        }

        foreach (var type in RedemptionTypes)
        {
            var body = JsonSerializer.Serialize(new
            {
                type,
                version = "1",
                condition = new { broadcaster_user_id = broadcasterId },
                transport = new { method = "webhook", callback = callbackUrl, secret = webhookSecret }
            });

            using var createRequest = new HttpRequestMessage(HttpMethod.Post, TwitchEndpoints.Helix + "/eventsub/subscriptions");
            createRequest.Headers.Add("Authorization", "Bearer " + accessToken);
            createRequest.Headers.Add("Client-Id", clientId);
            createRequest.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");

            using var createResponse = await http.SendAsync(createRequest);
            if (!createResponse.IsSuccessStatusCode)
            {
                var responseText = await createResponse.Content.ReadAsStringAsync();
                return (false, $"{type} failed ({(int)createResponse.StatusCode}): {responseText}");
            }
        }

        return (true, replaced > 0
            ? $"re-created ({replaced} replaced, {RedemptionTypes.Length} now active)"
            : $"created ({RedemptionTypes.Length} active)");
    }
}
