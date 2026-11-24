using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BGOverlay;
using WinApiBindings;

namespace WPFFrontend;

// The game evaluates one actor on its UI thread. Pick the least recently read
// open card so multiple cards keep updating even while the game is paused.
internal sealed class FeatCardReader
{
    private readonly GameMailbox channel = new(0x52444654, 0x61F0D972, 1);
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly Dictionary<int, (long Time, string Payload, List<SpellIconEntry> Rows)> cards = new();
    private Task<IntPtr> search;
    private IntPtr box;
    private long nextPoll, nextSearch;
    private uint session;
    private int? pending;

    internal static FeatCardReader ForGame(string gameFolder)
    {
        return File.Exists(Path.Combine(gameFolder, "override", "MOFBOOT.lua")) ? new FeatCardReader() : null;
    }

    internal List<SpellIconEntry> Read(BGEntity actor, IEnumerable<int> openCards)
    {
        long now = clock.ElapsedMilliseconds;
        if (now >= nextPoll)
        {
            nextPoll = now + 50;
            if (Locate(now)) Poll(now, openCards.Append(actor.Id).Distinct().ToArray());
        }
        return cards.GetValueOrDefault(actor.Id).Rows;
    }

    private bool Locate(long now)
    {
        if (box != IntPtr.Zero && channel.Matches(box)) return true;
        cards.Clear();
        pending = null;
        box = IntPtr.Zero;
        if (search?.IsCompleted == true)
        {
            box = search.GetAwaiter().GetResult();
            search = null;
            if (box != IntPtr.Zero && channel.Matches(box)) return true;
        }
        if (search == null && now >= nextSearch)
        {
            nextSearch = now + 30000;
            search = Task.Run(channel.Find);
        }
        return false;
    }

    private void Poll(long now, int[] ids)
    {
        uint currentSession = WinAPIBindings.ReadUInt32(box + 28);
        if (currentSession != session)
        {
            cards.Clear();
            pending = null;
            session = currentSession;
        }
        foreach (int closed in cards.Keys.Except(ids).ToArray()) cards.Remove(closed);

        uint state = WinAPIBindings.ReadUInt32(box + 16);
        if (state == 1) return;
        if (state == 2)
        {
            int id = WinAPIBindings.ReadInt32(box + 20);
            int length = WinAPIBindings.ReadInt32(box + 24);
            if (id == pending && ids.Contains(id) && length >= 0 && length < 1024 * 1024)
            {
                var payload = Encoding.UTF8.GetString(WinAPIBindings.ReadBytes(box + 32, length));
                var rows = cards.TryGetValue(id, out var old) && old.Payload == payload ? old.Rows : Parse(payload);
                cards[id] = (now, payload, rows);
            }
            pending = null;
            if (!WinAPIBindings.WriteBytes(box + 16, BitConverter.GetBytes(0))) return;
        }
        else if (state != 0) return;

        int next = ids.OrderBy(id => cards.TryGetValue(id, out var cached) ? cached.Time : long.MinValue).First();
        if (cards.TryGetValue(next, out var last) && now - last.Time < 1000) return;
        if (WinAPIBindings.WriteBytes(box + 20, BitConverter.GetBytes(next)) &&
            WinAPIBindings.WriteBytes(box + 24, BitConverter.GetBytes(-1)) &&
            WinAPIBindings.WriteBytes(box + 16, BitConverter.GetBytes(1)))
            pending = next;
    }

    private static List<SpellIconEntry> Parse(string payload)
    {
        var feats = new List<SpellIconEntry>();
        foreach (var line in payload.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split('\t');
            if (fields.Length is not (3 or 4) || !int.TryParse(fields[1], out int cycle) || fields[2].Length == 0) continue;
            var bam = fields[0] is "MOFGLYPH" or "MOFEPICO" ? ResourceManager.Instance.GetBAMReader(fields[0]) : null;
            feats.Add(new SpellIconEntry { Name = fields[2], Icon = bam?.FeatIcons.GetValueOrDefault(cycle), IsFeat = true,
                Description = fields.Length == 4 ? fields[3].Replace('\r', '\n').Trim() : null });
        }
        feats.Sort((left, right) => string.Compare(left.Name, right.Name, StringComparison.CurrentCulture));
        return feats;
    }
}
