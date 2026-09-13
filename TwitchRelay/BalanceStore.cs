using System.Text.Json;

namespace TwitchRelay;

/// <summary>
/// Per-viewer summon token balances - credited by a verified Bits purchase or Channel Points
/// redemption, spent in one shot by /api/summon on however many packs a viewer picked. Persisted
/// to a flat JSON file so a relay restart doesn't erase a balance someone already paid real
/// Bits or points for.
/// </summary>
public sealed class BalanceStore
{
    private readonly string path;
    private readonly SemaphoreSlim gate = new(1, 1);
    private Dictionary<string, int>? cached;

    public BalanceStore(string path)
    {
        this.path = path;
    }

    public async Task<int> GetBalanceAsync(string userId)
    {
        await gate.WaitAsync();
        try
        {
            var balances = await loadAsync();
            return balances.TryGetValue(userId, out var value) ? value : 0;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<int> CreditAsync(string userId, int amount)
    {
        if (amount <= 0)
            return await GetBalanceAsync(userId);

        await gate.WaitAsync();
        try
        {
            var balances = await loadAsync();
            var updated = balances.GetValueOrDefault(userId) + amount;
            balances[userId] = updated;
            await persistAsync(balances);
            return updated;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Atomic check-and-deduct: true only if the balance actually covered the cost, in which case
    /// it has already been spent by the time this returns. False leaves the balance untouched -
    /// callers must not dispatch a summon on a false result.
    /// </summary>
    public async Task<bool> TrySpendAsync(string userId, int amount)
    {
        if (amount <= 0)
            return true;

        await gate.WaitAsync();
        try
        {
            var balances = await loadAsync();
            var current = balances.GetValueOrDefault(userId);
            if (current < amount)
                return false;

            balances[userId] = current - amount;
            await persistAsync(balances);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<Dictionary<string, int>> loadAsync()
    {
        if (cached is not null)
            return cached;

        if (!File.Exists(path))
        {
            cached = new Dictionary<string, int>();
            return cached;
        }

        try
        {
            cached = JsonSerializer.Deserialize<Dictionary<string, int>>(await File.ReadAllTextAsync(path))
                      ?? new Dictionary<string, int>();
        }
        catch (JsonException)
        {
            cached = new Dictionary<string, int>();
        }
        return cached;
    }

    private async Task persistAsync(Dictionary<string, int> balances)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(balances));
    }
}
