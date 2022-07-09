using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using WinApiBindings;

namespace BGOverlay
{
    public static class Configuration
    {
        public static bool UseShiftClick { get; set; }
        public static bool CloseWithRightClick { get; set; }
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
        public static string Font1 { get; set; }
        public static string Font2 { get; set; }
        public static string Font3 { get; set; }
        public static string FontSize1 { get; set; }
        public static string FontSize2 { get; set; }
        public static string FontSize3Big { get; set; }
        public static string FontSize3Small { get; set; }

        private static Dictionary<String, String> storedConfig = new Dictionary<string, string>();
        public const string Version = "2.0.4.4";

        public static void Init()
        {
            Borderless          = true;
            HidePartyMembers    = false;
            HideNeutrals        = false;
            HideAllies          = false;
            ShowTraps           = false;
            RefreshTimeMS       = 300;
            Locale              = "en_US";
            GameFolder          = "None";
            BigBuffIcons        = true;
            Font1               = "Segoe Print";
            Font2               = "Ink Free";
            Font3               = "Bahnschrift Condensed";
            FontSize1           = "12";
            FontSize2           = "16";
            FontSize3Big        = "16";
            FontSize3Small      = "16";
            CloseWithRightClick = true;
            UseShiftClick       = false;
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
                $"Version={Version}",
                $"GameFolder={GameFolder}", 
                $"Locale={Locale}",
                $"Borderless={Borderless}",
                $"HidePartyMembers={HidePartyMembers}",
                $"RefreshTimeMs={RefreshTimeMS}",
                $"ShowTraps={ShowTraps}",
                $"HideNeutrals={HideNeutrals}",
                $"HideAllies={HideAllies}",
                $"BigBuffIcons={BigBuffIcons}",
                $"Font1={Font1}",
                $"Font2={Font2}",
                $"Font3={Font3}",
                $"FontSize1={FontSize1}",
                $"FontSize2={FontSize2}",
                $"FontSize3Big={FontSize3Big}",
                $"FontSize3Small={FontSize3Small}",
                $"CloseWithRightClick={CloseWithRightClick}",
                $"UseShiftClick={UseShiftClick}",
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
                foreach (var line in config)
                {
                    var split = line.Split('=');
                    storedConfig[split[0]] = split[1];
                }

                var version         = getProperty("Version", Configuration.Version); ;                
                GameFolder          = getProperty("GameFolder", "None");
                Locale              = getProperty("Locale", "en_US");
                Borderless          = getProperty("Borderless", "true").Equals("true");
                HidePartyMembers    = getProperty("HidePartyMembers", "false").Equals("true");
                RefreshTimeMS       = int.Parse(getProperty("RefreshTimeMs", "300"));
                ShowTraps           = getProperty("ShowTraps", "false").Equals("true");
                HideNeutrals        = getProperty("HideNeutrals", "false").Equals("true");
                HideAllies          = getProperty("HideAllies", "false").Equals("true");
                BigBuffIcons        = getProperty("BigBuffIcons", "false").Equals("true");
                Font1               = getProperty("Font1", "Segoe Print");
                Font2               = getProperty("Font2", "Ink Free");
                Font3               = getProperty("Font3", "Bahnschrift Condensed");
                FontSize1           = getProperty("FontSize1", "12");
                FontSize2           = getProperty("FontSize2", "16");
                FontSize3Big        = getProperty("FontSize3Big", "16");
                FontSize3Small      = getProperty("FontSize3Small", "12");
                CloseWithRightClick = getProperty("CloseWithRightClick", "true").Equals("true");
                UseShiftClick       = getProperty("UseShiftClick", "false").Equals("true");
                if (version != Configuration.Version)
                {
                    File.Delete("config.cfg");
                    loadConfig();
                }
                
            } catch (Exception ex)
            {
                // nothing
            }            
        }

        private static string getProperty(string key, string def)
        {
            if (!storedConfig.TryGetValue(key, out var value))
                return def;
            return value.Trim().ToLower();
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
