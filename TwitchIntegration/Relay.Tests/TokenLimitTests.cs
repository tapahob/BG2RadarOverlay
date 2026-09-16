using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace TwitchRelay.Tests;

/// <summary>
/// The ceiling a streamer sets in config.html on how many tokens one viewer may hold, so nobody
/// can bank a stockpile and spend the whole run in one go.
///
/// It applies to Channel Points and not to Bits, and that asymmetry is the whole design: points
/// are earned by watching, so a redemption that won't fit costs the viewer nothing real, while
/// Bits are money - refusing part of a purchase would be taking payment for tokens never handed
/// over. So a Bits purchase credits in full even when it carries someone past the ceiling, and
/// points simply stop crediting until they have spent back under it.
/// </summary>
public class TokenLimitTests
{
    private static async Task<(RelayHarness Relay, FakeOverlay Overlay)> StartAsync(int limit, int packCost = 1)
    {
        var relay = await RelayHarness.StartAsync();
        relay.SetTokenLimit(limit);
        var overlay = await relay.ConnectOverlayAsync();
        await overlay.PublishPacksAsync(("pack-a", packCost, true));
        return (relay, overlay);
    }

    private static Task<HttpResponseMessage> BuyBitsAsync(RelayHarness relay, int bits, string transactionId)
        => relay.PostBitsAsync(new
        {
            receipts = new[] { FakeTwitch.BitsReceiptToken(RelayHarness.ExtensionSecret, transactionId, RelayHarness.ViewerId, bits) },
            authToken = RelayHarness.ViewerToken()
        });

    [Fact]
    public async Task A_redemption_credits_only_what_fits_under_the_limit()
    {
        var (relay, overlay) = await StartAsync(limit: 5);
        await using var _ = relay;
        await using var __ = overlay;

        await relay.RedeemAsync(points: 2000); // would be 4 tokens
        Assert.Equal(4, await relay.GetBalanceAsync());

        await relay.RedeemAsync(points: 2000); // would be 4 more, only 1 fits
        Assert.Equal(5, await relay.GetBalanceAsync());
    }

    [Fact]
    public async Task A_redemption_by_a_viewer_already_at_the_limit_credits_nothing()
    {
        var (relay, overlay) = await StartAsync(limit: 4);
        await using var _ = relay;
        await using var __ = overlay;

        await relay.RedeemAsync(points: 2000);
        Assert.Equal(4, await relay.GetBalanceAsync());

        var response = await relay.RedeemAsync(points: 2000);

        // Accepted so Twitch doesn't retry it - there is simply nothing to credit.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(4, await relay.GetBalanceAsync());
    }

    [Fact]
    public async Task Spending_back_under_the_limit_lets_redemptions_credit_again()
    {
        var (relay, overlay) = await StartAsync(limit: 4, packCost: 3);
        await using var _ = relay;
        await using var __ = overlay;

        await relay.RedeemAsync(points: 2000);
        Assert.Equal(4, await relay.GetBalanceAsync());

        await relay.PostSummonAsync(new { packs = new[] { "pack-a" }, authToken = RelayHarness.ViewerToken() });
        Assert.Equal(1, await relay.GetBalanceAsync());

        await relay.RedeemAsync(points: 2000); // 4 would fit 3
        Assert.Equal(4, await relay.GetBalanceAsync());
    }

    /// <summary>
    /// The half of the rule that protects the viewer: Bits are already paid by the time the relay
    /// sees the receipt, so every token bought has to be handed over.
    /// </summary>
    [Fact]
    public async Task A_bits_purchase_credits_in_full_even_past_the_limit()
    {
        var (relay, overlay) = await StartAsync(limit: 5);
        await using var _ = relay;
        await using var __ = overlay;

        await relay.RedeemAsync(points: 2000); // 4 tokens, one under the limit
        var response = await BuyBitsAsync(relay, bits: 1000, transactionId: "txn-over-limit"); // 10 more

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(10, body.GetProperty("credited").GetInt32());
        Assert.Equal(14, body.GetProperty("balance").GetInt32());
        Assert.Equal(14, await relay.GetBalanceAsync());
    }

    [Fact]
    public async Task A_viewer_carried_past_the_limit_by_bits_gets_nothing_more_from_points()
    {
        var (relay, overlay) = await StartAsync(limit: 5);
        await using var _ = relay;
        await using var __ = overlay;

        await BuyBitsAsync(relay, bits: 1000, transactionId: "txn-big"); // 10 tokens, past the limit of 5
        Assert.Equal(10, await relay.GetBalanceAsync());

        await relay.RedeemAsync(points: 5000);

        Assert.Equal(10, await relay.GetBalanceAsync()); // points add nothing while over
    }

    [Fact]
    public async Task Tokens_over_the_limit_are_still_spendable()
    {
        var (relay, overlay) = await StartAsync(limit: 5, packCost: 8);
        await using var _ = relay;
        await using var __ = overlay;

        await BuyBitsAsync(relay, bits: 1000, transactionId: "txn-big"); // 10 tokens

        var response = await relay.PostSummonAsync(new { packs = new[] { "pack-a" }, authToken = RelayHarness.ViewerToken() });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, await relay.GetBalanceAsync());
        Assert.NotNull(await overlay.ReceiveCommandAsync());
    }

    /// <summary>
    /// A redemption that was only partly credited must only have that part taken back when it is
    /// refunded - refunding the nominal amount would leave the viewer out of pocket.
    /// </summary>
    [Fact]
    public async Task Refunding_a_partly_credited_redemption_takes_back_only_what_landed()
    {
        var (relay, overlay) = await StartAsync(limit: 5);
        await using var _ = relay;
        await using var __ = overlay;

        await relay.RedeemAsync(points: 2000, redemptionId: "redemption-full");     // 4 tokens
        await relay.RedeemAsync(points: 2000, redemptionId: "redemption-partial");  // only 1 fits
        Assert.Equal(5, await relay.GetBalanceAsync());

        await relay.ResolveRedemptionAsync("redemption-partial", "CANCELED");

        Assert.Equal(4, await relay.GetBalanceAsync()); // 1 back, not 4
    }

    [Fact]
    public async Task Refunding_a_redemption_that_did_not_fit_at_all_takes_nothing_back()
    {
        var (relay, overlay) = await StartAsync(limit: 4);
        await using var _ = relay;
        await using var __ = overlay;

        await relay.RedeemAsync(points: 2000, redemptionId: "redemption-first"); // 4 tokens, at the limit
        await relay.RedeemAsync(points: 2000, redemptionId: "redemption-none");  // nothing fits

        await relay.ResolveRedemptionAsync("redemption-none", "CANCELED");

        Assert.Equal(4, await relay.GetBalanceAsync());
    }

    /// <summary>
    /// A viewer in debt from a refund has to climb out of it before the ceiling means anything -
    /// the room available is measured from where they actually are, not from zero.
    /// </summary>
    [Fact]
    public async Task A_viewer_in_debt_can_redeem_their_way_back_up_to_the_limit()
    {
        var (relay, overlay) = await StartAsync(limit: 5, packCost: 4);
        await using var _ = relay;
        await using var __ = overlay;

        await relay.RedeemAsync(points: 2000, redemptionId: "redemption-1"); // 4 tokens
        await relay.PostSummonAsync(new { packs = new[] { "pack-a" }, authToken = RelayHarness.ViewerToken() });
        await relay.ResolveRedemptionAsync("redemption-1", "CANCELED");
        Assert.Equal(-4, await relay.GetBalanceAsync());

        await relay.RedeemAsync(points: 5000); // 10 tokens' worth; 9 of it fits between -4 and 5

        Assert.Equal(5, await relay.GetBalanceAsync());
    }

    [Fact]
    public async Task A_limit_of_zero_means_no_limit()
    {
        var (relay, overlay) = await StartAsync(limit: 0);
        await using var _ = relay;
        await using var __ = overlay;

        await relay.RedeemAsync(points: 50000);

        Assert.Equal(100, await relay.GetBalanceAsync());
    }

    [Fact]
    public async Task The_limit_is_per_viewer_not_shared_between_them()
    {
        var (relay, overlay) = await StartAsync(limit: 4);
        await using var _ = relay;
        await using var __ = overlay;

        await relay.RedeemAsync(points: 2000);
        await relay.RedeemAsync(points: 2000, userId: "55555");

        Assert.Equal(4, await relay.GetBalanceAsync());

        var other = await relay.Client.PostAsJsonAsync("/api/balance/" + RelayHarness.StreamKey,
            new { authToken = RelayHarness.ViewerToken(userId: "55555") });
        Assert.Equal(4, (await other.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("balance").GetInt32());
    }

    /// <summary>
    /// Each streamer sets their own ceiling, and it only governs their own channel's balances.
    /// </summary>
    [Fact]
    public async Task Each_streamer_gets_their_own_limit()
    {
        var (relay, overlay) = await StartAsync(limit: 4);
        await using var _ = relay;
        await using var __ = overlay;

        relay.Twitch.Logins["otherstreamer"] = "77777";
        relay.Twitch.SetPrices("77777", RelayHarness.BitsPerToken, RelayHarness.PointsPerToken,
            RelayHarness.RewardName, maxTokenBalance: 0);
        await using var otherOverlay = await relay.ConnectOverlayAsync("streamkey0002", "controlkey0002", "otherstreamer");
        await otherOverlay.PublishPacksAsync(("pack-a", 1, true));

        await relay.RedeemAsync(points: 5000);                              // capped at 4 here
        await relay.RedeemAsync(points: 5000, broadcasterId: "77777");      // uncapped there

        Assert.Equal(4, await relay.GetBalanceAsync());

        var there = await relay.Client.PostAsJsonAsync("/api/balance/streamkey0002",
            new { authToken = RelayHarness.ViewerToken(channelId: "77777") });
        Assert.Equal(10, (await there.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("balance").GetInt32());
    }

    /// <summary>
    /// Two redemptions landing together must not both see the same under-the-limit balance and
    /// both credit in full - the check and the write have to be one step.
    /// </summary>
    [Fact]
    public async Task Simultaneous_redemptions_cannot_race_past_the_limit()
    {
        var (relay, overlay) = await StartAsync(limit: 5);
        await using var _ = relay;
        await using var __ = overlay;

        var redemptions = Enumerable.Range(0, 8)
            .Select(i => relay.RedeemAsync(points: 2000, redemptionId: "redemption-" + i, messageId: "msg-" + i))
            .ToArray();
        await Task.WhenAll(redemptions);

        Assert.Equal(5, await relay.GetBalanceAsync());
    }
}
