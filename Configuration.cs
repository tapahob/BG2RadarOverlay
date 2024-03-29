﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;
using WinApiBindings;

namespace BGOverlay
{
    public static class Configuration
    {
        public static bool UseShiftClick { get; set; }
        public static int EnemyListXOffset { get; private set; }
        public static bool DebugMode { get; private set; }
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
        public static string Version => System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString();

        public static void Init(Process bgProcess)
        {            
            GameFolder          = bgProcess.MainModule.FileName.ToLower().Replace("\\baldur.exe", "");
            Borderless          = true;
            HidePartyMembers    = false;
            HideNeutrals        = false;
            HideAllies          = false;
            ShowTraps           = false;
            RefreshTimeMS       = 300;
            Locale              = "en_US";            
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
            EnemyListXOffset    = 0;
            DebugMode           = false;
            loadConfig();
            detectLocale();
        }

        private static void detectLocale()
        {
            if (Directory.Exists($"{GameFolder}\\lang\\{Locale}")) {
                return;
            }
            Locale = "en_US";
        }

        public static void SaveConfig()
        {
            File.WriteAllLines("config.cfg", new string[] 
            { 
                $"Version={Version}",
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
                $"EnemyListXOffset={EnemyListXOffset}",
                $"DebugMode={DebugMode}",
            });
        }

        private static void loadConfig()
        {
            if (!File.Exists("config.cfg"))
            {
                Logger.Debug("No config file exits, making a new one");
                SaveConfig();
            }
            try
            {
                Logger.Debug("Reading existing config ...");
                var config = File.ReadAllLines("config.cfg");
                foreach (var line in config)
                {
                    var split = line.Split('=');
                    storedConfig[split[0]] = split[1];
                }

                var version         = getProperty("Version", Configuration.Version); ;                
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
                EnemyListXOffset    = int.Parse(getProperty("EnemyListXOffset", "0"));
                DebugMode           = getProperty("DebugMode", "false").Equals("true");
                if (version != Configuration.Version)
                {
                    Logger.Debug("Outdated config version found - overriding it");
                    File.Delete("config.cfg");
                    loadConfig();
                    Logger.Debug("Done");
                }
                Logger.Info("Current config:\n" + string.Join("\n", File.ReadAllLines("config.cfg")));
                
            } catch (Exception ex)
            {
                Logger.Fatal("Load config error!", ex);
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
            Logger.Debug("Making the window borderless ...");
            try
            {
                var proc    = Process.GetProcessesByName("Baldur")[0];
                var bounds  = Screen.PrimaryScreen.Bounds;
                var hwnd    = proc.MainWindowHandle;
                
                WinAPIBindings.SetWindowLong32(hwnd, -16, (uint)WinAPIBindings.WindowStyles.WS_MAXIMIZE);
                WinAPIBindings.ShowWindow(hwnd.ToInt32(), 5);
                WinAPIBindings.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, bounds.Width, bounds.Height, 0x4000);
            } catch (Exception ex)
            {
                Logger.Error("Could not make a window borderless!", ex);
            }            
        }
    }
}
