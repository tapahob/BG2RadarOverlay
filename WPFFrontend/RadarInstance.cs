using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using BGOverlay;
using System.Threading;

namespace WPFFrontend;

internal static class RadarInstance
{
    internal const string Name = @"Local\BG2Radar.Companion";
    internal static string StopEvent(int pid) => $"{Name}.Stop.{pid}";

    // Serialize just the handover. The owner record survives while the radar runs.
    internal static Mutex TakeOver(MemoryMappedFile owners)
    {
        using var gate = new Mutex(false, Name + ".Startup");
        try { gate.WaitOne(); }
        catch (AbandonedMutexException) { } // Windows granted us the abandoned mutex.
        try
        {
            using var owner = owners.CreateViewAccessor();
            using var current = Process.GetCurrentProcess();
            long started = current.StartTime.ToFileTimeUtc();
            int pid = owner.ReadInt32(0);
            if (pid != 0)
            {
                try
                {
                    using var previous = Process.GetProcessById(pid);
                    long previousStart = previous.StartTime.ToFileTimeUtc();
                    if (previousStart == owner.ReadInt64(8) && previous.SessionId == current.SessionId)
                    {
                        // A slower starter can reach the gate after a newer launch.
                        if (previousStart > started || (previousStart == started && pid > current.Id))
                            return null;
                        Stop(previous);
                    }
                }
                catch (ArgumentException) { } // Exited before its PID could be opened.
                catch (InvalidOperationException) { } // Exited during inspection.
            }
            else if (Mutex.TryOpenExisting(Name, out var legacy))
            {
                legacy.Dispose();
                StopLegacyRadar(current);
                if (Mutex.TryOpenExisting(Name, out var stillRunning))
                {
                    stillRunning.Dispose();
                    throw new IOException(StartupError("Str_PreviousRadarRunning"));
                }
            }
            // Older native launchers still look for this marker.
            var marker = new Mutex(false, Name);
            owner.Write(8, started);
            owner.Write(0, current.Id);
            return marker;
        }
        finally { gate.ReleaseMutex(); }
    }

    // A handover error can precede normal UI and localization startup.
    private static string StartupError(string key)
    {
        Logger.Init();
        RadarLocalization.Init(Configuration.PeekPersistedLocale());
        return RadarLocalization.Get(key);
    }

    private static void StopLegacyRadar(Process current)
    {
        // Older releases announced their presence but did not publish a PID.
        foreach (var candidate in Process.GetProcesses())
        {
            using (candidate)
            {
                try
                {
                    if (candidate.Id == current.Id || (candidate.ProcessName != "BG Radar Overlay"
                        && candidate.MainWindowTitle != "BG Overlay") || candidate.SessionId != current.SessionId
                        || candidate.StartTime > current.StartTime) continue;
                    var version = candidate.MainModule?.FileVersionInfo;
                    if (version?.OriginalFilename == "BG Radar Overlay.dll" && version.ProductName == "BG Radar Overlay")
                        Stop(candidate);
                }
                catch (InvalidOperationException) { } // Already gone.
            }
        }
    }

    private static void Stop(Process previous)
    {
        if (EventWaitHandle.TryOpenExisting(StopEvent(previous.Id), out var stop))
        {
            using (stop) stop.Set();
        }
        else previous.CloseMainWindow();
        if (previous.WaitForExit(3000)) return;
        try { previous.Kill(); }
        catch (Win32Exception) when (previous.HasExited) { return; }
        if (!previous.WaitForExit(5000))
            throw new IOException(string.Format(StartupError("Str_RadarCloseTimeout"), previous.Id));
    }
}
