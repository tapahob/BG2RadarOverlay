using System.Text.Json;

namespace TwitchRelay;

public sealed class TokenPriceConfig
{
    public int BitsPerToken { get; init; }
    public int PointsPerToken { get; init; }
    public string RewardName { get; init; } = "";
}

/// <summary>
/// Reads what the streamer saved in config.html. relayUrl/streamKey never matter to the relay -
/// only a viewer's own browser reads those - but the token prices and reward name do, and the
/// relay has no other way to see them: config.html writes into Twitch's Configuration Service,
/// which only a browser-side Twitch.ext.configuration read can normally see.
///
/// A server reads the same data over Helix instead, authenticated as the extension itself - an
/// app access token from the extension's own client id/secret (Dev Console -> Extensions ->
/// Manage -> the extension's OAuth credentials), *not* the broadcaster's own OAuth grant. Nothing
/// here needs the broadcaster to authorize anything, unlike the Channel Points/EventSub path.
///
/// Cached briefly rather than fetched on every request: a redemption or purchase happening the
/// instant after a price change might see the old price for a few seconds, which is a far smaller
/// problem than hitting Helix - and its rate limits - on every single request.
/// </summary>
public sealed class ExtensionConfigCache
{
    private readonly string extensionClientId;
    private readonly string extensionClientSecret;
    private readonly string broadcasterLogin;
    private readonly TimeSpan ttl;

    private readonly SemaphoreSlim gate = new(1, 1);
    private string? appAccessToken;
    private DateTime appAccessTokenExpiresUtc;
    private string? broadcasterId;
    private TokenPriceConfig? cached;
    private DateTime cachedAtUtc;

    public ExtensionConfigCache(string extensionClientId, string extensionClientSecret, string broadcasterLogin, TimeSpan ttl)
    {
        this.extensionClientId = extensionClientId;
        this.extensionClientSecret = extensionClientSecret;
        this.broadcasterLogin = broadcasterLogin;
        this.ttl = ttl;
    }

    /// <summary>
    /// The broadcaster's numeric Twitch id - stable for as long as she keeps the same account,
    /// unlike a stream key (which she can and does regenerate from the Twitch Integration tab).
    /// This is what token balances are keyed against, precisely so a regenerated stream key can
    /// never orphan a balance a viewer already paid real Bits or points for.
    /// </summary>
    public async Task<string?> GetBroadcasterIdAsync(HttpClient http)
    {
        await gate.WaitAsync();
        try
        {
            if (broadcasterId is not null)
                return broadcasterId;

            var token = await ensureAppAccessTokenAsync(http);
            if (token is null)
                return null;

            return await ensureBroadcasterIdAsync(http, token);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<TokenPriceConfig?> GetAsync(HttpClient http)
    {
        await gate.WaitAsync();
        try
        {
            if (cached is not null && DateTime.UtcNow - cachedAtUtc < ttl)
                return cached;

            var token = await ensureAppAccessTokenAsync(http);
            if (token is null)
                return cached; // stale-but-something beats nothing if Twitch hiccups

            var id = await ensureBroadcasterIdAsync(http, token);
            if (id is null)
                return cached;

            using var request = new HttpRequestMessage(HttpMethod.Get,
                "https://api.twitch.tv/helix/extensions/configurations"
                + "?extension_id=" + Uri.EscapeDataString(extensionClientId)
                + "&broadcaster_id=" + Uri.EscapeDataString(id)
                + "&segment=broadcaster");
            request.Headers.Add("Authorization", "Bearer " + token);
            request.Headers.Add("Client-Id", extensionClientId);

            using var response = await http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                return cached;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var data = doc.RootElement.GetProperty("data");
            if (data.GetArrayLength() == 0)
                return cached; // nothing saved in config.html yet

            var contentJson = data[0].TryGetProperty("content", out var c) ? c.GetString() : null;
            if (string.IsNullOrEmpty(contentJson))
                return cached;

            using var contentDoc = JsonDocument.Parse(contentJson);
            var contentRoot = contentDoc.RootElement;
            cached = new TokenPriceConfig
            {
                BitsPerToken = contentRoot.TryGetProperty("bitsPerToken", out var b) && b.ValueKind == JsonValueKind.Number ? b.GetInt32() : 0,
                PointsPerToken = contentRoot.TryGetProperty("pointsPerToken", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 0,
                RewardName = contentRoot.TryGetProperty("rewardName", out var r) ? (r.GetString() ?? "") : ""
            };
            cachedAtUtc = DateTime.UtcNow;
            return cached;
        }
        catch (JsonException)
        {
            return cached;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<string?> ensureAppAccessTokenAsync(HttpClient http)
    {
        if (appAccessToken is not null && DateTime.UtcNow < appAccessTokenExpiresUtc)
            return appAccessToken;

        using var response = await http.PostAsync("https://id.twitch.tv/oauth2/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = extensionClientId,
            ["client_secret"] = extensionClientSecret,
            ["grant_type"] = "client_credentials"
        }));
        if (!response.IsSuccessStatusCode)
            return null;

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        appAccessToken = root.GetProperty("access_token").GetString();
        var expiresIn = root.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600;
        appAccessTokenExpiresUtc = DateTime.UtcNow.AddSeconds(Math.Max(0, expiresIn - 60));
        return appAccessToken;
    }

    private async Task<string?> ensureBroadcasterIdAsync(HttpClient http, string token)
    {
        if (broadcasterId is not null)
            return broadcasterId;

        using var request = new HttpRequestMessage(HttpMethod.Get,
            "https://api.twitch.tv/helix/users?login=" + Uri.EscapeDataString(broadcasterLogin));
        request.Headers.Add("Authorization", "Bearer " + token);
        request.Headers.Add("Client-Id", extensionClientId);

        using var response = await http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
            return null;

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = doc.RootElement.GetProperty("data");
        broadcasterId = data.GetArrayLength() > 0 ? data[0].GetProperty("id").GetString() : null;
        return broadcasterId;
    }
}
