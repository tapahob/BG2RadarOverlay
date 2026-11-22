using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;

namespace WPFFrontend
{
    public partial class App : Application
    {
        internal Process LaunchGame { get; private set; }

        internal static class EntryPoint
        {
            [STAThread]
            public static void Main()
            {
                // The native launcher checks this same mutex before starting the overlay.
                using var instance = new Mutex(false, @"Local\BG2Radar.Companion", out bool first);
                if (!first) return;

                var app = new App();
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
                if (!string.Equals(LaunchGame.ProcessName, "Baldur", StringComparison.OrdinalIgnoreCase)) return false;
                LaunchGame.Exited += (_, _) => Dispatcher.BeginInvoke(new Action(Shutdown));
                LaunchGame.EnableRaisingEvents = true;
                return !LaunchGame.HasExited;
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            LaunchGame?.Dispose();
            base.OnExit(e);
        }
    }
}
