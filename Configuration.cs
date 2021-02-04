﻿using System;
using System.IO;
using System.Linq;

namespace BGOverlay
{
    public static class Configuration
    {
        public static string GameFolder { get; private set; }

        public static string Locale { get; private set; }

        public static IntPtr hProc { get; set; }

        public static void Init()
        {
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
            if (Directory.EnumerateFiles(Directory.GetCurrentDirectory()).Any(x => x == "Baldur.exe")) 
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

            throw new Exception("Cannot find Baldur.exe!");
            //OpenFileDialog openFileDialog = new OpenFileDialog();
            //if (openFileDialog.ShowDialog() == true)
            //    txtEditor.Text = File.ReadAllText(openFileDialog.FileName);
        }

        private static void saveConfig()
        {
            File.WriteAllLines("config.cfg", new string[] { $"GameFolder={GameFolder}", $"Locale={Locale}" });
        }

        private static void loadConfig()
        {
            var config = File.ReadAllLines("config.cfg");
            GameFolder = config[0].Split('=')[1];
            Locale     = config[1].Split('=')[1];
        }
    }
}
