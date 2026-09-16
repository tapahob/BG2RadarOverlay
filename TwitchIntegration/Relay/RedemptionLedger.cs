using System.Collections.Concurrent;
using System.Text.Json;

namespace TwitchRelay;

/// <summary>
/// What each Channel Points redemption actually paid out, kept so a refund can take back exactly
/// that much and no more.
///
/// A redemption that sits in a streamer's request queue can be refunded later, and Twitch reports
/// that as a separate `.update` event carrying only the redemption's id - not what it was worth.
/// The price could also have changed in between, so recomputing from the current config would take
/// back the wrong number. This is the only record of what was credited at the time.
///
/// Entries are removed once the redemption reaches a terminal state, and expire regardless: a
/// redemption nobody ever resolved is not worth remembering forever.
/// </summary>
public sealed class RedemptionLedger
{
    public sealed record Entry(string BroadcasterId, string UserId, int Tokens, DateTime ExpiresAtUtc);

    private readonly string path;
    private readonly SemaphoreSlim gate = new(1, 1);
    private ConcurrentDictionary<string, Entry>? entries;

    public RedemptionLedger(string path)
    {
        this.path = path;
    }

    public async Task RecordAsync(string redemptionId, string broadcasterId, string userId, int tokens, DateTime expiresAtUtc)
    {
        if (string.IsNullOrEmpty(redemptionId) || tokens <= 0)
            return;

        await gate.WaitAsync();
        try
        {
            var all = await loadAsync();
            prune(all);
            all[redemptionId] = new Entry(broadcasterId, userId, tokens, expiresAtUtc);
            await persistAsync(all);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Reads an entry and removes it in one step - a redemption can only be refunded once, and
    /// leaving it behind would let a repeated cancellation debit the same tokens again.
    /// </summary>
    public async Task<Entry?> TakeAsync(string redemptionId)
    {
        if (string.IsNullOrEmpty(redemptionId))
            return null;

        await gate.WaitAsync();
        try
        {
            var all = await loadAsync();
            if (!all.TryRemove(redemptionId, out var entry))
                return null;
            await persistAsync(all);
            return entry;
        }
        finally
        {
            gate.Release();
        }
    }

    private static void prune(ConcurrentDictionary<string, Entry> all)
    {
        var now = DateTime.UtcNow;
        foreach (var stale in all.Where(kv => kv.Value.ExpiresAtUtc < now).Select(kv => kv.Key).ToList())
            all.TryRemove(stale, out _);
    }

    private async Task<ConcurrentDictionary<string, Entry>> loadAsync()
    {
        if (entries is not null)
            return entries;

        entries = new ConcurrentDictionary<string, Entry>();
        if (!File.Exists(path))
            return entries;

        try
        {
            var loaded = JsonSerializer.Deserialize<Dictionary<string, Entry>>(await File.ReadAllTextAsync(path));
            if (loaded is not null)
            {
                foreach (var kv in loaded)
                    entries[kv.Key] = kv.Value;
            }
        }
        catch (JsonException)
        {
            AtomicFile.Quarantine(path);
        }
        return entries;
    }

    private Task persistAsync(ConcurrentDictionary<string, Entry> all)
        => AtomicFile.WriteAllTextAsync(path, JsonSerializer.Serialize(all.ToDictionary(kv => kv.Key, kv => kv.Value)));
}
