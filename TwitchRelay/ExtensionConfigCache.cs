using System.Collections.Concurrent;
using System.Text.Json;

namespace TwitchRelay;

public sealed class TokenPriceConfig
{
    public int BitsPerToken { get; init; }
    public int PointsPerToken { get; init; }
    public string RewardName { get; init; } = "";
}

/// <summary>
/// Reads what each streamer saved in config.html. relayUrl/streamKey never matter to the relay -
/// only a viewer's own browser reads those - but the token prices and reward name do, and the
/// relay has no other way to see them: config.html writes into Twitch's Configuration Service,
/// which only a browser-side Twitch.ext.configuration read can normally see.
///
/// A server reads the same data over Helix instead, authenticated as the extension itself - an
/// app access token from the extension's own client id/secret (Dev Console -> Extensions ->
/// Manage -> the extension's OAuth credentials), *not* any one broadcaster's OAuth grant. That
/// token is a single extension-wide credential, shared across every streamer this relay serves;
/// everything else here (which broadcaster's prices, which broadcaster's numeric id) is cached
/// per streamer, since this relay can be serving several at once.
/// </summary>
public sealed class ExtensionConfigCache
{
    private readonly string extensionClientId;
    private readonly string extensionClientSecret;
    private readonly TimeSpan ttl;

    private readonly SemaphoreSlim tokenGate = new(1, 1);
    private string? appAccessToken;
    private DateTime appAccessTokenExpiresUtc;

    // Twitch logins don't change, so once resolved a login -> id mapping is good forever - no
    // TTL, no re-fetch, just a growing cache bounded by how many distinct streamers this relay
    // has ever seen.
    private readonly ConcurrentDictionary<string, string> loginToId = new();

    private sealed class PriceEntry
    {
        public TokenPriceConfig? Config;
        public DateTime CachedAtUtc;
    }
    private readonly ConcurrentDictionary<string, PriceEntry> pricesByBroadcasterId = new();

    public ExtensionConfigCache(string extensionClientId, string extensionClientSecret, TimeSpan ttl)
    {
        this.extensionClientId = extensionClientId;
        this.extensionClientSecret = extensionClientSecret;
        this.ttl = ttl;
    }

    /// <summary>
    /// A broadcaster's numeric Twitch id from their channel login - stable for as long as they
    /// keep the same account, unlike a stream key (which they can and do regenerate from the
    /// Twitch Integration tab). This is what token balances and price lookups key against,
    /// precisely so a regenerated stream key can never orphan a balance someone already paid
    /// real Bits or points for.
    /// </summary>
    public async Task<string?> ResolveBroadcasterIdAsync(HttpClient http, string login)
    {
        login = (login ?? "").Trim().ToLowerInvariant();
        if (login.Length == 0)
            return null;
        if (loginToId.TryGetValue(login, out var cachedId))
            return cachedId;

        var token = await ensureAppAccessTokenAsync(http);
        if (token is null)
            return null;

        using var request = new HttpRequestMessage(HttpMethod.Get,
            "https://api.twitch.tv/helix/users?login=" + Uri.EscapeDataString(login));
        request.Headers.Add("Authorization", "Bearer " + token);
        request.Headers.Add("Client-Id", extensionClientId);

        using var response = await http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
            return null;

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = doc.RootElement.GetProperty("data");
        if (data.GetArrayLength() == 0)
            return null;

        var id = data[0].GetProperty("id").GetString();
        if (id is not null)
            loginToId[login] = id;
        return id;
    }

    /// <summary>
    /// This broadcaster's token prices, from a numeric id already in hand - the EventSub webhook
    /// always has one (Twitch hands it over directly), everything else resolves it first via
    /// ResolveBroadcasterIdAsync. Cached briefly rather than fetched on every request: a
    /// redemption or purchase happening the instant after a price change might see the old price
    /// for a few seconds, which is a far smaller problem than hitting Helix - and its rate limits
    /// - on every single request, across however many streamers this relay serves.
    /// </summary>
    public async Task<TokenPriceConfig?> GetPricesAsync(HttpClient http, string broadcasterId)
    {
        if (pricesByBroadcasterId.TryGetValue(broadcasterId, out var entry)
            && entry.Config is not null && DateTime.UtcNow - entry.CachedAtUtc < ttl)
            return entry.Config;

        var token = await ensureAppAccessTokenAsync(http);
        if (token is null)
            return entry?.Config; // stale-but-something beats nothing if Twitch hiccups

        using var request = new HttpRequestMessage(HttpMethod.Get,
            "https://api.twitch.tv/helix/extensions/configurations"
            + "?extension_id=" + Uri.EscapeDataString(extensionClientId)
            + "&broadcaster_id=" + Uri.EscapeDataString(broadcasterId)
            + "&segment=broadcaster");
        request.Headers.Add("Authorization", "Bearer " + token);
        request.Headers.Add("Client-Id", extensionClientId);

        using var response = await http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
            return entry?.Config;

        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var data = doc.RootElement.GetProperty("data");
            if (data.GetArrayLength() == 0)
                return entry?.Config; // nothing saved in config.html yet for this broadcaster

            var contentJson = data[0].TryGetProperty("content", out var c) ? c.GetString() : null;
            if (string.IsNullOrEmpty(contentJson))
                return entry?.Config;

            using var contentDoc = JsonDocument.Parse(contentJson);
            var contentRoot = contentDoc.RootElement;
            var config = new TokenPriceConfig
            {
                BitsPerToken = contentRoot.TryGetProperty("bitsPerToken", out var b) && b.ValueKind == JsonValueKind.Number ? b.GetInt32() : 0,
                PointsPerToken = contentRoot.TryGetProperty("pointsPerToken", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 0,
                RewardName = contentRoot.TryGetProperty("rewardName", out var r) ? (r.GetString() ?? "") : ""
            };
            pricesByBroadcasterId[broadcasterId] = new PriceEntry { Config = config, CachedAtUtc = DateTime.UtcNow };
            return config;
        }
        catch (JsonException)
        {
            return entry?.Config;
        }
    }

    private async Task<string?> ensureAppAccessTokenAsync(HttpClient http)
    {
        await tokenGate.WaitAsync();
        try
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
        finally
        {
            tokenGate.Release();
        }
    }
}
