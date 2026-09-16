using System.Net;
using System.Text.Json;

namespace TwitchRelay.Tests;

/// <summary>
/// The snapshot is where a summon's price comes from, and the stream key that addresses it is
/// public - Twitch serves it to every viewer's browser in the extension's broadcaster config. So
/// these are payment tests as much as transport ones: whoever can write a snapshot can set what
/// every pack costs.
/// </summary>
public class SnapshotIngestTests
{
    private static async Task<HttpStatusCode> ReadSnapshotStatusAsync(RelayHarness relay, string streamKey)
    {
        // The socket write returns before the relay has processed it; give it room to be wrong.
        await Task.Delay(250);
        return (await relay.Client.GetAsync("/api/character/" + streamKey)).StatusCode;
    }

    [Fact]
    public async Task A_socket_that_never_handshook_cannot_publish_a_snapshot()
    {
        await using var relay = await RelayHarness.StartAsync();

        // Knows the (public) stream key, and nothing else.
        await using var intruder = await relay.ConnectOverlayAsync(login: null);
        await intruder.SendAsync(JsonSerializer.Serialize(new
        {
            party = Array.Empty<object>(),
            packs = new[] { new { id = "free-pack", name = "free", from = 1, to = 40, cost = 0, available = true } }
        }));

        Assert.Equal(HttpStatusCode.NotFound, await ReadSnapshotStatusAsync(relay, RelayHarness.StreamKey));
    }

    [Fact]
    public async Task A_live_stream_key_cannot_be_taken_over_without_the_control_key()
    {
        await using var relay = await RelayHarness.StartAsync();
        await using var overlay = await relay.ConnectOverlayAsync();
        await overlay.PublishPacksAsync(("pack-a", 6, true));

        // Same public stream key, a control key of the attacker's own choosing.
        await using var intruder = await relay.ConnectOverlayAsync(controlKey: "attackerkey01", login: "streamer");
        Assert.True(await intruder.WasClosedByRelayAsync());

        await intruder.TrySendAsync(JsonSerializer.Serialize(new
        {
            party = Array.Empty<object>(),
            packs = new[] { new { id = "pack-a", name = "a", from = 1, to = 40, cost = 0, available = true } }
        }));
        await Task.Delay(250);

        // The real streamer's prices still stand.
        var served = await relay.Client.GetStringAsync("/api/character/" + RelayHarness.StreamKey);
        var packs = JsonDocument.Parse(served).RootElement.GetProperty("packs");
        Assert.Equal(6, packs[0].GetProperty("cost").GetInt32());
    }

    /// <summary>
    /// The attack the takeover rule exists to stop, spelled out: reprice every pack to nothing and
    /// summon for free.
    /// </summary>
    [Fact]
    public async Task Repricing_a_pack_from_outside_cannot_buy_a_free_summon()
    {
        await using var relay = await RelayHarness.StartAsync();
        await using var overlay = await relay.ConnectOverlayAsync();
        await overlay.PublishPacksAsync(("pack-a", 6, true));

        await using var intruder = await relay.ConnectOverlayAsync(controlKey: "attackerkey01", login: "streamer");
        await intruder.TrySendAsync(JsonSerializer.Serialize(new
        {
            party = Array.Empty<object>(),
            packs = new[] { new { id = "pack-a", name = "a", from = 1, to = 40, cost = 0, available = true } }
        }));
        await Task.Delay(250);

        // A viewer with no tokens at all still cannot summon.
        var response = await relay.PostSummonAsync(new
        {
            packs = new[] { "pack-a" },
            authToken = RelayHarness.ViewerToken()
        });

        Assert.Equal(HttpStatusCode.PaymentRequired, response.StatusCode);
        Assert.Null(await overlay.ReceiveCommandAsync());
    }

    /// <summary>
    /// The other half of that rule: the real overlay reconnects constantly (network blips, the
    /// game restarting) and must always be able to take its own stream key back - including
    /// while the relay still believes the dropped connection is live.
    /// </summary>
    [Fact]
    public async Task The_real_overlay_can_reconnect_and_still_receive_summons()
    {
        await using var relay = await RelayHarness.StartAsync();

        var first = await relay.ConnectOverlayAsync();
        await first.PublishPacksAsync(("pack-a", 1, true));

        // Credit before reconnecting, so the summon below has something to spend.
        await relay.PostBitsAsync(new
        {
            receipts = new[] { FakeTwitch.BitsReceiptToken(RelayHarness.ExtensionSecret, "txn-reconnect", RelayHarness.ViewerId, 500) },
            authToken = RelayHarness.ViewerToken()
        });

        await using var reconnected = await relay.ConnectOverlayAsync();
        await reconnected.PublishPacksAsync(("pack-a", 1, true));
        await first.DisposeAsync(); // the old connection finally notices it is gone

        var response = await relay.PostSummonAsync(new
        {
            packs = new[] { "pack-a" },
            authToken = RelayHarness.ViewerToken()
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(await reconnected.ReceiveCommandAsync());
    }

    /// <summary>
    /// A cost that isn't a whole number - or isn't a number at all - used to throw something the
    /// surrounding catch didn't hold, turning a snapshot into a 500 on every summon.
    /// </summary>
    [Fact]
    public async Task A_malformed_pack_cost_is_dropped_rather_than_faulting_the_summon()
    {
        await using var relay = await RelayHarness.StartAsync();
        await using var overlay = await relay.ConnectOverlayAsync();
        await overlay.PublishRawAsync("""
            {"party":[],"packs":[
              {"id":"huge","name":"huge","cost":1e309,"available":true},
              {"id":"fractional","name":"fractional","cost":1.5,"available":true},
              {"id":"texty","name":"texty","cost":"free","available":true},
              {"id":"real","name":"real","cost":2,"available":true}
            ]}
            """);

        var response = await relay.PostSummonAsync(new
        {
            packs = new[] { "huge", "fractional", "texty" },
            authToken = RelayHarness.ViewerToken()
        });

        // Every id resolved to nothing - a bad request, not a server fault.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(await overlay.ReceiveCommandAsync());
    }
}
