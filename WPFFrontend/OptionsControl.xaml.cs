using BGOverlay;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;
using UserControl = System.Windows.Controls.UserControl;

namespace WPFFrontend
{
    /// <summary>
    /// Interaction logic for OptionsControl.xaml
    /// </summary>
    public partial class OptionsControl : UserControl
    {
        private bool hidden = true;

        /// <summary>
        /// Raised right after the Debug Mode checkbox is toggled (Configuration.DebugMode is
        /// already updated by then) - MainWindow uses this to invalidate the pooled BGEntity
        /// cache and pick up debug-only display rules (e.g. the "[CRE filename]" name suffix)
        /// that are otherwise only computed once, when an entity is first cached.
        /// </summary>
        public event Action DebugModeChanged;

        /// <summary>
        /// Raised right after a new locale is picked (Configuration.Locale and
        /// RadarLocalization.Strings are already updated by then) - MainWindow uses this the
        /// same way as DebugModeChanged, since some display text (e.g. the Pockets section's
        /// Stealable/Droppable flag words) is also only computed once, when a CREReader is
        /// first cached.
        /// </summary>
        public event Action LocaleChanged;

        public OptionsControl()
        {
            InitializeComponent();
            this.Version.Content             = $"Ver. {Configuration.Version}";
            this.HidePartyMembers.Click     += updateConfig;
            this.HideNeutrals.Click         += updateConfig;
            this.HideAllies.Click           += updateConfig;
            this.EnableBorderlessMode.Click += updateConfig;
            this.RefreshRate.TextChanged    += updateConfig;
            this.BigBuffIcons.Click         += updateConfig;
            this.UseShiftClick.Click        += updateConfig;
            this.DebugMode.Click            += updateConfig;
            this.DebugMode.Click            += (s, e) => DebugModeChanged?.Invoke();
            this.TwitchIntegrationEnabled.Click += updateConfig;
            this.TwitchRelayUrl.TextChanged += updateConfig;
            this.MouseUp                    += OptionsControl_MouseUp;
            this.CloseBtn.MouseUp           += Label_MouseDown;
            var app                          = System.Windows.Application.Current;
            this.Font1.Content               = $"{Configuration.Font1}, {Configuration.FontSize1}";
            this.Font2.Content               = $"{Configuration.Font2}, {Configuration.FontSize2}";
            this.Font3.Content               = Configuration.BigBuffIcons
                ? $"{Configuration.Font3}, {Configuration.FontSize3Big}"
                : $"{Configuration.Font3}, {Configuration.FontSize3Small}";
            initLocale();
            initTwitchStatusPolling();
        }

        /// <summary>
        /// TwitchRelayClient's connection loop runs on its own background task (see
        /// ProcessHacker.MainLoop -> TwitchRelayClient.UpdateConfig), so the status label can't
        /// just be set once - it polls TwitchRelayClient.Instance.Status on the UI thread every
        /// second for as long as this control exists (created once and kept alive for the whole
        /// app lifetime, same as OptionsControl itself), instead of wiring a cross-thread event.
        /// </summary>
        private void initTwitchStatusPolling()
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += (s, e) => updateTwitchStatusLabel();
            timer.Start();
            updateTwitchStatusLabel();
        }

        private void updateTwitchStatusLabel()
        {
            var key = TwitchRelayClient.Instance.Status switch
            {
                TwitchRelayStatus.Connecting => "Str_TwitchStatusConnecting",
                TwitchRelayStatus.Connected  => "Str_TwitchStatusConnected",
                TwitchRelayStatus.Error      => "Str_TwitchStatusError",
                _                            => "Str_TwitchStatusDisabled",
            };
            this.TwitchStatus.Content = RadarLocalization.Get(key);

            // A queue that isn't draining means the game isn't consuming requests - almost
            // always because it's paused. Without this the summon just appears to do nothing.
            var queued = GameSpawnBridge.Instance.QueueLength;
            if (queued > 0 && GameSpawnBridge.Instance.IsStalled)
                showSummonResult(false, string.Format(RadarLocalization.Get("Str_TwitchSummonStalled"), queued));
        }

        private void initLocale()
        {
            var availableLocales          = new DirectoryInfo(Path.Combine(Configuration.GameFolder, "lang")).EnumerateDirectories();
            this.Locale.ItemsSource       = availableLocales.Select(x => x.ToString().ToLower().Substring(x.ToString().LastIndexOf('\\') + 1));
            this.Locale.SelectedItem      = Configuration.Locale;
            this.Locale.SelectionChanged += updateConfig;
            // updateConfig() (above) writes the new selection into Configuration.Locale first;
            // reload the radar's own UI text right after so the change is visible immediately
            // instead of only after restarting the radar.
            this.Locale.SelectionChanged += (s, e) => MainWindow.ApplyLocalization();
            this.Locale.SelectionChanged += (s, e) => LocaleChanged?.Invoke();
        }

        private void OptionsControl_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (this.hidden || e != null && e.ChangedButton != MouseButton.Right)
                return;
            
            ThicknessAnimation anim = new ThicknessAnimation();
            anim.From               = this.Margin;
            var newMargin           = this.Margin;
            newMargin.Top           = -this.ActualHeight;
            anim.To                 = newMargin;
            anim.EasingFunction     = new BackEase() { Amplitude = .3, EasingMode = EasingMode.EaseIn };
            anim.Duration           = TimeSpan.FromSeconds(.45);
            anim.Completed         += (o, e) => 
            {
                this.hidden = true;
                this.Margin = new Thickness(0, -this.ActualHeight, 0, 0);
            };
            anim.FillBehavior = FillBehavior.HoldEnd;
            this.BeginAnimation(UserControl.MarginProperty, anim, HandoffBehavior.SnapshotAndReplace);
            WinApiBindings.WinAPIBindings.SetForegroundWindow(Configuration.HWndPtr);
            WinApiBindings.WinAPIBindings.SetFocus(Configuration.HWndPtr);
        }

        private void TextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            Regex regex = new Regex("[^0-9]+");
            e.Handled = regex.IsMatch(e.Text);
        }

        private void ForceBorderless_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            Configuration.ForceBorderless();
        }

        private void Save_Click(object sender, System.Windows.RoutedEventArgs e)
        {            
            Configuration.SaveConfig();
            this.Label_MouseDown(null, null);
        }

        private void updateConfig(object sender, object args)
        {
            Configuration.HidePartyMembers    = (bool)this.HidePartyMembers.IsChecked;
            Configuration.HideNeutrals        = (bool)this.HideNeutrals.IsChecked;
            Configuration.HideAllies          = (bool)this.HideAllies.IsChecked;
            Configuration.Borderless          = (bool)this.EnableBorderlessMode.IsChecked;
            Configuration.RefreshTimeMS       = int.Parse(this.RefreshRate.Text);
            Configuration.BigBuffIcons        = (bool)this.BigBuffIcons.IsChecked;
            Configuration.UseShiftClick       = (bool)this.UseShiftClick.IsChecked;
            Configuration.DebugMode           = (bool)this.DebugMode.IsChecked;
            Configuration.Locale              = this.Locale.SelectedValue.ToString();
            Configuration.TwitchIntegrationEnabled = (bool)this.TwitchIntegrationEnabled.IsChecked;
            Configuration.TwitchRelayUrl       = this.TwitchRelayUrl.Text;
            Configuration.TwitchStreamKey      = this.TwitchStreamKey.Text;

            this.Font3.Content = Configuration.BigBuffIcons
                ? $"{Configuration.Font3}, {Configuration.FontSize3Big}"
                : $"{Configuration.Font3}, {Configuration.FontSize3Small}";
        }

        private List<SpawnPack> spawnPacks = new List<SpawnPack>();

        /// <summary>
        /// Queues the pack matching the protagonist's current level - the same path a viewer
        /// redemption takes - so the whole chain can be exercised before anything Twitch-facing
        /// is wired up.
        /// </summary>
        /// <summary>
        /// Queues the pack currently selected in the list, so a pack can be tried out directly
        /// while authoring it. Note this deliberately bypasses level matching - a real viewer
        /// summon still resolves the pack from the protagonist's level (see
        /// TwitchRelayClient.handleCommand).
        /// </summary>
        private void TestSummon_Click(object sender, RoutedEventArgs e)
        {
            var pack = selectedPack();
            if (pack == null)
                return;

            // Sent with a sample message so the test exercises the viewer-text path too. Plain
            // ASCII and deliberately not localised: GameSpawnBridge drops anything outside
            // printable ASCII, since the engine's message log renders from a single-byte
            // codepage, so a translated string would show up in game as an empty line.
            GameSpawnBridge.Instance.Enqueue(pack.Entries, "Test summon from the radar overlay");
            showSummonResult(true, string.Format(RadarLocalization.Get("Str_TwitchPackQueued"), pack.ToString()));
        }

        private SpawnPack selectedPack()
        {
            var index = this.PackList.SelectedIndex;
            return index >= 0 && index < spawnPacks.Count ? spawnPacks[index] : null;
        }

        /// <summary>
        /// Save/Delete/Test all act on the selected pack, so they stay disabled until there is
        /// one. New creates and selects a pack rather than just clearing the selection -
        /// otherwise a disabled Save would leave no way to ever add one.
        /// </summary>
        private void updatePackButtons()
        {
            var hasSelection = selectedPack() != null;
            this.PackSave.IsEnabled   = hasSelection;
            this.PackDelete.IsEnabled = hasSelection;
            this.TestSummon.IsEnabled = hasSelection;
        }

        private void showSummonResult(bool ok, string text)
        {
            this.SummonResult.Foreground = ok
                ? System.Windows.Media.Brushes.DarkGreen
                : System.Windows.Media.Brushes.DarkRed;
            this.SummonResult.Text = text;
        }

        /// <summary>
        /// Rebuilds the list, restoring the given selection - clearing Items resets
        /// SelectedIndex, which would otherwise drop the selection (and disable the buttons)
        /// every time a pack is saved.
        /// </summary>
        private void refreshPackList(int selectIndex = -1)
        {
            this.PackList.Items.Clear();
            foreach (var pack in spawnPacks)
                this.PackList.Items.Add(pack.ToString());

            if (selectIndex >= 0 && selectIndex < this.PackList.Items.Count)
                this.PackList.SelectedIndex = selectIndex;

            updatePackButtons();
        }

        private void PackList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            updatePackButtons();

            var pack = selectedPack();
            if (pack == null)
                return;

            this.PackName.Text      = pack.Name ?? "";
            this.PackLevelFrom.Text = pack.LevelFrom.ToString();
            this.PackLevelTo.Text   = pack.LevelTo.ToString();
            this.PackEntries.Text   = SpawnPack.ToEntryLines(pack.Entries);
        }

        /// <summary>
        /// Saves the editor contents over the selected pack. Packs are matched to a level band
        /// at summon time, so overlapping bands are allowed - the narrowest match wins.
        /// </summary>
        private void PackSave_Click(object sender, RoutedEventArgs e)
        {
            var index = this.PackList.SelectedIndex;
            if (index < 0 || index >= spawnPacks.Count)
                return;

            if (!int.TryParse(this.PackLevelFrom.Text.Trim(), out var from) ||
                !int.TryParse(this.PackLevelTo.Text.Trim(), out var to) ||
                from < 1 || to < from)
            {
                showSummonResult(false, RadarLocalization.Get("Str_TwitchPackBadRange"));
                return;
            }

            var entries = SpawnPack.ParseEntryLines(this.PackEntries.Text, out var rejected);
            if (entries.Count == 0)
            {
                showSummonResult(false, RadarLocalization.Get("Str_TwitchPackNoCreatures"));
                return;
            }

            // Sanitised rather than validated: the name carries the pack's identity for viewers,
            // and rejecting a save because someone typed a colon would be a worse trade than
            // quietly dropping the character. The editor shows what was kept straight after.
            var name = SpawnPack.SanitizeName(this.PackName.Text);
            this.PackName.Text = name;

            spawnPacks[index] = new SpawnPack { Name = name, LevelFrom = from, LevelTo = to, Entries = entries };

            persistPacks();
            refreshPackList(index);

            // Surfaced rather than silently dropped, so a typo'd ResRef doesn't vanish without
            // the user noticing it never made it into the pack.
            showSummonResult(rejected.Count == 0,
                rejected.Count == 0
                    ? RadarLocalization.Get("Str_TwitchPackSaved")
                    : string.Format(RadarLocalization.Get("Str_TwitchPackSavedWithErrors"), string.Join(", ", rejected)));
        }

        private void PackDelete_Click(object sender, RoutedEventArgs e)
        {
            var index = this.PackList.SelectedIndex;
            if (index < 0 || index >= spawnPacks.Count)
                return;

            spawnPacks.RemoveAt(index);
            persistPacks();

            // Keep a neighbour selected so the buttons don't go dead after every delete.
            refreshPackList(Math.Min(index, spawnPacks.Count - 1));
            showSummonResult(true, RadarLocalization.Get("Str_TwitchPackDeleted"));
        }

        /// <summary>
        /// Adds an empty pack and selects it. It only reaches config.cfg once it has creatures -
        /// SpawnPack.Serialize skips empty packs - so abandoning a new pack costs nothing.
        /// </summary>
        private void PackNew_Click(object sender, RoutedEventArgs e)
        {
            spawnPacks.Add(new SpawnPack { LevelFrom = 1, LevelTo = 1 });
            refreshPackList(spawnPacks.Count - 1);

            this.PackName.Text      = "";
            this.PackLevelFrom.Text = "1";
            this.PackLevelTo.Text   = "1";
            this.PackEntries.Text   = "";
            this.PackName.Focus();
        }

        private void persistPacks()
        {
            Configuration.SpawnPacks = SpawnPack.Serialize(spawnPacks);
            Configuration.SaveConfig();
        }

        private void GenerateStreamKey_Click(object sender, RoutedEventArgs e)
        {
            // Lowercase hex only (no dashes) - Configuration.getProperty() lowercases every
            // persisted value on load, so anything with uppercase characters would get mangled
            // by a config.cfg round-trip.
            //
            // The control key is regenerated alongside it. The stream key goes into the Twitch
            // Extension config (and so reaches viewers' browsers), while this one authorizes
            // summons coming back down - it is shown here for the streamer to paste into the
            // local extension mock, and must go nowhere else.
            this.TwitchStreamKey.Text = Guid.NewGuid().ToString("N");
            Configuration.TwitchControlKey = Guid.NewGuid().ToString("N");
            this.TwitchControlKey.Text = Configuration.TwitchControlKey;
            updateConfig(null, null);
        }

        public void Init()
        {
            this.HidePartyMembers.IsChecked     = Configuration.HidePartyMembers;
            this.HideNeutrals.IsChecked         = Configuration.HideNeutrals;
            this.HideAllies.IsChecked           = Configuration.HideAllies;
            this.EnableBorderlessMode.IsChecked = Configuration.Borderless;
            this.RefreshRate.Text               = Configuration.RefreshTimeMS.ToString();
            this.BigBuffIcons.IsChecked         = Configuration.BigBuffIcons;
            this.UseShiftClick.IsChecked        = Configuration.UseShiftClick;
            this.DebugMode.IsChecked            = Configuration.DebugMode;
            this.TwitchIntegrationEnabled.IsChecked = Configuration.TwitchIntegrationEnabled;
            this.TwitchRelayUrl.Text            = Configuration.TwitchRelayUrl;
            this.TwitchStreamKey.Text           = Configuration.TwitchStreamKey;
            this.TwitchControlKey.Text          = Configuration.TwitchControlKey;

            spawnPacks = SpawnPack.Deserialize(Configuration.SpawnPacks);
            refreshPackList();
        }

        public void Show()
        {
            if (this.hidden == false)
            {                
                OptionsControl_MouseUp(null, null);
                return;
            }
            
            this.Margin = new Thickness(0, -this.ActualHeight, 0, 0);
            this.Visibility         = System.Windows.Visibility.Visible;            
            ThicknessAnimation anim = new ThicknessAnimation();
            var margin1             = this.Margin;
            margin1.Top            /= 2;
            anim.From               = margin1;            
            var newMargin           = this.Margin;
            newMargin.Top           = 0;
            anim.To                 = newMargin;
            anim.EasingFunction     = new PowerEase() { Power = 10, EasingMode = EasingMode.EaseOut };
            anim.Duration           = TimeSpan.FromSeconds(.85);
            //anim.FillBehavior       = FillBehavior.HoldEnd;
            anim.Completed += (o,e) => this.hidden = false;
            this.BeginAnimation(UserControl.MarginProperty, anim, HandoffBehavior.SnapshotAndReplace);
        }

        private void Label_MouseDown(object sender, MouseButtonEventArgs e)
        {
            OptionsControl_MouseUp(null, null);
        }

        private void SelectFont(object sender, RoutedEventArgs e)
        {
            var button = (Button)sender;
            var dialog = new FontDialog();
            var split = button.Content.ToString().Split(",");
            dialog.Font = new System.Drawing.Font(split[0].Trim(), float.Parse(split[1].Trim()));

            if (dialog.ShowDialog() == DialogResult.OK)
            {
                var str = $"{dialog.Font.Name}, {Convert.ToInt32(dialog.Font.Size)}";
                button.Content = str;
                var app = System.Windows.Application.Current;
                switch (button.Name)
                {
                    case "Font1":
                        app.Resources["FontFamily1"] = new FontFamily(dialog.Font.Name);
                        app.Resources["FontSize1"] = Convert.ToDouble(dialog.Font.Size);
                        Configuration.Font1 = dialog.Font.Name;
                        Configuration.FontSize1 = Convert.ToInt32(dialog.Font.Size).ToString();
                        break;
                    case "Font2":
                        app.Resources["FontFamily2"] = new FontFamily(dialog.Font.Name);
                        app.Resources["FontSize2"] = Convert.ToDouble(dialog.Font.Size);
                        Configuration.Font2 = dialog.Font.Name;
                        Configuration.FontSize2 = Convert.ToInt32(dialog.Font.Size).ToString();
                        break;
                    case "Font3":
                        app.Resources["FontFamilyBuff"] = new FontFamily(dialog.Font.Name);
                        Configuration.Font3 = dialog.Font.Name;
                        var key = Configuration.BigBuffIcons 
                            ? "FontSize3Big"
                            : "FontSize3Small";
                        app.Resources[key] = Convert.ToDouble(dialog.Font.Size);
                        if (key.EndsWith("Big"))
                            Configuration.FontSize3Big = Convert.ToInt32(dialog.Font.Size).ToString();
                        else 
                            Configuration.FontSize3Small = Convert.ToInt32(dialog.Font.Size).ToString();
                        break;
                }                
            };
        }        
    }
}
