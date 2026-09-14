using System.Text.Json;

namespace TwitchRelay;

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
            using var response = await http.PostAsync("https://id.twitch.tv/oauth2/token", new FormUrlEncodedContent(new Dictionary<string, string>
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

    private async Task persistAsync(Dictionary<string, StoredTokens> all)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(all));
    }

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
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(path, cached);
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
        using var response = await http.PostAsync("https://id.twitch.tv/oauth2/token", new FormUrlEncodedContent(new Dictionary<string, string>
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
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.twitch.tv/helix/users");
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
    /// Whether this broadcaster already has a live (or pending-verification) redemption
    /// subscription pointed at our callback URL - shared by the idempotent create below and by
    /// config.html's "is Channel Points authorized yet" status check, so a streamer can see
    /// whether they still need to click Authorize without it ever creating a duplicate.
    /// </summary>
    public static async Task<string?> FindActiveRedemptionSubscriptionStatusAsync(
        HttpClient http, string appAccessToken, string clientId, string broadcasterId, string callbackUrl)
    {
        using var listRequest = new HttpRequestMessage(HttpMethod.Get,
            "https://api.twitch.tv/helix/eventsub/subscriptions?type=channel.channel_points_custom_reward_redemption.add");
        listRequest.Headers.Add("Authorization", "Bearer " + appAccessToken);
        listRequest.Headers.Add("Client-Id", clientId);

        using var listResponse = await http.SendAsync(listRequest);
        if (!listResponse.IsSuccessStatusCode)
            return null;

        using var listDoc = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
        foreach (var sub in listDoc.RootElement.GetProperty("data").EnumerateArray())
        {
            var condBroadcaster = sub.GetProperty("condition").TryGetProperty("broadcaster_user_id", out var b) ? b.GetString() : null;
            var transportCallback = sub.GetProperty("transport").TryGetProperty("callback", out var c) ? c.GetString() : null;
            var status = sub.TryGetProperty("status", out var s) ? s.GetString() : null;

            if (condBroadcaster == broadcasterId && transportCallback == callbackUrl
                && status is "enabled" or "webhook_callback_verification_pending")
            {
                return status;
            }
        }
        return null;
    }

    /// <summary>
    /// Idempotent: lists what's already subscribed and only creates a new one if nothing matches
    /// this broadcaster + callback URL yet, so re-running /oauth/authorize (or a relay restart
    /// that redoes setup) doesn't pile up duplicate subscriptions - Twitch would otherwise deliver
    /// every redemption two, three, N times over.
    /// </summary>
    public static async Task<(bool Ok, string Status)> EnsureRedemptionSubscriptionAsync(
        HttpClient http, string accessToken, string clientId,
        string broadcasterId, string callbackUrl, string webhookSecret)
    {
        var existingStatus = await FindActiveRedemptionSubscriptionStatusAsync(http, accessToken, clientId, broadcasterId, callbackUrl);
        if (existingStatus is not null)
            return (true, "already subscribed (" + existingStatus + ")");

        var body = JsonSerializer.Serialize(new
        {
            type = "channel.channel_points_custom_reward_redemption.add",
            version = "1",
            condition = new { broadcaster_user_id = broadcasterId },
            transport = new { method = "webhook", callback = callbackUrl, secret = webhookSecret }
        });

        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "https://api.twitch.tv/helix/eventsub/subscriptions");
        createRequest.Headers.Add("Authorization", "Bearer " + accessToken);
        createRequest.Headers.Add("Client-Id", clientId);
        createRequest.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");

        using var createResponse = await http.SendAsync(createRequest);
        var responseText = await createResponse.Content.ReadAsStringAsync();
        return (createResponse.IsSuccessStatusCode, createResponse.IsSuccessStatusCode ? "created" : $"failed ({(int)createResponse.StatusCode}): {responseText}");
    }
}
