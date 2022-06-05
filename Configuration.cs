using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using WinApiBindings;

namespace BGOverlay
{
    public static class Configuration
    {
        public static string GameFolder { get; set; }
        public static string Locale { get; set; }
        public static bool Borderless { get; set; }
        public static IntPtr hProc { get; set; }
        public static bool HidePartyMembers { get; set; }
        public static bool ShowTraps { get; set; }
        public static int RefreshTimeMS { get; set; }
        public static bool HideNeutrals { get; set; }
        public static bool HideAllies { get; set; }
        public static IntPtr HWndPtr { get; set; }
        public static bool BigBuffIcons { get; set; }

        private static readonly string version = "2.0.3";

        public static void Init()
        {
            Borderless = true;
            HidePartyMembers = false;
            HideNeutrals = false;
            HideAllies = false;
            ShowTraps = false;
            RefreshTimeMS = 300;
            Locale = "en_US";
            GameFolder = "None";
            BigBuffIcons = true;
            loadConfig();
            detectLocale();
            detectGameFolder();            
        }

        private static void detectLocale()
        {
            if (Directory.Exists($"{GameFolder}\\lang\\{Locale}")) {
                return;
            }
            Locale = "en_US";
        }

        private static void detectGameFolder()
        {
            if (File.Exists(GameFolder + "\\Baldur.exe"))
            {
                return;
            }
            if (new DirectoryInfo(Directory.GetCurrentDirectory()).EnumerateFiles().Any(x => x.Name == "Baldur.exe")) 
            {
                GameFolder = Directory.GetCurrentDirectory().Trim('\\');
                SaveConfig();
                return;
            }
            if (new DirectoryInfo(Directory.GetCurrentDirectory()).Parent.EnumerateFiles().Any(x => x.Name == "Baldur.exe"))
            {
                GameFolder = new DirectoryInfo(Directory.GetCurrentDirectory()).Parent.FullName.Trim('\\');
                SaveConfig();
                return;
            }
            MessageBox.Show("Can't find game folder :(, please check the config file.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Environment.Exit(1);
        }

        public static void SaveConfig()
        {
            File.WriteAllLines("config.cfg", new string[] 
            { 
                $"Version={version}",
                $"GameFolder={GameFolder}", 
                $"Locale={Locale}",
                $"Borderless={Borderless}",
                $"HidePartyMembers={HidePartyMembers}",
                $"RefreshTimeMs={RefreshTimeMS}",
                $"ShowTraps={ShowTraps}",
                $"HideNeutrals={HideNeutrals}",
                $"HideAllies={HideAllies}",
                $"BigBuffIcons={BigBuffIcons}",
            });
        }

        private static void loadConfig()
        {
            if (!File.Exists("config.cfg"))
            {
                SaveConfig();
            }
            try
            {
                var config = File.ReadAllLines("config.cfg");
                var version = config[0].Split('=')[1].Trim();
                if (version != Configuration.version)
                {
                    File.Delete("config.cfg");
                    loadConfig();
                }
                GameFolder = config[1].Split('=')[1].Trim();
                Locale = config[2].Split('=')[1].Trim();
                Borderless = config[3].Split('=')[1].Trim().ToLower().Equals("true");
                HidePartyMembers = config[4].Split('=')[1].Trim().ToLower().Equals("true");
                RefreshTimeMS = int.Parse(config[5].Split('=')[1].Trim());
                ShowTraps = config[6].Split('=')[1].Trim().ToLower().Equals("true");
                HideNeutrals = config[7].Split('=')[1].Trim().ToLower().Equals("true");
                HideAllies = config[8].Split('=')[1].Trim().ToLower().Equals("true");
                BigBuffIcons = config[9].Split('=')[1].Trim().ToLower().Equals("true");
            } catch (Exception ex)
            {
                // nothing
            }            
        }

        public static void ForceBorderless()
        {
            var bounds = Screen.PrimaryScreen.Bounds;
            if (HWndPtr == null)
                return;
            WinAPIBindings.SetWindowLong32(HWndPtr, -16, (uint)WinAPIBindings.WindowStyles.WS_MAXIMIZE);
            WinAPIBindings.ShowWindow(HWndPtr.ToInt32(), 5);
            WinAPIBindings.SetWindowPos(HWndPtr, IntPtr.Zero, 0, 0, bounds.Width, bounds.Height, 0x4000);
        }
    }
}
