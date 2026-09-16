using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace TwitchRelay.Tests;

/// <summary>
/// Spending the tokens: what a summon costs, who is allowed to spend, and - the half that is easy
/// to forget is also a payment bug - that a viewer is never charged for a summon the overlay was
/// never told to perform.
/// </summary>
public class SummonSpendTests
{
    /// <summary>Credits a viewer by actually buying, so nothing here tests against a balance that could not exist.</summary>
    private static async Task GiveTokensAsync(RelayHarness relay, int tokens, string transactionId = "txn-setup")
    {
        var response = await relay.PostBitsAsync(new
        {
            receipts = new[] { FakeTwitch.BitsReceiptToken(RelayHarness.ExtensionSecret, transactionId, RelayHarness.ViewerId, tokens * RelayHarness.BitsPerToken) },
            authToken = RelayHarness.ViewerToken()
        });
        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task A_summon_debits_the_pack_cost_and_reaches_the_overlay()
    {
        await using var relay = await RelayHarness.StartAsync();
        await using var overlay = await relay.ConnectOverlayAsync();
        await overlay.PublishPacksAsync(("pack-a", 3, true), ("pack-b", 4, true));
        await GiveTokensAsync(relay, 10);

        var response = await relay.PostSummonAsync(new
        {
            packs = new[] { "pack-a", "pack-b" },
            message = "for the horde",
            authToken = RelayHarness.ViewerToken()
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("balance").GetInt32());

        var command = await overlay.ReceiveCommandAsync();
        Assert.NotNull(command);
        var parsed = JsonDocument.Parse(command!).RootElement;
        Assert.Equal("summon", parsed.GetProperty("type").GetString());
        Assert.Equal(new[] { "pack-a", "pack-b" }, parsed.GetProperty("packs").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal("for the horde", parsed.GetProperty("message").GetString());
    }

    [Fact]
    public async Task A_summon_that_costs_more_than_the_balance_is_refused_and_dispatches_nothing()
    {
        await using var relay = await RelayHarness.StartAsync();
        await using var overlay = await relay.ConnectOverlayAsync();
        await overlay.PublishPacksAsync(("pack-a", 9, true));
        await GiveTokensAsync(relay, 2);

        var response = await relay.PostSummonAsync(new { packs = new[] { "pack-a" }, authToken = RelayHarness.ViewerToken() });

        Assert.Equal(HttpStatusCode.PaymentRequired, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("insufficient_balance", body.GetProperty("error").GetString());
        Assert.Equal(9, body.GetProperty("required").GetInt32());

        Assert.Null(await overlay.ReceiveCommandAsync());
        Assert.Equal(2, await relay.GetBalanceAsync()); // untouched
    }

    /// <summary>
    /// The viewer's browser sends pack ids, never prices. If it could name its own total, every
    /// summon would be free.
    /// </summary>
    [Fact]
    public async Task A_cost_named_by_the_caller_is_ignored_in_favour_of_the_streamers_price()
    {
        await using var relay = await RelayHarness.StartAsync();
        await using var overlay = await relay.ConnectOverlayAsync();
        await overlay.PublishPacksAsync(("pack-a", 8, true));
        await GiveTokensAsync(relay, 10);

        await relay.PostSummonAsync(new
        {
            packs = new[] { "pack-a" },
            cost = 0,
            total = 0,
            authToken = RelayHarness.ViewerToken()
        });

        Assert.Equal(2, await relay.GetBalanceAsync()); // charged 8, not 0
    }

    /// <summary>
    /// The overlay drops packs outside the protagonist's level band when the command arrives, so
    /// charging for one takes tokens for a summon that provably never happens.
    /// </summary>
    [Fact]
    public async Task A_pack_the_overlay_would_skip_is_not_charged_for()
    {
        await using var relay = await RelayHarness.StartAsync();
        await using var overlay = await relay.ConnectOverlayAsync();
        await overlay.PublishPacksAsync(("pack-ok", 2, true), ("pack-too-high", 7, false));
        await GiveTokensAsync(relay, 10);

        await relay.PostSummonAsync(new
        {
            packs = new[] { "pack-ok", "pack-too-high" },
            authToken = RelayHarness.ViewerToken()
        });

        Assert.Equal(8, await relay.GetBalanceAsync()); // charged 2, not 9

        var parsed = JsonDocument.Parse((await overlay.ReceiveCommandAsync())!).RootElement;
        Assert.Equal(new[] { "pack-ok" }, parsed.GetProperty("packs").EnumerateArray().Select(e => e.GetString()).ToArray());
    }

    /// <summary>
    /// Only the first eight packs are ever forwarded, so a longer list must not be billed for.
    /// </summary>
    [Fact]
    public async Task A_summon_is_only_charged_for_the_packs_that_are_actually_dispatched()
    {
        await using var relay = await RelayHarness.StartAsync();
        await using var overlay = await relay.ConnectOverlayAsync();
        var packs = Enumerable.Range(0, 12).Select(i => ("pack-" + i, 1, true)).ToArray();
        await overlay.PublishPacksAsync(packs);
        await GiveTokensAsync(relay, 20);

        await relay.PostSummonAsync(new
        {
            packs = packs.Select(p => p.Item1).ToArray(),
            authToken = RelayHarness.ViewerToken()
        });

        Assert.Equal(12, await relay.GetBalanceAsync()); // charged for 8, the number dispatched

        var parsed = JsonDocument.Parse((await overlay.ReceiveCommandAsync())!).RootElement;
        Assert.Equal(8, parsed.GetProperty("packs").GetArrayLength());
    }

    /// <summary>
    /// A non-positive cost is treated as free, so one negative pack in a combo would pay for the
    /// rest of it - and a snapshot is not as trustworthy as it looks (see SnapshotIngestTests).
    /// </summary>
    [Fact]
    public async Task A_negatively_priced_pack_cannot_pay_for_the_rest_of_a_combo()
    {
        await using var relay = await RelayHarness.StartAsync();
        await using var overlay = await relay.ConnectOverlayAsync();
        await overlay.PublishPacksAsync(("pack-dear", 10, true), ("pack-negative", -100, true));
        await GiveTokensAsync(relay, 3);

        var response = await relay.PostSummonAsync(new
        {
            packs = new[] { "pack-dear", "pack-negative" },
            authToken = RelayHarness.ViewerToken()
        });

        Assert.Equal(HttpStatusCode.PaymentRequired, response.StatusCode);
        Assert.Null(await overlay.ReceiveCommandAsync());
        Assert.Equal(3, await relay.GetBalanceAsync());
    }

    [Fact]
    public async Task A_summon_authorized_for_a_different_channel_is_refused()
    {
        await using var relay = await RelayHarness.StartAsync();
        await using var overlay = await relay.ConnectOverlayAsync();
        await overlay.PublishPacksAsync(("pack-a", 1, true));
        await GiveTokensAsync(relay, 5);

        var response = await relay.PostSummonAsync(new
        {
            packs = new[] { "pack-a" },
            authToken = RelayHarness.ViewerToken(channelId: "77777")
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(await overlay.ReceiveCommandAsync());
        Assert.Equal(5, await relay.GetBalanceAsync());
    }

    [Fact]
    public async Task A_summon_with_a_forged_identity_token_is_refused()
    {
        await using var relay = await RelayHarness.StartAsync();
        await using var overlay = await relay.ConnectOverlayAsync();
        await overlay.PublishPacksAsync(("pack-a", 1, true));
        await GiveTokensAsync(relay, 5);

        var response = await relay.PostSummonAsync(new
        {
            packs = new[] { "pack-a" },
            authToken = FakeTwitch.ViewerToken(RelayHarness.WrongSecret, RelayHarness.BroadcasterId, RelayHarness.ViewerId)
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(await overlay.ReceiveCommandAsync());
        Assert.Equal(5, await relay.GetBalanceAsync());
    }

    /// <summary>
    /// Balances are keyed per channel as well as per viewer, so tokens bought on one streamer's
    /// channel are not spendable on another's.
    /// </summary>
    [Fact]
    public async Task Tokens_bought_on_one_channel_are_not_visible_on_another()
    {
        await using var relay = await RelayHarness.StartAsync();
        await using var overlay = await relay.ConnectOverlayAsync();
        await overlay.PublishPacksAsync(("pack-a", 1, true));
        await GiveTokensAsync(relay, 7);

        // A second streamer, same relay, same viewer.
        relay.Twitch.Logins["otherstreamer"] = "77777";
        relay.Twitch.SetPrices("77777", RelayHarness.BitsPerToken);
        await using var otherOverlay = await relay.ConnectOverlayAsync("streamkey0002", "controlkey0002", "otherstreamer");
        await otherOverlay.PublishPacksAsync(("pack-a", 1, true));

        var response = await relay.PostSummonAsync(new
        {
            packs = new[] { "pack-a" },
            authToken = RelayHarness.ViewerToken(channelId: "77777")
        }, streamKey: "streamkey0002");

        Assert.Equal(HttpStatusCode.PaymentRequired, response.StatusCode);
        Assert.Null(await otherOverlay.ReceiveCommandAsync());
        Assert.Equal(7, await relay.GetBalanceAsync()); // the original channel's balance is untouched
    }

    /// <summary>
    /// The viewer's message ends up in the streamer's game through a hand-rolled parser, so it is
    /// rebuilt rather than forwarded - quotes and backslashes could otherwise close their own field.
    /// </summary>
    [Fact]
    public async Task A_viewer_message_is_stripped_of_anything_that_could_forge_a_command()
    {
        await using var relay = await RelayHarness.StartAsync();
        await using var overlay = await relay.ConnectOverlayAsync();
        await overlay.PublishPacksAsync(("pack-a", 1, true));
        await GiveTokensAsync(relay, 5);

        await relay.PostSummonAsync(new
        {
            packs = new[] { "pack-a" },
            message = "hi\",\"resref\":\"BALOR\\",
            authToken = RelayHarness.ViewerToken()
        });

        var command = (await overlay.ReceiveCommandAsync())!;
        var parsed = JsonDocument.Parse(command).RootElement;
        var message = parsed.GetProperty("message").GetString()!;
        Assert.DoesNotContain('"', message);
        Assert.DoesNotContain('\\', message);
        Assert.False(parsed.TryGetProperty("resref", out _));
    }

    [Fact]
    public async Task Reading_a_balance_requires_a_token_for_that_channel()
    {
        await using var relay = await RelayHarness.StartAsync();
        await using var overlay = await relay.ConnectOverlayAsync();
        await overlay.PublishPacksAsync(("pack-a", 1, true));
        await GiveTokensAsync(relay, 4);

        var wrongChannel = await relay.Client.PostAsJsonAsync("/api/balance/" + RelayHarness.StreamKey,
            new { authToken = RelayHarness.ViewerToken(channelId: "77777") });
        Assert.Equal(HttpStatusCode.Unauthorized, wrongChannel.StatusCode);

        var forged = await relay.Client.PostAsJsonAsync("/api/balance/" + RelayHarness.StreamKey,
            new { authToken = FakeTwitch.ViewerToken(RelayHarness.WrongSecret, RelayHarness.BroadcasterId, RelayHarness.ViewerId) });
        Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);

        Assert.Equal(4, await relay.GetBalanceAsync());
    }
}
