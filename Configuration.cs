using System;
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
        public static int EnemyListXOffset { get; set; }
        public static int RadarIconXOffset { get; set; }
        public static bool DebugMode { get; set; }
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
        public static bool TwitchIntegrationEnabled { get; set; }
        public static string TwitchRelayUrl { get; set; }
        // getProperty() below lowercases every persisted value on load, so these can't hold a
        // mixed-case secret - OptionsControl's "Generate" button produces them via
        // Guid.ToString("N") (lowercase hex already) specifically so a config reload can't
        // corrupt them.
        //
        // Two separate keys on purpose: the stream key is pasted into the Twitch Extension's
        // broadcaster config, which Twitch serves to every viewer's browser, so it's
        // effectively public and may only grant read access. The control key authorizes
        // commands coming back down (summons) and must never leave the overlay and relay.
        public static string TwitchStreamKey { get; set; }
        public static string TwitchControlKey { get; set; }

        /// <summary>
        /// Level-banded creature packs a viewer summon draws from - see <see cref="SpawnPack"/>
        /// for the encoding. Stored as one flat string because config.cfg is key=value only.
        /// </summary>
        public static string SpawnPacks { get; set; }

        private static Dictionary<String, String> storedConfig = new Dictionary<string, string>();
        public static string Version => System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString();

        public static void Init(Process bgProcess)
        {            
            GameFolder          = Path.GetDirectoryName(bgProcess.MainModule.FileName);
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
            UseShiftClick       = false;
            EnemyListXOffset    = 0;
            RadarIconXOffset    = 0;
            DebugMode           = false;
            TwitchIntegrationEnabled = false;
            TwitchRelayUrl      = "ws://localhost:5080";
            TwitchStreamKey     = "";
            TwitchControlKey    = "";
            SpawnPacks          = "";
            loadConfig();

            // Every install needs a control key, even on configs written before this existed or
            // where the user never pressed Generate - without one the relay has no way to
            // address commands at this overlay, and it would fail silently.
            if (string.IsNullOrEmpty(TwitchControlKey))
            {
                TwitchControlKey = Guid.NewGuid().ToString("N");
                SaveConfig();
            }

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
                $"UseShiftClick={UseShiftClick}",
                $"EnemyListXOffset={EnemyListXOffset}",
                $"RadarIconXOffset={RadarIconXOffset}",
                $"DebugMode={DebugMode}",
                $"TwitchIntegrationEnabled={TwitchIntegrationEnabled}",
                $"TwitchRelayUrl={TwitchRelayUrl}",
                $"TwitchStreamKey={TwitchStreamKey}",
                $"TwitchControlKey={TwitchControlKey}",
                $"SpawnPacks={SpawnPacks}",
            });
        }

        /// <summary>
        /// Reads just the persisted Locale value out of config.cfg, without requiring the game
        /// process Init() otherwise needs (for GameFolder etc.) - for MainWindow's
        /// waiting-for-game indicator, which needs a locale before the process (and therefore
        /// Init()) is available yet. Returns null if config.cfg doesn't exist yet or has no
        /// Locale entry, same as a fresh/first-ever run.
        /// </summary>
        public static string PeekPersistedLocale()
        {
            if (!File.Exists("config.cfg"))
                return null;
            try
            {
                foreach (var line in File.ReadAllLines("config.cfg"))
                {
                    var split = line.Split('=');
                    if (split.Length >= 2 && split[0].Trim().Equals("Locale", StringComparison.OrdinalIgnoreCase))
                        return split[1].Trim().ToLower();
                }
            }
            catch (Exception ex)
            {
                Logger.Error("PeekPersistedLocale error!", ex);
            }
            return null;
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
                UseShiftClick       = getProperty("UseShiftClick", "false").Equals("true");
                EnemyListXOffset    = int.Parse(getProperty("EnemyListXOffset", "0"));
                RadarIconXOffset    = int.Parse(getProperty("RadarIconXOffset", "0"));
                DebugMode           = getProperty("DebugMode", "false").Equals("true");
                TwitchIntegrationEnabled = getProperty("TwitchIntegrationEnabled", "false").Equals("true");
                TwitchRelayUrl      = getProperty("TwitchRelayUrl", "ws://localhost:5080");
                TwitchStreamKey     = getProperty("TwitchStreamKey", "");
                TwitchControlKey    = getProperty("TwitchControlKey", "");
                SpawnPacks          = getProperty("SpawnPacks", "");
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
                var proc    = Process.GetProcessesByName(ProcessHacker.gameName)[0];
                var hwnd    = proc.MainWindowHandle;
                // Use whichever monitor the game window is currently on instead of always the
                // primary one, so making it borderless doesn't drag it to a different screen.
                var bounds  = Screen.FromHandle(hwnd).Bounds;

                WinAPIBindings.SetWindowLong32(hwnd, -16, (uint)WinAPIBindings.WindowStyles.WS_MAXIMIZE);
                WinAPIBindings.ShowWindow(hwnd.ToInt32(), 5);
                WinAPIBindings.SetWindowPos(hwnd, IntPtr.Zero, bounds.Left, bounds.Top, bounds.Width, bounds.Height, 0x4000);
            } catch (Exception ex)
            {
                Logger.Error("Could not make a window borderless!", ex);
            }            
        }
    }
}
