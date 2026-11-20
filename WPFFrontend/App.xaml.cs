using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;

namespace WPFFrontend
{
    public partial class App : Application
    {
        private Mutex _instance;
        internal Process LaunchGame { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            // Atomic even when Steam and a manual radar launch happen together.
            _instance = new Mutex(false, @"Local\BG2Radar.Companion", out bool first);
            if (!first)
            {
                Shutdown();
                return;
            }
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
            _instance?.Dispose();
            base.OnExit(e);
        }
    }
}
