using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Threading;
using WindowStyles = WinApiBindings.WinAPIBindings.WindowStyles;
using WinApiBindings;

namespace BGOverlay
{
    public enum CombatTrajectory { Up, ArcRight, Parabola, ArcLeft, Diagonal, Wave, Fan }
    public enum CombatEasing { CircOut, QuadOut, CubicOut, SineOut, CubicInOut, QuadIn, QuartOut, ExpoOut, BackOut, SpringOut, SmoothStep, Linear }

    public static class Configuration
    {
        public static bool UseShiftClick { get; set; }
        public static int EnemyListXOffset { get; set; }
        public static int RadarIconXOffset { get; set; }
        public static bool DebugMode { get; set; }
        public static string GameExecutable { get; private set; }
        public static string GameFolder { get; set; }
        public static string Locale { get; set; }
        public static bool Borderless { get; set; }
        public static IntPtr hProc { get; set; }
        public static bool HidePartyMembers { get; set; }
        public static bool ShowTraps { get; set; }
        /// <summary>
        /// Floor for <see cref="RefreshTimeMS"/>. Zero turns ProcessHacker.MainLoop into a spin
        /// loop that burns a core doing nothing useful - well past the point where a faster tick
        /// buys anything, since a single iteration already costs more than this.
        /// </summary>
        public const int MinRefreshTimeMS = 50;

        /// <summary>
        /// Ceiling for <see cref="RefreshTimeMS"/>. Five seconds of staleness is already far
        /// beyond usable; anything higher just looks like the radar has frozen.
        /// </summary>
        public const int MaxRefreshTimeMS = 5000;

        private static int refreshTimeMS = 100;

        /// <summary>
        /// How long MainLoop waits between iterations. Both of the places this is assigned from
        /// take it from free text - the Options box, and a config.cfg the user may have edited by
        /// hand - so the clamp lives on the setter rather than at each call site, and neither can
        /// hand the loop a value that spins the CPU or stalls the display.
        /// </summary>
        public static int RefreshTimeMS
        {
            get { return refreshTimeMS; }
            set
            {
                if (value < MinRefreshTimeMS)
                    refreshTimeMS = MinRefreshTimeMS;
                else if (value > MaxRefreshTimeMS)
                    refreshTimeMS = MaxRefreshTimeMS;
                else
                    refreshTimeMS = value;
            }
        }

        public static bool HideNeutrals { get; set; }
        public static bool HideAllies { get; set; }
        public static IntPtr HWndPtr { get; set; }
        public static bool BigBuffIcons { get; set; }
        public static bool CenterBuffIcons { get; set; }
        public const int MinRadarScale = 50, MaxRadarScale = 200;
        public static int RadarScalePercent { get; set; } = 100;
        private static bool buffInkFilter;
        public static event EventHandler BuffInkFilterChanged;

        public static bool BuffInkFilter
        {
            get => buffInkFilter;
            set
            {
                if (buffInkFilter == value)
                    return;

                buffInkFilter = value;
                BuffInkFilterChanged?.Invoke(null, EventArgs.Empty);
            }
        }
        public static bool AnimateEnemyListNumbers { get; set; } = true;
        public static bool AnimateCharacterCardNumbers { get; set; } = true;
        public static bool ShowCombatText { get; set; } = true;
        public static bool ShowCastingSpell { get; set; } = true;
        public static int CombatTextScale { get; set; } = 40;
        public static int CombatTextSpeed { get; set; } = 100;
        public static int CombatTextPush { get; set; } = 50;
        public static CombatTrajectory CombatTextPath { get; set; } = CombatTrajectory.Up;
        public static CombatEasing CombatTextEasing { get; set; } = CombatEasing.CircOut;
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
        /// Her Twitch channel login (e.g. "yuna_maxwell") - sent to the relay in the same
        /// handshake as the control key, so a relay serving several streamers can tell whose
        /// stream key belongs to whom. Lowercase already (Twitch logins are), so
        /// getProperty()'s lowercasing on reload is harmless here, unlike the two keys above.
        /// </summary>
        public static string TwitchBroadcasterLogin { get; set; }

        /// <summary>
        /// Level-banded creature packs a viewer summon draws from - see <see cref="SpawnPack"/>
        /// for the encoding. Stored as one flat string because config.cfg is key=value only.
        /// </summary>
        public static string SpawnPacks { get; set; }

        private static Dictionary<String, String> storedConfig = new Dictionary<string, string>();
        public static string Version => System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString();

        private static int gameProcessId;

        public static void Init(Process bgProcess)
        {            
            gameProcessId       = bgProcess.Id;
            GameExecutable      = bgProcess.MainModule.FileName;
            GameFolder          = Path.GetDirectoryName(GameExecutable);
            Borderless          = true;
            HidePartyMembers    = false;
            HideNeutrals        = false;
            HideAllies          = false;
            ShowTraps           = false;
            RefreshTimeMS       = 100;
            Locale              = "en_US";            
            BigBuffIcons        = true;
            CenterBuffIcons     = true;
            RadarScalePercent   = 100;
            BuffInkFilter       = false;
            AnimateEnemyListNumbers = true;
            AnimateCharacterCardNumbers = true;
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
            TwitchBroadcasterLogin = "";
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
                $"{nameof(CenterBuffIcons)}={CenterBuffIcons}",
                $"{nameof(RadarScalePercent)}={RadarScalePercent}",
                $"{nameof(BuffInkFilter)}={BuffInkFilter}",
                $"AnimateEnemyListNumbers={AnimateEnemyListNumbers}",
                $"AnimateCharacterCardNumbers={AnimateCharacterCardNumbers}",
                $"ShowCombatText={ShowCombatText}",
                $"ShowCastingSpell={ShowCastingSpell}",
                $"CombatTextScale={CombatTextScale}",
                $"CombatTextSpeed={CombatTextSpeed}",
                $"CombatTextPush={CombatTextPush}",
                $"CombatTextPath={CombatTextPath}",
                $"CombatTextEasing={CombatTextEasing}",
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
                $"TwitchBroadcasterLogin={TwitchBroadcasterLogin}",
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
                storedConfig.Clear();
                foreach (var line in config)
                {
                    var split = line.Split('=');
                    storedConfig[split[0]] = split[1];
                }

                Locale              = getProperty("Locale", "en_US");
                Borderless          = getProperty("Borderless", "true").Equals("true");
                HidePartyMembers    = getProperty("HidePartyMembers", "false").Equals("true");
                // TryParse, not Parse: config.cfg is a plain text file people edit, and a
                // typo here used to take the whole app down before the window ever appeared.
                int refreshMs;
                RefreshTimeMS = int.TryParse(getProperty("RefreshTimeMs", "100"), out refreshMs)
                    ? refreshMs
                    : 100;
                ShowTraps           = getProperty("ShowTraps", "false").Equals("true");
                HideNeutrals        = getProperty("HideNeutrals", "false").Equals("true");
                HideAllies          = getProperty("HideAllies", "false").Equals("true");
                BigBuffIcons        = getProperty("BigBuffIcons", "false").Equals("true");
                CenterBuffIcons     = getProperty(nameof(CenterBuffIcons), "true").Equals("true");
                RadarScalePercent   = ReadPercent(nameof(RadarScalePercent), 100, MinRadarScale, MaxRadarScale);
                BuffInkFilter       = getProperty(nameof(BuffInkFilter), "false").Equals("true");
                AnimateEnemyListNumbers = getProperty(nameof(AnimateEnemyListNumbers), "true").Equals("true");
                AnimateCharacterCardNumbers = getProperty(nameof(AnimateCharacterCardNumbers), "true").Equals("true");
                ShowCombatText = getProperty(nameof(ShowCombatText), "true").Equals("true");
                ShowCastingSpell = getProperty(nameof(ShowCastingSpell), "true").Equals("true");
                CombatTextScale = ReadPercent(nameof(CombatTextScale), 40, 1, 150);
                CombatTextSpeed = ReadPercent(nameof(CombatTextSpeed), 100, 1, 200);
                CombatTextPush = ReadPercent(nameof(CombatTextPush), 50, 0, 300);
                CombatTextPath = ReadChoice(nameof(CombatTextPath), CombatTrajectory.Up);
                CombatTextEasing = ReadChoice(nameof(CombatTextEasing), CombatEasing.CircOut);
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
                TwitchBroadcasterLogin = getProperty("TwitchBroadcasterLogin", "");
                SpawnPacks          = getProperty("SpawnPacks", "");
                Logger.Info("Current config:\n" + string.Join("\n", File.ReadAllLines("config.cfg")));
                
            } catch (Exception ex)
            {
                Logger.Fatal("Load config error!", ex);
            }            
        }

        private static int ReadPercent(string key, int fallback, int min, int max) =>
            int.TryParse(getProperty(key, ""), out int value) ? Math.Max(min, Math.Min(max, value)) : fallback;

        private static T ReadChoice<T>(string key, T fallback) where T : struct =>
            storedConfig.TryGetValue(key, out var saved) && Enum.TryParse(saved, true, out T value)
                && Enum.IsDefined(typeof(T), value) ? value : fallback;

        private static string getProperty(string key, string def)
        {
            if (!storedConfig.TryGetValue(key, out var value))
                return def;
            return value.Trim().ToLower();
        }

        private static WindowStyles ReadWindowStyle(IntPtr hwnd) =>
            (WindowStyles)unchecked((uint)WinAPIBindings.GetWindowLongPtr(hwnd, -16).ToInt64());

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowRect { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out WindowRect rect);

        private static void PostGameKey(IntPtr hwnd, uint message, int key, uint keyData)
        {
            if (!WinAPIBindings.PostMessage(hwnd, message, new IntPtr(key), new IntPtr((long)keyData)))
                throw new Win32Exception();
        }

        // Both events are reset for an exclusive D3D9 device. SDL's window style
        // alone cannot distinguish it from a borderless window on this backend.
        private static (bool Exclusive, long Present)? ReadPresentation(int pid, IntPtr hwnd)
        {
            string name = $@"Local\BG2Radar.Frame.1.{pid}";
            if (!EventWaitHandle.TryOpenExisting(name + ".FullscreenLayer", out var fullscreen)) return null;
            using (fullscreen)
            {
                if (!EventWaitHandle.TryOpenExisting(name + ".WindowLayer", out var windowed)) return null;
                using (windowed)
                using (var map = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read))
                using (var frame = map.CreateViewAccessor(0, 56, MemoryMappedFileAccess.Read))
                {
                    long present = frame.ReadInt64(0);
                    if (present == 0 || frame.ReadInt64(48) != hwnd.ToInt64()) return null;
                    return (!fullscreen.WaitOne(0) && !windowed.WaitOne(0), present);
                }
            }
        }

        public static void ForceBorderless() => ForceBorderless(HWndPtr);

        internal static void ForceBorderless(IntPtr hwnd) => ApplyBorderless(gameProcessId, hwnd, null);

        public static void RecoverBorderless(int pid, IntPtr hwnd, long stalledPresent) =>
            ApplyBorderless(pid, hwnd, stalledPresent);

        private static readonly object borderlessChange = new object();

        private static void ApplyBorderless(int pid, IntPtr hwnd, long? stalledPresent)
        {
            if (!Monitor.TryEnter(borderlessChange)) return;
            try
            {
                using (var game = Process.GetProcessById(pid))
                {
                    WinAPIBindings.GetWindowThreadProcessId(hwnd, out var owner);
                    bool recovering = stalledPresent.HasValue;
                    if (recovering && (!Borderless || gameProcessId != pid || owner != pid
                        || WinAPIBindings.GetForegroundWindow() != hwnd)) return;
                    if (owner != game.Id) hwnd = game.MainWindowHandle;
                    if (hwnd == IntPtr.Zero) return;

                    var style = ReadWindowStyle(hwnd);
                    if ((style & WindowStyles.WS_MINIMIZE) != 0)
                    {
                        if (recovering) return;
                        WinAPIBindings.ShowWindow(hwnd, (int)WinAPIBindings.ShowWindowCommands.SW_RESTORE);
                        style = ReadWindowStyle(hwnd);
                    }

                    // SDL gives the game a popup window while it owns the fullscreen display.
                    // Let the engine leave that mode with its Alt+Enter shortcut before resizing.
                    var presentation = ReadPresentation(game.Id, hwnd);
                    if (recovering && (presentation?.Exclusive != true || presentation.Value.Present != stalledPresent
                        || WinAPIBindings.GetForegroundWindow() != hwnd)) return;
                    if (presentation?.Exclusive == true || ((style & WindowStyles.WS_POPUP) != 0
                        && (style & WindowStyles.WS_CAPTION) == 0))
                    {
                        if (recovering)
                            Logger.Info("Fullscreen presentation stalled after restore; applying the enabled borderless setting.");
                        else WinAPIBindings.SetForegroundWindow(hwnd);
                        const uint sysKeyDown = 0x104, sysKeyUp = 0x105;
                        PostGameKey(hwnd, sysKeyDown, 0x12, 0x00380001); // Alt
                        PostGameKey(hwnd, sysKeyDown, 0x0D, 0x201C0001); // Enter with Alt held
                        PostGameKey(hwnd, sysKeyUp, 0x0D, 0xE01C0001);
                        PostGameKey(hwnd, sysKeyUp, 0x12, 0xC0380001);

                        var timeout = Stopwatch.StartNew();
                        long firstWindowedPresent = 0;
                        do
                        {
                            if (timeout.ElapsedMilliseconds >= 5000)
                                throw new TimeoutException("Game did not switch to windowed mode.");
                            Thread.Sleep(50);
                            // SDL may recreate its window. Keep targeting the original while
                            // it is visible, even if another top-level window appears first.
                            if (!WinAPIBindings.IsWindowVisible(hwnd))
                            {
                                game.Refresh();
                                hwnd = game.MainWindowHandle;
                            }
                            if (recovering && (hwnd == IntPtr.Zero || WinAPIBindings.GetForegroundWindow() != hwnd)) return;
                            style = ReadWindowStyle(hwnd);
                            if ((style & WindowStyles.WS_CAPTION) == 0) continue;
                            if (presentation == null) break;
                            var current = ReadPresentation(game.Id, hwnd);
                            if (!current.HasValue || current.Value.Exclusive || current.Value.Present <= presentation.Value.Present)
                                continue;
                            if (firstWindowedPresent != 0 && current.Value.Present > firstWindowedPresent) break;
                            firstWindowedPresent = current.Value.Present;
                        } while (true);
                    }

                    if (recovering && WinAPIBindings.GetForegroundWindow() != hwnd) return;
                    if ((style & WindowStyles.WS_MAXIMIZE) != 0)
                    {
                        WinAPIBindings.ShowWindow(hwnd, (int)WinAPIBindings.ShowWindowCommands.SW_RESTORE);
                        style = ReadWindowStyle(hwnd);
                    }

                    var bounds = Screen.FromHandle(hwnd).Bounds;
                    // Preserve visibility and clipping (including the hosted options panel).
                    // Leaving WS_POPUP clear also makes repeated clicks stay in windowed mode.
                    var borderless = style & ~(WindowStyles.WS_OVERLAPPEDWINDOW | WindowStyles.WS_POPUP);
                    if (borderless == style && GetWindowRect(hwnd, out var currentRect)
                        && currentRect.Left == bounds.Left && currentRect.Top == bounds.Top
                        && currentRect.Right == bounds.Right && currentRect.Bottom == bounds.Bottom)
                        return;
                    if (borderless != style && WinAPIBindings.SetWindowLong32(hwnd, -16, (uint)borderless) == 0)
                        throw new Win32Exception();
                    const uint noZOrder = 0x0004, noActivate = 0x0010, frameChanged = 0x0020;
                    if (!WinAPIBindings.SetWindowPos(hwnd, IntPtr.Zero, bounds.Left, bounds.Top,
                        bounds.Width, bounds.Height, noZOrder | noActivate | (borderless != style ? frameChanged : 0)))
                        throw new Win32Exception();
                }
            } catch (Exception ex)
            {
                Logger.Error("Could not make a window borderless!", ex);
            }
            finally { Monitor.Exit(borderlessChange); }
        }
    }
}
