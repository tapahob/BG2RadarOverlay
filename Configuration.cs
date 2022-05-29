using System;
using System.IO;
using System.Linq;

namespace BGOverlay
{
    public static class Configuration
    {
        public static string GameFolder { get; private set; }

        public static string Locale { get; private set; }
        public static bool Borderless { get; private set; }
        public static int RadarRadius { get; private set; }
        public static IntPtr hProc { get; set; }

        public static void Init()
        {
            Borderless = true;
            RadarRadius = 300;
            loadConfig();
            detectGameFolder();
            detectLocale();            
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
                saveConfig();
                return;
            }
            if (new DirectoryInfo(Directory.GetCurrentDirectory()).Parent.EnumerateFiles().Any(x => x.Name == "Baldur.exe"))
            {
                GameFolder = new DirectoryInfo(Directory.GetCurrentDirectory()).Parent.FullName.Trim('\\');
                saveConfig();
                return;
            }

            Environment.Exit(1);
        }

        private static void saveConfig()
        {
            File.WriteAllLines("config.cfg", new string[] 
            { 
                $"GameFolder={GameFolder}", 
                $"Locale={Locale}",
                $"Borderless={Borderless}",
                $"RadarRadius={RadarRadius}",
            });
        }

        private static void loadConfig()
        {
            if (!File.Exists("config.cfg"))
            {
                File.WriteAllLines("config.cfg", new string[] { $"GameFolder=none", $"Locale=en_US" });
            }
            try
            {
                var config = File.ReadAllLines("config.cfg");
                GameFolder = config[0].Split('=')[1];
                Locale = config[1].Split('=')[1];
                Borderless = config[2].Split('=')[1].Trim().ToLower().Equals("true");
                RadarRadius = int.Parse(config[3].Split('=')[1].Trim());
            } catch (Exception ex)
            {
                // nothing
            }            
        }
    }
}
