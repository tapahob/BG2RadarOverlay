using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace BGOverlay.Diagnostics
{
    /// <summary>
    /// One finished measurement of the radar's main loop, as shown in Options -> Diagnostics.
    /// Times are milliseconds; averages/percentiles are over the profiler's rolling window
    /// (<see cref="CycleProfiler.Capacity"/> cycles, about 40 seconds at the default tick).
    /// </summary>
    public struct CycleStats
    {
        public int Samples;

        /// <summary>Wall time from the start of one iteration to the start of the next - work plus sleep.</summary>
        public double CycleLast, CycleAvg, CycleP95, CycleMax;

        /// <summary>The same iteration with the sleep taken out: what the radar actually costs.</summary>
        public double WorkLast, WorkAvg, WorkP95, WorkMax;

        public double ScanAvg, PruneAvg, FilterAvg, TwitchAvg, UiAvg, SleepAvg;

        /// <summary>The scan phase split three ways: reading the slot array, refreshing pooled
        /// creatures, and building ones seen for the first time.</summary>
        public double SlotReadAvg, RefreshAvg, BuildAvg;

        public int Slots, Built, Reused, Shown;
        public double BuiltAvg;

        /// <summary>Share of each cycle spent working rather than sleeping - one core's worth is 100%.</summary>
        public double BusyPercent;

        /// <summary>What the loop actually achieves, as opposed to what the refresh-rate box asks for.</summary>
        public double EffectiveHz;

        public string Format()
        {
            var c = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.Append("cycle ").Append(CycleAvg.ToString("F1", c)).Append(" ms avg / ")
              .Append(CycleP95.ToString("F1", c)).Append(" p95 / ")
              .Append(CycleMax.ToString("F1", c)).Append(" max, ");
            sb.Append("work ").Append(WorkAvg.ToString("F1", c)).Append(" ms (")
              .Append(BusyPercent.ToString("F1", c)).Append("% busy, ")
              .Append(EffectiveHz.ToString("F2", c)).Append(" Hz), ");
            sb.Append("scan ").Append(ScanAvg.ToString("F1", c))
              .Append(" [slots ").Append(SlotReadAvg.ToString("F1", c))
              .Append(" refresh ").Append(RefreshAvg.ToString("F1", c))
              .Append(" build ").Append(BuildAvg.ToString("F1", c)).Append("]")
              .Append(" prune ").Append(PruneAvg.ToString("F1", c))
              .Append(" filter ").Append(FilterAvg.ToString("F1", c))
              .Append(" twitch ").Append(TwitchAvg.ToString("F1", c))
              .Append(" ui ").Append(UiAvg.ToString("F1", c))
              .Append(" sleep ").Append(SleepAvg.ToString("F1", c)).Append(", ");
            sb.Append(Slots.ToString(c)).Append(" slots, ")
              .Append(Built.ToString(c)).Append(" built, ")
              .Append(Reused.ToString(c)).Append(" reused, ")
              .Append(Shown.ToString(c)).Append(" shown");
            return sb.ToString();
        }
    }

    /// <summary>
    /// Times one iteration of the radar's main loop and keeps a rolling window of the results,
    /// so "is 300 ms doing anything for me?" is a question with a number behind it rather than a
    /// guess. Answers two different questions on purpose:
    ///
    ///   Cycle - wall time from one iteration starting to the next starting. This is what the
    ///           radar's update rate actually is, and it is NOT Configuration.RefreshTimeMS:
    ///           ProcessHacker.MainLoop sleeps for that long *after* doing its work, so the real
    ///           period is work + RefreshTimeMS.
    ///   Work  - the same iteration with the sleep removed. This is the part that costs the user
    ///           CPU, and the only part worth trading responsiveness against.
    ///
    /// Always on. A cycle costs about a dozen Stopwatch.GetTimestamp() calls (a QPC read each,
    /// tens of nanoseconds) against a tick that is measured in milliseconds, so there is no
    /// "enable profiling" switch to forget to turn on when a user reports the radar being slow.
    ///
    /// Threading: everything between BeginCycle() and the next BeginCycle() is written by the
    /// single thread that owns the loop (the LongRunning task started in MainWindow), including
    /// MarkUi(), which that same thread calls after MainLoop() returns. Only the finished
    /// samples are shared, and those sit behind <see cref="gate"/> for the UI thread to read.
    /// </summary>
    public sealed class CycleProfiler
    {
        public static CycleProfiler Instance { get; } = new CycleProfiler();

        /// <summary>Cycles kept in the rolling window - roughly 40 s at the default 300 ms tick.</summary>
        public const int Capacity = 128;

        /// <summary>How often the rolling summary is written to the log, in cycles (Debug Mode only).</summary>
        private const int LogEveryCycles = 200;

        private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

        private struct Sample
        {
            public double Cycle, Scan, Prune, Filter, Twitch, Ui, Sleep;
            public double SlotRead, Refresh, Build;
            public int Slots, Built, Reused, Shown;

            public double Work { get { return Scan + Prune + Filter + Twitch + Ui; } }
        }

        private readonly object gate = new object();
        private readonly Sample[] ring = new Sample[Capacity];
        private int next;
        private int count;

        // Loop-thread state. Not guarded: only the loop thread touches these, and it hands a
        // finished Sample over the gate rather than letting the UI thread read them mid-cycle.
        private Sample pending;
        private long cycleStart;
        private long phaseStart;
        private bool hasPending;
        private long cyclesSeen;

        private CycleProfiler() { }

        /// <summary>
        /// Opens a new iteration, and closes the previous one. The previous cycle is finished
        /// here rather than at the end of MainLoop because its wall time isn't known until the
        /// next one starts - and because a cycle that threw halfway through still gets committed
        /// (with whatever phases it reached) instead of silently vanishing from the window.
        /// </summary>
        public void BeginCycle()
        {
            var now = Stopwatch.GetTimestamp();

            if (hasPending)
            {
                pending.Cycle = toMs(now - cycleStart);
                commit(pending);
            }

            pending    = new Sample();
            cycleStart = now;
            phaseStart = now;
            subStart   = now;
            hasPending = true;
        }

        public void MarkScan()   { pending.Scan   = lap(); }

        // The three below accumulate inside the scan rather than marking a boundary, since the
        // scan interleaves them slot by slot. They use a stopwatch of their own so that adding a
        // slice doesn't disturb the phase boundary lap() is measuring towards.
        //
        // These are an attribution, not a partition: walking the 63k slots that hold the empty
        // sentinel costs a little, and that little lands on whichever of the three marks next.
        // It is well under a millisecond a tick, and the three still sum to roughly the scan.
        public void AddSlotRead() { pending.SlotRead += subLap(); }
        public void AddRefresh()  { pending.Refresh  += subLap(); }
        public void AddBuild()    { pending.Build    += subLap(); }
        public void MarkPrune()  { pending.Prune  = lap(); }
        public void MarkFilter() { pending.Filter = lap(); }
        public void MarkTwitch() { pending.Twitch = lap(); }

        /// <summary>
        /// Called right after MainLoop's Thread.Sleep. Measured rather than derived from
        /// (cycle - work): what the OS actually gives back for a Sleep(300) is 300 plus however
        /// long the scheduler takes to get round to this thread again, and on a machine busy
        /// running the game that difference is the interesting part.
        /// </summary>
        public void MarkSleep() { pending.Sleep = lap(); }

        /// <summary>
        /// Called from MainWindow's loop body after the enemy controls and the list have been
        /// updated - the same thread, immediately after MainLoop() returns, so it closes the
        /// iteration rather than opening a new measurement.
        /// </summary>
        public void MarkUi() { pending.Ui = lap(); }

        /// <summary>
        /// The tick's workload, which is what the timings above should be read against: a scan
        /// that got slower because an area holds 80 creatures instead of 12 isn't a regression.
        /// </summary>
        public void NoteEntities(int slots, int built, int reused, int shown)
        {
            pending.Slots  = slots;
            pending.Built  = built;
            pending.Reused = reused;
            pending.Shown  = shown;
        }

        /// <summary>
        /// Milliseconds since this iteration began. MainLoop subtracts this from RefreshTimeMS
        /// so the setting behaves as a period rather than as a delay added on top of the work -
        /// the profiler already holds the iteration's start timestamp, so nothing else needs to
        /// keep a second one in step with it.
        /// </summary>
        public double ElapsedThisCycleMs()
        {
            return hasPending ? toMs(Stopwatch.GetTimestamp() - cycleStart) : 0.0;
        }

        /// <summary>Drops the rolling window, e.g. before measuring a specific fight.</summary>
        public void Reset()
        {
            lock (gate)
            {
                next  = 0;
                count = 0;
            }
        }

        public CycleStats Snapshot()
        {
            Sample[] copy;
            int n, writeCursor;
            lock (gate)
            {
                n           = count;
                writeCursor = next;
                copy        = new Sample[n];
                Array.Copy(ring, copy, n);
            }

            var stats = new CycleStats();
            stats.Samples = n;
            if (n == 0)
                return stats;

            var cycles = new double[n];
            var works  = new double[n];
            double scan = 0, prune = 0, filter = 0, twitch = 0, ui = 0, sleep = 0, built = 0;
            double slotRead = 0, refresh = 0, build = 0;

            for (int i = 0; i < n; i++)
            {
                cycles[i] = copy[i].Cycle;
                works[i]  = copy[i].Work;
                scan     += copy[i].Scan;
                prune    += copy[i].Prune;
                filter   += copy[i].Filter;
                twitch   += copy[i].Twitch;
                ui       += copy[i].Ui;
                sleep    += copy[i].Sleep;
                built    += copy[i].Built;
                slotRead += copy[i].SlotRead;
                refresh  += copy[i].Refresh;
                build    += copy[i].Build;
            }

            // The ring's newest entry sits just behind the write cursor, which is not copy[n-1]
            // once the window has wrapped at least once.
            var newest = copy[(writeCursor + n - 1) % n];

            stats.CycleLast = newest.Cycle;
            stats.WorkLast  = newest.Work;
            stats.Slots     = newest.Slots;
            stats.Built     = newest.Built;
            stats.Reused    = newest.Reused;
            stats.Shown     = newest.Shown;

            stats.CycleAvg = average(cycles, n);
            stats.WorkAvg  = average(works, n);

            Array.Sort(cycles);
            Array.Sort(works);
            stats.CycleP95 = percentile(cycles, n, 0.95);
            stats.CycleMax = cycles[n - 1];
            stats.WorkP95  = percentile(works, n, 0.95);
            stats.WorkMax  = works[n - 1];

            stats.ScanAvg   = scan / n;
            stats.PruneAvg  = prune / n;
            stats.FilterAvg = filter / n;
            stats.TwitchAvg = twitch / n;
            stats.UiAvg     = ui / n;
            stats.SleepAvg  = sleep / n;
            stats.SlotReadAvg = slotRead / n;
            stats.RefreshAvg  = refresh / n;
            stats.BuildAvg    = build / n;
            stats.BuiltAvg  = built / n;

            stats.BusyPercent = stats.CycleAvg > 0 ? stats.WorkAvg / stats.CycleAvg * 100.0 : 0;
            stats.EffectiveHz = stats.CycleAvg > 0 ? 1000.0 / stats.CycleAvg : 0;

            return stats;
        }

        private void commit(Sample sample)
        {
            lock (gate)
            {
                ring[next] = sample;
                next       = (next + 1) % Capacity;
                if (count < Capacity)
                    count++;
            }

            cyclesSeen++;
            if (Configuration.DebugMode && cyclesSeen % LogEveryCycles == 0)
            {
                Logger.Debug("Main loop: " + Snapshot().Format());
            }
        }

        // Sub-phase stopwatch, independent of lap() so that accumulating a slice inside the
        // scan doesn't disturb the phase boundary lap() is still measuring towards.
        private long subStart;

        private double subLap()
        {
            var now = Stopwatch.GetTimestamp();
            var ms  = subStart == 0 ? 0.0 : toMs(now - subStart);
            subStart = now;
            return ms;
        }

        private double lap()
        {
            var now = Stopwatch.GetTimestamp();
            var ms  = toMs(now - phaseStart);
            phaseStart = now;
            return ms;
        }

        private static double toMs(long ticks)
        {
            return ticks * TicksToMs;
        }

        private static double average(double[] values, int n)
        {
            double sum = 0;
            for (int i = 0; i < n; i++)
                sum += values[i];
            return sum / n;
        }

        /// <summary>Nearest-rank percentile over an already-sorted array.</summary>
        private static double percentile(double[] sorted, int n, double p)
        {
            var index = (int)Math.Ceiling(p * n) - 1;
            if (index < 0) index = 0;
            if (index >= n) index = n - 1;
            return sorted[index];
        }
    }
}
