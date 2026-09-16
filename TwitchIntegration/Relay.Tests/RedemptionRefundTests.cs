using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace TwitchRelay.Tests;

/// <summary>
/// What happens when a streamer refunds a Channel Points redemption that already bought tokens.
///
/// A reward that doesn't skip the request queue sits there until the streamer accepts or refunds
/// it, and the tokens are credited the moment it is redeemed - so without this, refunding the
/// points left the tokens behind. That is not merely untidy: redeem, summon, get refunded, repeat
/// is a way to summon indefinitely for nothing.
/// </summary>
public class RedemptionRefundTests
{
    private static async Task<RelayHarness> StartWithPacksAsync(params (string Id, int Cost, bool Available)[] packs)
    {
        var relay = await RelayHarness.StartAsync();
        var overlay = await relay.ConnectOverlayAsync();
        await overlay.PublishPacksAsync(packs.Length > 0 ? packs : new[] { ("pack-a", 1, true) });
        return relay;
    }

    [Fact]
    public async Task Refunding_a_redemption_takes_back_exactly_what_it_credited()
    {
        await using var relay = await StartWithPacksAsync();

        await relay.RedeemAsync(points: 2000, redemptionId: "redemption-1"); // 4 tokens
        Assert.Equal(4, await relay.GetBalanceAsync());

        var response = await relay.ResolveRedemptionAsync("redemption-1", "CANCELED");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, await relay.GetBalanceAsync());
    }

    /// <summary>
    /// Accepting a redemption is the normal outcome and must leave the tokens alone - it arrives
    /// on the same subscription as a refund.
    /// </summary>
    [Fact]
    public async Task Fulfilling_a_redemption_leaves_the_tokens_alone()
    {
        await using var relay = await StartWithPacksAsync();

        await relay.RedeemAsync(points: 2000, redemptionId: "redemption-1");
        await relay.ResolveRedemptionAsync("redemption-1", "FULFILLED");

        Assert.Equal(4, await relay.GetBalanceAsync());
    }

    /// <summary>
    /// The price could have changed between the redemption and the refund, so the amount taken
    /// back has to come from what was recorded at the time, not from recomputing it now.
    /// </summary>
    [Fact]
    public async Task A_refund_takes_back_the_original_amount_even_if_the_price_has_since_changed()
    {
        await using var relay = await StartWithPacksAsync();

        await relay.RedeemAsync(points: 2000, redemptionId: "redemption-1"); // 4 tokens at 500/token
        Assert.Equal(4, await relay.GetBalanceAsync());

        // The streamer halves the price afterwards.
        relay.Twitch.SetPrices(RelayHarness.BroadcasterId, RelayHarness.BitsPerToken, pointsPerToken: 250, rewardName: RelayHarness.RewardName);

        await relay.ResolveRedemptionAsync("redemption-1", "CANCELED");

        Assert.Equal(0, await relay.GetBalanceAsync()); // 4 back, not 8
    }

    /// <summary>
    /// The loop this exists to close: spend the tokens first, then get the points refunded.
    /// Stopping at zero would make that free summons on repeat, so the balance is allowed to go
    /// negative and the viewer has to buy their way back up before spending again.
    /// </summary>
    [Fact]
    public async Task A_refund_after_the_tokens_were_spent_leaves_the_viewer_in_debt()
    {
        await using var relay = await StartWithPacksAsync(("pack-a", 4, true));
        await using var overlay = await relay.ConnectOverlayAsync();
        await overlay.PublishPacksAsync(("pack-a", 4, true));

        await relay.RedeemAsync(points: 2000, redemptionId: "redemption-1"); // 4 tokens

        var summon = await relay.PostSummonAsync(new { packs = new[] { "pack-a" }, authToken = RelayHarness.ViewerToken() });
        Assert.Equal(HttpStatusCode.OK, summon.StatusCode);
        Assert.Equal(0, await relay.GetBalanceAsync());

        await relay.ResolveRedemptionAsync("redemption-1", "CANCELED");
        Assert.Equal(-4, await relay.GetBalanceAsync());

        // And they cannot simply redeem-and-refund their way to another free summon.
        var again = await relay.PostSummonAsync(new { packs = new[] { "pack-a" }, authToken = RelayHarness.ViewerToken() });
        Assert.Equal(HttpStatusCode.PaymentRequired, again.StatusCode);
    }

    [Fact]
    public async Task Buying_more_tokens_clears_the_debt_before_anything_is_spendable()
    {
        await using var relay = await StartWithPacksAsync(("pack-a", 4, true));

        await relay.RedeemAsync(points: 2000, redemptionId: "redemption-1");
        await relay.PostSummonAsync(new { packs = new[] { "pack-a" }, authToken = RelayHarness.ViewerToken() });
        await relay.ResolveRedemptionAsync("redemption-1", "CANCELED");
        Assert.Equal(-4, await relay.GetBalanceAsync());

        await relay.PostBitsAsync(new
        {
            receipts = new[] { FakeTwitch.BitsReceiptToken(RelayHarness.ExtensionSecret, "txn-repay", RelayHarness.ViewerId, 1000) },
            authToken = RelayHarness.ViewerToken()
        });

        Assert.Equal(6, await relay.GetBalanceAsync()); // 10 bought, 4 of it settling the debt
    }

    /// <summary>
    /// Twitch retries deliveries. A refund applied twice would take double the tokens back.
    /// </summary>
    [Fact]
    public async Task A_refund_cannot_be_applied_twice()
    {
        await using var relay = await StartWithPacksAsync();

        await relay.RedeemAsync(points: 2000, redemptionId: "redemption-1");

        // Different message ids, so the delivery-level duplicate guard does not cover this - the
        // ledger entry being consumed is what does.
        await relay.ResolveRedemptionAsync("redemption-1", "CANCELED", messageId: "msg-a");
        await relay.ResolveRedemptionAsync("redemption-1", "CANCELED", messageId: "msg-b");

        Assert.Equal(0, await relay.GetBalanceAsync()); // not -4
    }

    [Fact]
    public async Task A_refund_still_applies_after_the_relay_restarts()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "relay-tests", Guid.NewGuid().ToString("N"));

        await using (var relay = await RelayHarness.StartAsync(dataDir))
        {
            await using var overlay = await relay.ConnectOverlayAsync();
            await overlay.PublishPacksAsync(("pack-a", 1, true));
            await relay.RedeemAsync(points: 2000, redemptionId: "redemption-1");
            Assert.Equal(4, await relay.GetBalanceAsync());
        }

        await using (var restarted = await RelayHarness.StartAsync(dataDir))
        {
            await using var overlay = await restarted.ConnectOverlayAsync();
            await overlay.PublishPacksAsync(("pack-a", 1, true));

            await restarted.ResolveRedemptionAsync("redemption-1", "CANCELED");

            Assert.Equal(0, await restarted.GetBalanceAsync());
        }
    }

    /// <summary>
    /// A redemption of some unrelated reward credited nothing, so refunding it must take nothing.
    /// </summary>
    [Fact]
    public async Task Refunding_a_redemption_that_never_credited_anything_changes_nothing()
    {
        await using var relay = await StartWithPacksAsync();

        await relay.RedeemAsync(points: 2000, redemptionId: "redemption-tokens");
        await relay.RedeemAsync(points: 5000, rewardTitle: "Hydrate!", redemptionId: "redemption-other");
        Assert.Equal(4, await relay.GetBalanceAsync());

        await relay.ResolveRedemptionAsync("redemption-other", "CANCELED", rewardTitle: "Hydrate!");

        Assert.Equal(4, await relay.GetBalanceAsync());
    }

    [Fact]
    public async Task A_refund_with_an_unknown_redemption_id_changes_nothing()
    {
        await using var relay = await StartWithPacksAsync();

        await relay.RedeemAsync(points: 2000, redemptionId: "redemption-1");
        await relay.ResolveRedemptionAsync("no-such-redemption", "CANCELED");

        Assert.Equal(4, await relay.GetBalanceAsync());
    }

    /// <summary>
    /// The refund event is only as trustworthy as the signature on it - the same public endpoint,
    /// and a forged one would let anyone drain any viewer's balance.
    /// </summary>
    [Fact]
    public async Task A_forged_refund_takes_nothing_back()
    {
        await using var relay = await StartWithPacksAsync();

        await relay.RedeemAsync(points: 2000, redemptionId: "redemption-1");

        var body = FakeTwitch.RedemptionBody(
            RelayHarness.BroadcasterId, RelayHarness.ViewerId, RelayHarness.RewardName, 0,
            subscriptionType: "channel.channel_points_custom_reward_redemption.update",
            redemptionId: "redemption-1", status: "CANCELED");

        var response = await relay.PostEventSubAsync(body, secret: "not-the-webhook-secret");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(4, await relay.GetBalanceAsync());
    }

    /// <summary>
    /// A refund is filed under the channel the original redemption was on, so one streamer's
    /// refund cannot reach into a viewer's balance on another's.
    /// </summary>
    [Fact]
    public async Task A_refund_only_touches_the_channel_the_redemption_was_on()
    {
        await using var relay = await StartWithPacksAsync();

        relay.Twitch.Logins["otherstreamer"] = "77777";
        relay.Twitch.SetPrices("77777", bitsPerToken: 1, pointsPerToken: 500, rewardName: RelayHarness.RewardName);
        await using var otherOverlay = await relay.ConnectOverlayAsync("streamkey0002", "controlkey0002", "otherstreamer");
        await otherOverlay.PublishPacksAsync(("pack-a", 1, true));

        await relay.RedeemAsync(points: 2000, redemptionId: "redemption-here");
        await relay.RedeemAsync(points: 2000, redemptionId: "redemption-there", broadcasterId: "77777");

        // Refund the one on the other channel.
        await relay.ResolveRedemptionAsync("redemption-there", "CANCELED", broadcasterId: "77777");

        Assert.Equal(4, await relay.GetBalanceAsync()); // untouched here

        var there = await relay.Client.PostAsJsonAsync("/api/balance/streamkey0002",
            new { authToken = RelayHarness.ViewerToken(channelId: "77777") });
        Assert.Equal(0, (await there.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("balance").GetInt32());
    }
}
