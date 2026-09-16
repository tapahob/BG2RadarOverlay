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
    /// Credits what will fit under a ceiling and reports how much that actually was - which can
    /// be less than asked for, or nothing at all. The check and the write are one step on
    /// purpose: two redemptions arriving together would otherwise both read the same
    /// under-the-limit balance and both credit in full, putting the viewer over it.
    ///
    /// A ceiling of zero means there isn't one. Callers must use the returned figure rather than
    /// what they asked for - it is what the viewer was actually given, and what a refund of that
    /// redemption has to take back.
    /// </summary>
    public async Task<int> CreditUpToAsync(string userId, int amount, int ceiling)
    {
        if (amount <= 0)
            return 0;

        if (ceiling <= 0)
        {
            await CreditAsync(userId, amount);
            return amount;
        }

        await gate.WaitAsync();
        try
        {
            var balances = await loadAsync();
            var current = balances.GetValueOrDefault(userId);
            // Room is measured from where the viewer actually is, which can be below zero after a
            // refund - a debt has to be climbed out of before the ceiling starts to bite.
            var room = ceiling - current;
            if (room <= 0)
                return 0;

            var credited = Math.Min(amount, room);
            balances[userId] = current + credited;
            await persistAsync(balances);
            return credited;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Takes tokens back for a Channel Points redemption the streamer refunded, and is the one
    /// operation here allowed to leave a balance below zero. That is deliberate: clamping at zero
    /// would make "redeem, summon, get the points refunded" a repeatable way to summon for free.
    /// A viewer in debt simply can't spend until they have bought their way back up - and a
    /// refund they never spent just returns them to where they were.
    /// </summary>
    public async Task<int> DebitAsync(string userId, int amount)
    {
        if (amount <= 0)
            return await GetBalanceAsync(userId);

        await gate.WaitAsync();
        try
        {
            var balances = await loadAsync();
            var updated = balances.GetValueOrDefault(userId) - amount;
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
            // Every balance here was paid for with real Bits or points. Starting empty *and*
            // writing that emptiness back over the file on the next credit is how a transient
            // bad read turns into everyone's balance being gone for good - so the unreadable
            // file is moved aside intact first, leaving something to restore from by hand.
            AtomicFile.Quarantine(path);
            cached = new Dictionary<string, int>();
        }
        return cached;
    }

    private Task persistAsync(Dictionary<string, int> balances)
        => AtomicFile.WriteAllTextAsync(path, JsonSerializer.Serialize(balances));
}
