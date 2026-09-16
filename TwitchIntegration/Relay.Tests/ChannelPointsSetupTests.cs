using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace TwitchRelay.Tests;

/// <summary>
/// The one-time flow each streamer runs before Channel Points can pay for anything: authorize as
/// themselves, and let the relay register an EventSub subscription for their redemptions. It is
/// the only per-streamer step in the whole Twitch setup, and the only part of the payment system
/// where getting it wrong is silent - a subscription that was never created, or was created
/// twice, shows up as redemptions that credit nothing or credit double.
/// </summary>
public class ChannelPointsSetupTests
{
    private static readonly System.Text.RegularExpressions.Regex StatePattern = new("[?&]state=([a-f0-9]{32})");

    /// <summary>Follows /oauth/authorize far enough to learn the state it minted.</summary>
    private static async Task<string> BeginAuthorizationAsync(RelayHarness relay)
    {
        var response = await relay.Client.GetAsync("/oauth/authorize");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var location = response.Headers.Location!.ToString();
        Assert.Contains("channel%3Aread%3Aredemptions", location); // the scope redemptions need
        var match = StatePattern.Match(location);
        Assert.True(match.Success, "no state in the authorize redirect: " + location);
        return match.Groups[1].Value;
    }

    /// <summary>
    /// Two subscriptions, not one: redemptions being created is what credits tokens, and
    /// redemptions being resolved is what takes them back when the streamer refunds one.
    /// </summary>
    [Fact]
    public async Task Authorizing_registers_both_redemption_subscriptions()
    {
        await using var relay = await RelayHarness.StartAsync();

        var state = await BeginAuthorizationAsync(relay);
        var response = await relay.Client.GetAsync($"/oauth/callback?code=fake-code&state={state}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Channel Points redemptions are live", await response.Content.ReadAsStringAsync());

        Assert.Equal(
            new[]
            {
                "channel.channel_points_custom_reward_redemption.add",
                "channel.channel_points_custom_reward_redemption.update"
            },
            relay.Twitch.Subscriptions.Select(sub => sub.Type).OrderBy(t => t).ToArray());

        foreach (var subscription in relay.Twitch.Subscriptions)
        {
            Assert.Equal(RelayHarness.BroadcasterId, subscription.BroadcasterId);
            Assert.Equal(RelayHarness.EventSubCallbackUrl, subscription.Callback);
            Assert.Equal(RelayHarness.WebhookSecret, subscription.Secret);
        }
    }

    /// <summary>
    /// A streamer who set Channel Points up before refunds were handled has only the first
    /// subscription. Re-authorizing has to pick up the second, and the status endpoint has to say
    /// so rather than reporting them as fully set up.
    /// </summary>
    [Fact]
    public async Task A_setup_missing_the_refund_subscription_reads_as_incomplete_until_reauthorized()
    {
        await using var relay = await RelayHarness.StartAsync();

        // As an older version of this relay would have left it.
        relay.Twitch.Subscriptions.Add(new FakeTwitch.CreatedSubscription(
            "sub-legacy", "channel.channel_points_custom_reward_redemption.add",
            RelayHarness.BroadcasterId, RelayHarness.EventSubCallbackUrl, RelayHarness.WebhookSecret));

        var before = await relay.Client.GetFromJsonAsync<JsonElement>($"/api/eventsub-status/{RelayHarness.BroadcasterId}");
        Assert.False(before.GetProperty("authorized").GetBoolean());
        Assert.True(before.GetProperty("redemptionsCredit").GetBoolean());   // crediting still works
        Assert.False(before.GetProperty("refundsClawBack").GetBoolean());    // refunds do not
        Assert.Contains("channel.channel_points_custom_reward_redemption.update",
            before.GetProperty("missing").EnumerateArray().Select(e => e.GetString()));

        var state = await BeginAuthorizationAsync(relay);
        await relay.Client.GetAsync($"/oauth/callback?code=fake-code&state={state}");

        var after = await relay.Client.GetFromJsonAsync<JsonElement>($"/api/eventsub-status/{RelayHarness.BroadcasterId}");
        Assert.True(after.GetProperty("authorized").GetBoolean());
        Assert.True(after.GetProperty("refundsClawBack").GetBoolean());
    }

    /// <summary>
    /// Twitch never discloses the secret a subscription was created with, so a relay that lost its
    /// own copy cannot tell that every delivery is now being rejected. Re-creating on authorize is
    /// what makes that recoverable - and the test for it is that the subscriptions left behind
    /// carry the secret the relay holds *now*.
    /// </summary>
    [Fact]
    public async Task Reauthorizing_replaces_subscriptions_that_were_signed_with_a_lost_secret()
    {
        await using var relay = await RelayHarness.StartAsync();

        // A subscription from before the secret was lost: Twitch reports it as enabled, but it
        // signs with something this relay can no longer verify.
        relay.Twitch.Subscriptions.Add(new FakeTwitch.CreatedSubscription(
            "sub-stale", "channel.channel_points_custom_reward_redemption.add",
            RelayHarness.BroadcasterId, RelayHarness.EventSubCallbackUrl, "a-secret-this-relay-no-longer-has"));

        // As things stand, a genuine redemption from Twitch is rejected outright.
        var rejected = await relay.PostEventSubAsync(
            FakeTwitch.RedemptionBody(RelayHarness.BroadcasterId, RelayHarness.ViewerId, RelayHarness.RewardName, 1000),
            secret: "a-secret-this-relay-no-longer-has");
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);

        var state = await BeginAuthorizationAsync(relay);
        var response = await relay.Client.GetAsync($"/oauth/callback?code=fake-code&state={state}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Contains("sub-stale", relay.Twitch.DeletedSubscriptionIds);
        Assert.All(relay.Twitch.Subscriptions, sub => Assert.Equal(RelayHarness.WebhookSecret, sub.Secret));

        // And redemptions credit again.
        await using var overlay = await relay.ConnectOverlayAsync();
        await overlay.PublishPacksAsync(("pack-a", 1, true));
        await relay.RedeemAsync(points: 1000);
        Assert.Equal(2, await relay.GetBalanceAsync());
    }

    /// <summary>
    /// The count of deliveries turned away for a bad signature is what makes that failure visible
    /// at all - config.html has no other way to tell "healthy" from "silently rejecting
    /// everything", because Twitch reports the subscription as enabled either way.
    /// </summary>
    [Fact]
    public async Task Rejected_deliveries_are_reported_so_a_lost_secret_is_not_silent()
    {
        await using var relay = await RelayHarness.StartAsync();

        var before = await relay.Client.GetFromJsonAsync<JsonElement>($"/api/eventsub-status/{RelayHarness.BroadcasterId}");
        Assert.Equal(0, before.GetProperty("rejectedDeliveries").GetInt32());

        var body = FakeTwitch.RedemptionBody(RelayHarness.BroadcasterId, RelayHarness.ViewerId, RelayHarness.RewardName, 1000);
        await relay.PostEventSubAsync(body, secret: "wrong-secret");
        await relay.PostEventSubAsync(body, secret: "wrong-secret");

        var after = await relay.Client.GetFromJsonAsync<JsonElement>($"/api/eventsub-status/{RelayHarness.BroadcasterId}");
        Assert.Equal(2, after.GetProperty("rejectedDeliveries").GetInt32());
        Assert.NotNull(after.GetProperty("lastRejectedDeliveryUtc").GetString());
    }

    /// <summary>
    /// Streamers re-run this - they lose the tab, they are not sure it worked, they set the relay
    /// up again. A second subscription would make Twitch deliver every redemption twice, and each
    /// delivery carries its own message id, so the duplicate guard would not catch it: the viewer
    /// would simply get double tokens for every redemption, forever.
    /// </summary>
    [Fact]
    public async Task Authorizing_twice_does_not_leave_duplicate_subscriptions()
    {
        await using var relay = await RelayHarness.StartAsync();

        var firstState = await BeginAuthorizationAsync(relay);
        await relay.Client.GetAsync($"/oauth/callback?code=fake-code&state={firstState}");
        var firstIds = relay.Twitch.Subscriptions.Select(sub => sub.Id).ToArray();

        var secondState = await BeginAuthorizationAsync(relay);
        var second = await relay.Client.GetAsync($"/oauth/callback?code=fake-code&state={secondState}");

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        // Still exactly the two types, and the originals were replaced rather than added to -
        // a duplicate subscription would make Twitch deliver every redemption twice, each with
        // its own message id, so the duplicate guard would not catch it and every viewer would
        // quietly get double tokens forever.
        Assert.Equal(2, relay.Twitch.Subscriptions.Count);
        Assert.Equal(2, relay.Twitch.Subscriptions.Select(sub => sub.Type).Distinct().Count());
        Assert.All(firstIds, id => Assert.Contains(id, relay.Twitch.DeletedSubscriptionIds));
    }

    [Fact]
    public async Task A_callback_with_a_state_nobody_asked_for_is_refused()
    {
        await using var relay = await RelayHarness.StartAsync();

        var response = await relay.Client.GetAsync("/oauth/callback?code=fake-code&state=deadbeefdeadbeefdeadbeefdeadbeef");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(relay.Twitch.Subscriptions);
    }

    [Fact]
    public async Task A_state_cannot_be_used_twice()
    {
        await using var relay = await RelayHarness.StartAsync();

        var state = await BeginAuthorizationAsync(relay);
        var first = await relay.Client.GetAsync($"/oauth/callback?code=fake-code&state={state}");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var replayed = await relay.Client.GetAsync($"/oauth/callback?code=fake-code&state={state}");
        Assert.Equal(HttpStatusCode.BadRequest, replayed.StatusCode);
    }

    /// <summary>
    /// Two streamers setting themselves up at the same time each hold their own state, and must
    /// not be able to complete each other's authorization.
    /// </summary>
    [Fact]
    public async Task Two_streamers_authorizing_at_once_each_get_their_own_state()
    {
        await using var relay = await RelayHarness.StartAsync();

        var first = await BeginAuthorizationAsync(relay);
        var second = await BeginAuthorizationAsync(relay);
        Assert.NotEqual(first, second);

        Assert.Equal(HttpStatusCode.OK, (await relay.Client.GetAsync($"/oauth/callback?code=a&state={first}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await relay.Client.GetAsync($"/oauth/callback?code=b&state={second}")).StatusCode);
    }

    [Fact]
    public async Task A_denied_authorization_reports_the_reason_and_registers_nothing()
    {
        await using var relay = await RelayHarness.StartAsync();

        var state = await BeginAuthorizationAsync(relay);
        var response = await relay.Client.GetAsync(
            $"/oauth/callback?state={state}&error=access_denied&error_description=The+user+denied+you+access");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("denied", await response.Content.ReadAsStringAsync());
        Assert.Empty(relay.Twitch.Subscriptions);
    }

    /// <summary>
    /// What config.html's "Authorize Channel Points" button reads to decide whether to show the
    /// button at all.
    /// </summary>
    [Fact]
    public async Task The_status_endpoint_reports_whether_this_channel_is_set_up()
    {
        await using var relay = await RelayHarness.StartAsync();

        var before = await relay.Client.GetFromJsonAsync<JsonElement>($"/api/eventsub-status/{RelayHarness.BroadcasterId}");
        Assert.True(before.GetProperty("configured").GetBoolean());
        Assert.False(before.GetProperty("authorized").GetBoolean());

        var state = await BeginAuthorizationAsync(relay);
        await relay.Client.GetAsync($"/oauth/callback?code=fake-code&state={state}");

        var after = await relay.Client.GetFromJsonAsync<JsonElement>($"/api/eventsub-status/{RelayHarness.BroadcasterId}");
        Assert.True(after.GetProperty("authorized").GetBoolean());
        Assert.Equal("enabled", after.GetProperty("status").GetString());
    }

    [Fact]
    public async Task The_status_endpoint_refuses_anything_that_is_not_a_broadcaster_id()
    {
        await using var relay = await RelayHarness.StartAsync();

        foreach (var candidate in new[] { "not-a-number", "12345abc", "../../etc/passwd", "" })
        {
            var response = await relay.Client.GetAsync("/api/eventsub-status/" + Uri.EscapeDataString(candidate));
            Assert.True(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound,
                $"'{candidate}' was answered with {response.StatusCode}");
        }
    }

    /// <summary>
    /// This route is unauthenticated - config.html asks before the broadcaster has anything to
    /// authenticate with - and every uncached call spends two Helix requests out of a quota shared
    /// by every streamer on the relay, including the calls that read token prices.
    /// </summary>
    [Fact]
    public async Task Repeated_status_checks_do_not_hammer_helix()
    {
        await using var relay = await RelayHarness.StartAsync();

        await relay.Client.GetAsync($"/api/eventsub-status/{RelayHarness.BroadcasterId}");
        var afterFirst = relay.Twitch.HelixCallCount;

        for (var i = 0; i < 20; i++)
            await relay.Client.GetAsync($"/api/eventsub-status/{RelayHarness.BroadcasterId}");

        Assert.Equal(afterFirst, relay.Twitch.HelixCallCount);
    }

    /// <summary>
    /// A redemption arriving for a channel that completed this flow is the end-to-end case: setup
    /// through to a spendable balance, with nothing stubbed but Twitch itself.
    /// </summary>
    [Fact]
    public async Task Setup_through_to_a_redemption_credits_the_viewer()
    {
        await using var relay = await RelayHarness.StartAsync();
        await using var overlay = await relay.ConnectOverlayAsync();
        await overlay.PublishPacksAsync(("pack-a", 2, true));

        var state = await BeginAuthorizationAsync(relay);
        await relay.Client.GetAsync($"/oauth/callback?code=fake-code&state={state}");

        // Twitch verifies the callback before it starts delivering.
        var verification = await relay.PostEventSubAsync(
            JsonSerializer.Serialize(new { challenge = "verify-me", subscription = new { id = "sub-1" } }),
            messageType: "webhook_callback_verification",
            secret: relay.Twitch.Subscriptions.First().Secret);
        Assert.Equal("verify-me", await verification.Content.ReadAsStringAsync());

        await relay.RedeemAsync(points: 1500);

        Assert.Equal(3, await relay.GetBalanceAsync());
    }
}
