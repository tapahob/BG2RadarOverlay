using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading;
using System.Windows;

namespace WPFFrontend.CombatText;

internal readonly record struct CombatImpact(int Actor, DamageKind Kind, int Amount, Point Anchor);

internal sealed class CombatEventReader : IDisposable
{
    const int Capacity = 128, SlotSize = 40;
    readonly int pid;
    MemoryMappedViewAccessor view;
    long nextProbe, cursor;
    internal bool Connected => view != null;
    internal CombatEventReader(int pid) => this.pid = pid;

    internal void Poll(long now, Action<CombatImpact> receive)
    {
        if (view == null)
        {
            if (now < nextProbe) return;
            nextProbe = now + 2000;
            try
            {
                using var map = MemoryMappedFile.OpenExisting($@"Local\BG2Radar.Combat.1.{pid}", MemoryMappedFileRights.Read);
                view = map.CreateViewAccessor(0, 8 + SlotSize * Capacity, MemoryMappedFileAccess.Read);
                cursor = view.ReadInt64(0); // A companion may attach halfway through a fight.
            }
            catch (FileNotFoundException) { return; }
        }
        long newest = view.ReadInt64(0);
        cursor = Math.Max(cursor, newest - Capacity);
        while (cursor < newest)
        {
            long offset = 8 + cursor % Capacity * SlotSize;
            long sequence = ++cursor;
            if (view.ReadInt64(offset) != sequence) continue;
            Thread.MemoryBarrier();
            long tick = view.ReadInt64(offset + 8);
            int actor = view.ReadInt32(offset + 16), kind = view.ReadInt32(offset + 20), amount = view.ReadInt32(offset + 24);
            var anchor = new Point(view.ReadSingle(offset + 28), view.ReadSingle(offset + 32));
            Thread.MemoryBarrier();
            if (view.ReadInt64(offset) == sequence && tick >= now - 500 &&
                (uint)kind <= (uint)DamageKind.Magic && (amount > 0 || kind == (int)DamageKind.Miss) &&
                anchor.X is >= 0 and <= 1 && anchor.Y is >= 0 and <= 1)
                receive(new CombatImpact(actor, (DamageKind)kind, amount, anchor));
        }
    }

    public void Dispose() => view?.Dispose();
}
