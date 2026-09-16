using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace TwitchRelay.Tests;

/// <summary>
/// Buying summon tokens with Bits, end to end against a stub Twitch: the happy path, and every
/// way a viewer's browser could try to get tokens it didn't pay for. A receipt is just a signed
/// blob the browser holds, so "the signature checks out" is where trust *starts*, not ends -
/// most of what follows is about what else has to be true.
/// </summary>
public class BitsPurchaseTests
{
    private static string Receipt(string transactionId, int bits = 500, string? userId = null, byte[]? secret = null,
        bool includeExp = true, DateTimeOffset? expires = null)
        => FakeTwitch.BitsReceiptToken(secret ?? RelayHarness.ExtensionSecret, transactionId,
            userId ?? RelayHarness.ViewerId, bits, expires, includeExp);

    private static async Task<(int Credited, bool Duplicate, int Balance)> ReadCreditAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (json.GetProperty("credited").GetInt32(),
                json.GetProperty("duplicate").GetBoolean(),
                json.GetProperty("balance").GetInt32());
    }

    [Fact]
    public async Task Paying_bits_credits_tokens_at_the_streamers_configured_price()
    {
        await using var relay = await RelayHarness.StartAsync();
        await using var overlay = await relay.ConnectOverlayAsync();
        await overlay.PublishPacksAsync(("pack-a", 2, true));

        var response = await relay.PostBitsAsync(new
        {
            receipts = new[] { Receipt("txn-1", bits: 500) },
            authToken = RelayHarness.ViewerToken()
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var (credited, duplicate, balance) = await ReadCreditAsync(response);
        Assert.Equal(5, credited); // 500 bits at 100 bits/token
        Assert.False(duplicate);
        Assert.Equal(5, balance);
        Assert.Equal(5, await relay.GetBalanceAsync());
    }

    [Fact]
    public async Task Bits_that_dont_divide_evenly_round_down_rather_than_over_credit()
    {
        await using var relay = await RelayHarness.StartAsync();
        await using var overlay = await relay.ConnectOverlayAsync();
        await overlay.PublishPacksAsync(("pack-a", 1, true));

        var response = await relay.PostBitsAsync(new
        {
            receipts = new[] { Receipt("txn-round", bits: 250) },
            authToken = RelayHarness.ViewerToken()
        });

        var (credited, _, _) = await ReadCreditAsync(response);
        Assert.Equal(2, credited); // not 2.5, and not 3
    }

    /// <summary>
    /// The receipt lives in the viewer's browser, so it can be posted again at will. Crediting it
    /// twice would let anyone who bought once top up for free forever.
    /// </summary>
    [Fact]
    public async Task The_same_receipt_cannot_be_credited_twice()
    {
        await using var relay = await RelayHarness.StartAsync();
        await using var overlay = await relay.ConnectOverlayAsync();
        await overlay.PublishPacksAsync(("pack-a", 1, true));

        var receipt = Receipt("txn-replay", bits: 500);
        var body = new { receipts = new[] { receipt }, authToken = RelayHarness.ViewerToken() };

        var first = await ReadCreditAsync(await relay.PostBitsAsync(body));
        Assert.Equal(5, first.Credited);

        // Settled, not an error - the extension retries a receipt it never got confirmation for,
        // and has to be able to tell "already done" from "try again".
        var second = await ReadCreditAsync(await relay.PostBitsAsync(body));
        Assert.Equal(0, second.Credited);
        Assert.True(second.Duplicate);
        Assert.Equal(5, second.Balance);

        Assert.Equal(5, await relay.GetBalanceAsync());
    }

    /// <summary>
    /// The one that matters most: spent transaction ids used to live only in memory, so every
    /// redeploy - which is routine - handed every viewer's browser a fresh free top-up.
    /// </summary>
    [Fact]
    public async Task A_spent_receipt_is_still_spent_after_the_relay_restarts()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "relay-tests", Guid.NewGuid().ToString("N"));
        var receipt = Receipt("txn-survives-restart", bits: 1000);
        var body = new { receipts = new[] { receipt }, authToken = RelayHarness.ViewerToken() };

        await using (var relay = await RelayHarness.StartAsync(dataDir))
        {
            await using var overlay = await relay.ConnectOverlayAsync();
            await overlay.PublishPacksAsync(("pack-a", 1, true));
            var first = await ReadCreditAsync(await relay.PostBitsAsync(body));
            Assert.Equal(10, first.Credited);
        }

        // Same data directory, new process lifetime - a redeploy.
        await using (var restarted = await RelayHarness.StartAsync(dataDir))
        {
            await using var overlay = await restarted.ConnectOverlayAsync();
            await overlay.PublishPacksAsync(("pack-a", 1, true));

            var replay = await ReadCreditAsync(await restarted.PostBitsAsync(body));
            Assert.Equal(0, replay.Credited);
            Assert.True(replay.Duplicate);

            // And the balance the viewer paid for survived the restart too.
            Assert.Equal(10, await restarted.GetBalanceAsync());
        }
    }

    [Fact]
    public async Task A_receipt_signed_with_the_wrong_secret_credits_nothing()
    {
        await using var relay = await RelayHarness.StartAsync();
        await using var overlay = await relay.ConnectOverlayAsync();
        await overlay.PublishPacksAsync(("pack-a", 1, true));

        var response = await relay.PostBitsAsync(new
        {
            receipts = new[] { Receipt("txn-forged", bits: 100000, secret: RelayHarness.WrongSecret) },
            authToken = RelayHarness.ViewerToken()
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await relay.GetBalanceAsync());
    }

    /// <summary>
    /// A signed token with no expiry never stops being valid, which is what turns a receipt the
    /// browser still holds into an unlimited top-up once its replay record ages out.
    /// </summary>
    [Fact]
    public async Task A_receipt_with_no_expiry_credits_nothing()
    {
        await using var relay = await RelayHarness.StartAsync();
        await using var overlay = await relay.ConnectOverlayAsync();
        await overlay.PublishPacksAsync(("pack-a", 1, true));

        var response = await relay.PostBitsAsync(new
        {
            receipts = new[] { Receipt("txn-no-exp", includeExp: false) },
            authToken = RelayHarness.ViewerToken()
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await relay.GetBalanceAsync());
    }

    [Fact]
    public async Task An_expired_receipt_credits_nothing()
    {
        await using var relay = await RelayHarness.StartAsync();
        await using var overlay = await relay.ConnectOverlayAsync();
        await overlay.PublishPacksAsync(("pack-a", 1, true));

        var response = await relay.PostBitsAsync(new
        {
            receipts = new[] { Receipt("txn-expired", expires: DateTimeOffset.UtcNow.AddHours(-2)) },
            authToken = RelayHarness.ViewerToken()
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await relay.GetBalanceAsync());
    }

    /// <summary>
    /// The extension's signing secret is one value shared by every channel the extension runs on,
    /// so a genuine receipt proves a purchase happened - not whose channel it happened on. On a
    /// relay serving several streamers, that difference is worth real money: buy where tokens are
    /// dear, cash in where they're cheap.
    /// </summary>
    [Fact]
    public async Task A_receipt_cannot_be_cashed_in_on_a_different_streamers_channel()
    {
        await using var relay = await RelayHarness.StartAsync();
        await using var overlay = await relay.ConnectOverlayAsync();
        await overlay.PublishPacksAsync(("pack-a", 1, true));

        // Genuine token, genuine receipt - but issued while watching somebody else.
        var otherChannelToken = RelayHarness.ViewerToken(channelId: "77777");

        var response = await relay.PostBitsAsync(new
        {
            receipts = new[] { Receipt("txn-cross-channel", bits: 1000) },
            authToken = otherChannelToken
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, await relay.GetBalanceAsync());
    }

    [Fact]
    public async Task A_receipt_belonging_to_another_viewer_credits_nothing()
    {
        await using var relay = await RelayHarness.StartAsync();
        await using var overlay = await relay.ConnectOverlayAsync();
        await overlay.PublishPacksAsync(("pack-a", 1, true));

        // Somebody else's receipt, presented with my own identity token.
        var response = await relay.PostBitsAsync(new
        {
            receipts = new[] { Receipt("txn-someone-elses", bits: 1000, userId: "55555") },
            authToken = RelayHarness.ViewerToken()
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await relay.GetBalanceAsync());
    }

    [Fact]
    public async Task A_purchase_with_no_identity_token_credits_nothing()
    {
        await using var relay = await RelayHarness.StartAsync();
        await using var overlay = await relay.ConnectOverlayAsync();
        await overlay.PublishPacksAsync(("pack-a", 1, true));

        var response = await relay.PostBitsAsync(new { receipts = new[] { Receipt("txn-no-auth") } });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, await relay.GetBalanceAsync());
    }

    /// <summary>
    /// A viewer can buy Bits without sharing their identity - Twitch leaves user_id out of the
    /// token then, and the receipt's own id is the only one there is. The purchase still has to
    /// credit, or they've paid for nothing.
    /// </summary>
    [Fact]
    public async Task A_viewer_who_has_not_shared_identity_can_still_buy()
    {
        await using var relay = await RelayHarness.StartAsync();
        await using var overlay = await relay.ConnectOverlayAsync();
        await overlay.PublishPacksAsync(("pack-a", 1, true));

        var anonymousToken = FakeTwitch.ViewerToken(RelayHarness.ExtensionSecret, RelayHarness.BroadcasterId, userId: "");

        var response = await relay.PostBitsAsync(new
        {
            receipts = new[] { Receipt("txn-anon", bits: 300) },
            authToken = anonymousToken
        });

        var (credited, _, balance) = await ReadCreditAsync(response);
        Assert.Equal(3, credited);
        Assert.Equal(3, balance);

        // And it landed under their real id, so it's there once they do share it.
        Assert.Equal(3, await relay.GetBalanceAsync());
    }

    /// <summary>
    /// Every receipt costs a signature check. An unbounded array is free CPU for whoever posts it.
    /// </summary>
    [Fact]
    public async Task Only_the_first_ten_receipts_in_one_request_are_considered()
    {
        await using var relay = await RelayHarness.StartAsync();
        await using var overlay = await relay.ConnectOverlayAsync();
        await overlay.PublishPacksAsync(("pack-a", 1, true));

        var receipts = Enumerable.Range(0, 25).Select(i => Receipt("txn-bulk-" + i, bits: 100)).ToArray();

        var (credited, _, _) = await ReadCreditAsync(await relay.PostBitsAsync(new
        {
            receipts,
            authToken = RelayHarness.ViewerToken()
        }));

        Assert.Equal(10, credited); // ten receipts at 100 bits = 1 token each
    }

    [Fact]
    public async Task A_purchase_against_an_unknown_stream_key_credits_nothing()
    {
        await using var relay = await RelayHarness.StartAsync();

        var response = await relay.PostBitsAsync(new
        {
            receipts = new[] { Receipt("txn-nokey") },
            authToken = RelayHarness.ViewerToken()
        }, streamKey: "nosuchkey0001");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
