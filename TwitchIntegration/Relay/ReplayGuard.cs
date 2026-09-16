using System.Collections.Concurrent;
using System.Text.Json;

namespace TwitchRelay;

/// <summary>
/// One-shot tokens that must never be honoured twice: spent Bits transaction ids, and EventSub
/// message ids Twitch has already delivered. Both credit a balance someone pays real money for,
/// so "we already handled this one" has to survive the thing that used to erase it - a restart.
/// Redeploying this relay is routine (see CLAUDE.md), and an in-memory set meant every receipt a
/// viewer's browser still held could be posted again the moment the process came back.
///
/// Each entry is filed with the moment it stops mattering - a Bits receipt's own `exp`, or the
/// end of the EventSub replay window - and pruning only ever removes entries past that point, so
/// there is no gap where something still verifies but is no longer remembered as spent.
/// </summary>
public sealed class ReplayGuard
{
    private readonly string path;
    private readonly SemaphoreSlim gate = new(1, 1);
    private ConcurrentDictionary<string, DateTime>? entries;

    public ReplayGuard(string path)
    {
        this.path = path;
    }

    /// <summary>
    /// True if this id had not been seen before (and is now recorded as used, on disk). False
    /// means it was already spent - the caller must credit nothing.
    /// </summary>
    public async Task<bool> TryClaimAsync(string id, DateTime expiresAtUtc)
    {
        if (string.IsNullOrEmpty(id))
            return false;

        await gate.WaitAsync();
        try
        {
            var all = await loadAsync();
            prune(all);
            if (!all.TryAdd(id, expiresAtUtc))
                return false;
            await persistAsync(all);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Gives a claimed id back, for when the credit it was claimed for could not be completed -
    /// otherwise a viewer whose top-up failed on our side would be left unable to retry with the
    /// receipt they actually paid for.
    /// </summary>
    public async Task ReleaseAsync(string id)
    {
        if (string.IsNullOrEmpty(id))
            return;

        await gate.WaitAsync();
        try
        {
            var all = await loadAsync();
            if (all.TryRemove(id, out _))
                await persistAsync(all);
        }
        finally
        {
            gate.Release();
        }
    }

    private static void prune(ConcurrentDictionary<string, DateTime> all)
    {
        var now = DateTime.UtcNow;
        foreach (var stale in all.Where(kv => kv.Value < now).Select(kv => kv.Key).ToList())
            all.TryRemove(stale, out _);
    }

    private async Task<ConcurrentDictionary<string, DateTime>> loadAsync()
    {
        if (entries is not null)
            return entries;

        entries = new ConcurrentDictionary<string, DateTime>();
        if (!File.Exists(path))
            return entries;

        try
        {
            var loaded = JsonSerializer.Deserialize<Dictionary<string, DateTime>>(await File.ReadAllTextAsync(path));
            if (loaded is not null)
            {
                foreach (var kv in loaded)
                    entries[kv.Key] = kv.Value;
            }
        }
        catch (JsonException)
        {
            // Unreadable. Starting from nothing does reopen the replay window for anything still
            // unexpired, so the broken file is moved aside intact rather than overwritten - it
            // is the only record of what had already been spent, and it is worth being able to
            // reconcile by hand. With AtomicFile writes this should not be reachable at all.
            AtomicFile.Quarantine(path);
        }
        return entries;
    }

    private Task persistAsync(ConcurrentDictionary<string, DateTime> all)
        => AtomicFile.WriteAllTextAsync(path, JsonSerializer.Serialize(all.ToDictionary(kv => kv.Key, kv => kv.Value)));
}

/// <summary>
/// Whole-file writes that either land completely or not at all. The money-carrying files here
/// (balances, spent receipts, OAuth tokens) were being written straight over the live copy: a
/// crash or a full disk mid-write left truncated JSON, which every loader then treated as "no
/// data" - wiping balances viewers had paid for.
/// </summary>
public static class AtomicFile
{
    public static async Task WriteAllTextAsync(string path, string contents)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, contents);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// Moves a file that would not parse out of the way instead of letting the next write bury
    /// it - whatever it held is still recoverable by hand.
    /// </summary>
    public static void Quarantine(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Move(path, path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"), overwrite: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
