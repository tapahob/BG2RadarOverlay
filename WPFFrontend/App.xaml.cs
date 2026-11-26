using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
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
            public static void Main(string[] args)
            {
#if RADAR_AUTOUPDATE
                if (ReleaseUpdater.LaunchPreparedVersion(args)) return;
#endif
                var app = new App();
                if (args.Length == 2 && args[0] == "--game-pid"
                    && (!int.TryParse(args[1], out int pid) || !app.watchGame(pid))) return;

                if (app.LaunchGame != null)
                {
                    var settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "BG2RadarOverlay");
                    Directory.CreateDirectory(settings);
                    Directory.SetCurrentDirectory(settings);
                }

                using var owners = MemoryMappedFile.CreateOrOpen(RadarInstance.Name + ".Owner", 16);
                using var stop = new EventWaitHandle(false, EventResetMode.ManualReset,
                    RadarInstance.StopEvent(Environment.ProcessId));
                try
                {
                    app._instance = RadarInstance.TakeOver(owners);
                }
                catch (Exception ex) when (ex is IOException or Win32Exception or UnauthorizedAccessException)
                {
                    MessageBox.Show(ex.Message, "BG Radar Overlay", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                using var instance = app._instance;
                if (instance == null) return;
                var dispatcher = app.Dispatcher;
                var registration = ThreadPool.RegisterWaitForSingleObject(stop, (_, _) =>
                    dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Send, new Action(app.Shutdown)),
                    null, Timeout.Infinite, true);
                try
                {
                    app.InitializeComponent();
#if RADAR_AUTOUPDATE
                    using var updates = new CancellationTokenSource();
                    BGOverlay.Logger.Init();
                    _ = Task.Run(() => ReleaseUpdater.CheckPeriodically(updates.Token));
                    app.Exit += (_, _) => updates.Cancel();
#endif
                    app.Run();
                }
                finally { registration.Unregister(null); }
            }
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
            BGOverlay.Configuration.UpdateTaskbarFullscreen(exiting: true);
            LaunchGame?.Dispose();
            base.OnExit(e);
        }
    }
}
