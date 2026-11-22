using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Threading.Tasks;
using static BGOverlay.RadarLocalization;

namespace WPFFrontend
{
    public partial class App : Application
    {
        private Mutex _instance;
        private bool restartingLaunchGame;
        internal Process LaunchGame { get; private set; }

        internal static class EntryPoint
        {
            [STAThread]
            public static void Main()
            {
                // The native launcher checks this same mutex before starting the overlay.
                using var instance = new Mutex(false, @"Local\BG2Radar.Companion", out bool first);
                if (!first) return;

                var app = new App { _instance = instance };
                app.InitializeComponent();
                app.Run();
            }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            if (e.Args.Length == 2 && e.Args[0] == "--game-pid")
            {
                if (!int.TryParse(e.Args[1], out int pid) || !watchGame(pid))
                {
                    Shutdown();
                    return;
                }
                var settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "BG2RadarOverlay");
                Directory.CreateDirectory(settings);
                Directory.SetCurrentDirectory(settings);
            }
            base.OnStartup(e);
        }

        private bool watchGame(int pid)
        {
            try
            {
                LaunchGame = Process.GetProcessById(pid);
                if (!string.Equals(LaunchGame.ProcessName, "Baldur", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(LaunchGame.ProcessName, "icewind", StringComparison.OrdinalIgnoreCase)) return false;
                LaunchGame.Exited += (_, _) => Dispatcher.BeginInvoke(new Action(() => { if (!restartingLaunchGame) Shutdown(); }));
                LaunchGame.EnableRaisingEvents = true;
                return !LaunchGame.HasExited;
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
        }

        internal async Task Restart(GameInstallation game, Process process)
        {
            if (process.HasExited) return;
            var infinityLoader = Path.Combine(game.Folder, "InfinityLoader.exe");
            var launcher = File.Exists(infinityLoader) ? infinityLoader : game.Executable;
            bool companion = process.Id == LaunchGame?.Id;
            restartingLaunchGame = companion;
            try
            {
                // The overlay has already confirmed the restart. Closing the window would prompt again.
                try { process.Kill(); }
                catch (Exception ex) when ((ex is InvalidOperationException or Win32Exception) && process.HasExited) { }
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                try { await process.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException)
                {
                    throw new IOException(Get("Str_NativeCloseTimeout"));
                }
                // Let the new game's native module claim the single companion slot.
                if (companion) _instance.Dispose();
                Process.Start(new ProcessStartInfo(launcher) { WorkingDirectory = game.Folder })?.Dispose();
                if (companion) Shutdown();
            }
            finally
            {
                restartingLaunchGame = false;
                if (companion && process.HasExited && !Dispatcher.HasShutdownStarted)
                    _ = Dispatcher.InvokeAsync(Shutdown, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            LaunchGame?.Dispose();
            base.OnExit(e);
        }
    }
}
