using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace TwitchRelay.Tests;

/// <summary>
/// Buying summon tokens with Channel Points. The shape of the trust is completely different from
/// the Bits path: nothing comes from the viewer's browser at all - Twitch posts the redemption to
/// a public endpoint, and an HMAC over (message id + timestamp + body) is the only thing that
/// distinguishes it from anyone else on the internet posting to the same URL.
///
/// Which means the credit-worthy questions are: is it really from Twitch, is it recent, have we
/// already handled it, is it even the reward that sells tokens, and whose channel was it on.
/// </summary>
public class ChannelPointsTests
{
    [Fact]
    public async Task A_redemption_credits_tokens_at_the_streamers_configured_rate()
    {
        await using var relay = await RelayHarness.StartAsync();
        await using var overlay = await relay.ConnectOverlayAsync();
        await overlay.PublishPacksAsync(("pack-a", 1, true));

        var response = await relay.RedeemAsync(points: 2000); // 500 points per token

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(4, await relay.GetBalanceAsync());
    }

    /// <summary>
    /// The whole point of the token economy: whichever currency bought them, they spend the same.
    /// </summary>
    [Fact]
    public async Task Tokens_bought_with_points_can_be_spent_on_a_summon()
    {
        await using var relay = await RelayHarness.StartAsync();
        await using var overlay = await relay.ConnectOverlayAsync();
        await overlay.PublishPacksAsync(("pack-a", 3, true));

        await relay.RedeemAsync(points: 2000); // 4 tokens

        var response = await relay.PostSummonAsync(new
        {
            packs = new[] { "pack-a" },
            authToken = RelayHarness.ViewerToken()
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, await relay.GetBalanceAsync());

        var command = await overlay.ReceiveCommandAsync();
        Assert.NotNull(command);
        Assert.Equal("summon", JsonDocument.Parse(command!).RootElement.GetProperty("type").GetString());
    }

    /// <summary>
    /// Points and Bits credit the same balance, so a viewer can part-pay with either.
    /// </summary>
    [Fact]
    public async Task Points_and_bits_add_up_to_one_balance()
    {
        await using var relay = await RelayHarness.StartAsync();
        await using var overlay = await relay.ConnectOverlayAsync();
        await overlay.PublishPacksAsync(("pack-a", 1, true));

        await relay.RedeemAsync(points: 1000); // 2 tokens
        await relay.PostBitsAsync(new
        {
            receipts = new[] { FakeTwitch.BitsReceiptToken(RelayHarness.ExtensionSecret, "txn-mixed", RelayHarness.ViewerId, 300) },
            authToken = RelayHarness.ViewerToken()
        });

        Assert.Equal(5, await relay.GetBalanceAsync()); // 2 from points + 3 from bits
    }

    [Fact]
    public async Task Points_that_dont_divide_evenly_round_down()
    {
        await using var relay = await RelayHarness.StartAsync();
        await using var overlay = await relay.ConnectOverlayAsync();
        await overlay.PublishPacksAsync(("pack-a", 1, true));

        await relay.RedeemAsync(points: 1750); // 3.5 tokens at 500/token

        Assert.Equal(3, await relay.GetBalanceAsync());
    }

    /// <summary>
    /// A channel has many rewards. Only the one whose title matches config.html's "Token Reward
    /// Name" sells tokens - every other redemption on the channel arrives here too.
    /// </summary>
    [Fact]
    public async Task Redeeming_some_other_reward_credits_nothing()
    {
        await using var relay = await RelayHarness.StartAsync();
        await using var overlay = await relay.ConnectOverlayAsync();
        await overlay.PublishPacksAsync(("pack-a", 1, true));

        await relay.RedeemAsync(points: 5000, rewardTitle: "Hydrate!");

        Assert.Equal(0, await relay.GetBalanceAsync());
    }

    [Fact]
    public async Task The_reward_name_is_matched_ignoring_case_and_surrounding_space()
    {
        await using var relay = await RelayHarness.StartAsync();
        await using var overlay = await relay.ConnectOverlayAsync();
        await overlay.PublishPacksAsync(("pack-a", 1, true));

        await relay.RedeemAsync(points: 500, rewardTitle: "  summon TOKEN ");

        Assert.Equal(1, await relay.GetBalanceAsync());
    }

    /// <summary>
    /// This endpoint is public - Twitch has to be able to reach it, so anyone can. The signature
    /// is the whole of the authentication.
    /// </summary>
    [Fact]
    public async Task A_redemption_with_a_bad_signature_credits_nothing()
    {
        await using var relay = await RelayHarness.StartAsync();
        await using var overlay = await relay.ConnectOverlayAsync();
        await overlay.PublishPacksAsync(("pack-a", 1, true));

        var body = FakeTwitch.RedemptionBody(RelayHarness.BroadcasterId, RelayHarness.ViewerId, RelayHarness.RewardName, 100000);
        var response = await relay.PostEventSubAsync(body, secret: "not-the-webhook-secret");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, await relay.GetBalanceAsync());
    }

    [Fact]
    public async Task A_redemption_with_no_signature_at_all_credits_nothing()
    {
        await using var relay = await RelayHarness.StartAsync();
        await using var overlay = await relay.ConnectOverlayAsync();
        await overlay.PublishPacksAsync(("pack-a", 1, true));

        var body = FakeTwitch.RedemptionBody(RelayHarness.BroadcasterId, RelayHarness.ViewerId, RelayHarness.RewardName, 5000);
        var response = await relay.PostEventSubAsync(body, signature: "sha256=00");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, await relay.GetBalanceAsync());
    }

    /// <summary>
    /// The signature covers the timestamp, so a genuine delivery captured once stays verifiable
    /// forever. Age is what stops it being replayable indefinitely.
    /// </summary>
    [Fact]
    public async Task A_delivery_older_than_the_replay_window_credits_nothing()
    {
        await using var relay = await RelayHarness.StartAsync();
        await using var overlay = await relay.ConnectOverlayAsync();
        await overlay.PublishPacksAsync(("pack-a", 1, true));

        var body = FakeTwitch.RedemptionBody(RelayHarness.BroadcasterId, RelayHarness.ViewerId, RelayHarness.RewardName, 5000);
        var response = await relay.PostEventSubAsync(body, timestamp: DateTimeOffset.UtcNow.AddHours(-1));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, await relay.GetBalanceAsync());
    }

    [Fact]
    public async Task A_delivery_dated_in_the_future_credits_nothing()
    {
        await using var relay = await RelayHarness.StartAsync();
        await using var overlay = await relay.ConnectOverlayAsync();
        await overlay.PublishPacksAsync(("pack-a", 1, true));

        var body = FakeTwitch.RedemptionBody(RelayHarness.BroadcasterId, RelayHarness.ViewerId, RelayHarness.RewardName, 5000);
        var response = await relay.PostEventSubAsync(body, timestamp: DateTimeOffset.UtcNow.AddHours(1));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, await relay.GetBalanceAsync());
    }

    /// <summary>
    /// Twitch retries a delivery it didn't get a clean 2xx for, reusing the message id. Crediting
    /// each retry would hand out tokens for one redemption several times over.
    /// </summary>
    [Fact]
    public async Task A_redelivered_notification_is_only_credited_once()
    {
        await using var relay = await RelayHarness.StartAsync();
        await using var overlay = await relay.ConnectOverlayAsync();
        await overlay.PublishPacksAsync(("pack-a", 1, true));

        var body = FakeTwitch.RedemptionBody(RelayHarness.BroadcasterId, RelayHarness.ViewerId, RelayHarness.RewardName, 1500);
        var messageId = "retried-message-id";

        await relay.PostEventSubAsync(body, messageId: messageId);
        await relay.PostEventSubAsync(body, messageId: messageId);
        await relay.PostEventSubAsync(body, messageId: messageId);

        Assert.Equal(3, await relay.GetBalanceAsync()); // credited once, not three times
    }

    [Fact]
    public async Task A_redelivered_notification_is_still_a_duplicate_after_a_restart()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "relay-tests", Guid.NewGuid().ToString("N"));
        var body = FakeTwitch.RedemptionBody(RelayHarness.BroadcasterId, RelayHarness.ViewerId, RelayHarness.RewardName, 1500);
        const string messageId = "survives-restart";

        await using (var relay = await RelayHarness.StartAsync(dataDir))
        {
            await using var overlay = await relay.ConnectOverlayAsync();
            await overlay.PublishPacksAsync(("pack-a", 1, true));
            await relay.PostEventSubAsync(body, messageId: messageId);
            Assert.Equal(3, await relay.GetBalanceAsync());
        }

        await using (var restarted = await RelayHarness.StartAsync(dataDir))
        {
            await using var overlay = await restarted.ConnectOverlayAsync();
            await overlay.PublishPacksAsync(("pack-a", 1, true));
            await restarted.PostEventSubAsync(body, messageId: messageId);
            Assert.Equal(3, await restarted.GetBalanceAsync()); // not 6
        }
    }

    /// <summary>
    /// A relay serving several streamers must file each redemption under the channel it happened
    /// on - Twitch names it in the event, so nothing here has to assume there is only one.
    /// </summary>
    [Fact]
    public async Task A_redemption_is_credited_on_the_channel_it_happened_on()
    {
        await using var relay = await RelayHarness.StartAsync();
        await using var overlay = await relay.ConnectOverlayAsync();
        await overlay.PublishPacksAsync(("pack-a", 1, true));

        // A second streamer on the same relay, pricing tokens far more cheaply.
        relay.Twitch.Logins["otherstreamer"] = "77777";
        relay.Twitch.SetPrices("77777", bitsPerToken: 1, pointsPerToken: 1, rewardName: RelayHarness.RewardName);
        await using var otherOverlay = await relay.ConnectOverlayAsync("streamkey0002", "controlkey0002", "otherstreamer");
        await otherOverlay.PublishPacksAsync(("pack-a", 1, true));

        // Redeemed on the cheap channel.
        await relay.RedeemAsync(points: 3000, broadcasterId: "77777");

        // 3000 tokens there...
        var otherBalance = await relay.Client.PostAsJsonAsync("/api/balance/streamkey0002",
            new { authToken = RelayHarness.ViewerToken(channelId: "77777") });
        Assert.Equal(3000, (await otherBalance.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("balance").GetInt32());

        // ...and none at all on the first streamer's channel.
        Assert.Equal(0, await relay.GetBalanceAsync());
    }

    /// <summary>
    /// A streamer can have Bits set up and Channel Points not - redemptions still arrive (the
    /// subscription is per channel, not per reward), and must simply be ignored.
    /// </summary>
    [Fact]
    public async Task A_redemption_on_a_channel_that_has_not_priced_points_credits_nothing()
    {
        await using var relay = await RelayHarness.StartAsync();
        // Overwritten before anything has read prices for this broadcaster, so this is what the
        // relay's cache will be populated from: a Bits price, no Channel Points price or reward.
        relay.Twitch.SetPrices(RelayHarness.BroadcasterId, bitsPerToken: RelayHarness.BitsPerToken, pointsPerToken: 0, rewardName: "");

        await using var overlay = await relay.ConnectOverlayAsync();
        await overlay.PublishPacksAsync(("pack-a", 1, true));

        await relay.RedeemAsync(points: 5000);

        Assert.Equal(0, await relay.GetBalanceAsync());
    }

    /// <summary>
    /// And the same when points are priced but the reward that sells them was never named - there
    /// is then no reward title that should match, so nothing should.
    /// </summary>
    [Fact]
    public async Task A_redemption_on_a_channel_with_no_reward_name_credits_nothing()
    {
        await using var relay = await RelayHarness.StartAsync();
        relay.Twitch.SetPrices(RelayHarness.BroadcasterId, bitsPerToken: RelayHarness.BitsPerToken, pointsPerToken: 500, rewardName: "");

        await using var overlay = await relay.ConnectOverlayAsync();
        await overlay.PublishPacksAsync(("pack-a", 1, true));

        await relay.RedeemAsync(points: 5000, rewardTitle: "");

        Assert.Equal(0, await relay.GetBalanceAsync());
    }

    [Fact]
    public async Task The_subscription_verification_challenge_is_echoed_back()
    {
        await using var relay = await RelayHarness.StartAsync();

        var body = JsonSerializer.Serialize(new
        {
            challenge = "a-challenge-string",
            subscription = new { id = "sub-1", status = "webhook_callback_verification_pending" }
        });

        var response = await relay.PostEventSubAsync(body, messageType: "webhook_callback_verification");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("a-challenge-string", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_revocation_is_accepted_without_crediting_anything()
    {
        await using var relay = await RelayHarness.StartAsync();
        await using var overlay = await relay.ConnectOverlayAsync();
        await overlay.PublishPacksAsync(("pack-a", 1, true));

        var body = FakeTwitch.RedemptionBody(RelayHarness.BroadcasterId, RelayHarness.ViewerId, RelayHarness.RewardName, 5000);
        var response = await relay.PostEventSubAsync(body, messageType: "revocation");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, await relay.GetBalanceAsync());
    }

    /// <summary>
    /// A subscription type we never asked for, arriving on the same signed endpoint, must not be
    /// mistaken for a token purchase.
    /// </summary>
    [Fact]
    public async Task A_notification_of_some_other_subscription_type_credits_nothing()
    {
        await using var relay = await RelayHarness.StartAsync();
        await using var overlay = await relay.ConnectOverlayAsync();
        await overlay.PublishPacksAsync(("pack-a", 1, true));

        var body = FakeTwitch.RedemptionBody(
            RelayHarness.BroadcasterId, RelayHarness.ViewerId, RelayHarness.RewardName, 5000,
            subscriptionType: "channel.follow");

        await relay.PostEventSubAsync(body);

        Assert.Equal(0, await relay.GetBalanceAsync());
    }

    [Fact]
    public async Task A_malformed_notification_is_refused_rather_than_faulting()
    {
        await using var relay = await RelayHarness.StartAsync();

        var garbage = await relay.PostEventSubAsync("not json at all");
        Assert.Equal(HttpStatusCode.BadRequest, garbage.StatusCode);

        var missingEverything = await relay.PostEventSubAsync("{}");
        Assert.Equal(HttpStatusCode.OK, missingEverything.StatusCode); // nothing to act on, nothing broken

        var absurdCost = await relay.PostEventSubAsync("""
            {"subscription":{"type":"channel.channel_points_custom_reward_redemption.add"},
             "event":{"broadcaster_user_id":"12345","user_id":"99001",
                      "reward":{"title":"Summon Token","cost":1e309}}}
            """);
        Assert.Equal(HttpStatusCode.OK, absurdCost.StatusCode);
    }

    [Fact]
    public async Task A_redemption_of_zero_or_negative_points_credits_nothing()
    {
        await using var relay = await RelayHarness.StartAsync();
        await using var overlay = await relay.ConnectOverlayAsync();
        await overlay.PublishPacksAsync(("pack-a", 1, true));

        await relay.RedeemAsync(points: 0);
        await relay.RedeemAsync(points: -5000);

        Assert.Equal(0, await relay.GetBalanceAsync());
    }
}
