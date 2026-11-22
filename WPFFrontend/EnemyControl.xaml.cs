using BGOverlay;
using System;
using BGOverlay.Resources;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace WPFFrontend
{
    /// <summary>
    /// Interaction logic for EnemyControl.xaml
    /// </summary>
    public partial class EnemyControl : UserControl
    {
        private MainWindow mainWindow;

        private Dictionary<String, BuffControl> buffs = new Dictionary<string, BuffControl>();

        private BitmapImage emptyBuffIcon = new BitmapImage(new Uri($"icons/not_found.png", UriKind.Relative));

        public EnemyControl(BGEntity bgEntity, MainWindow mainWindow)
        {
            InitializeComponent();
            init(bgEntity, mainWindow);
        }

        private void init(BGEntity bgEntity, MainWindow mainWindow)
        {
            Logger.Debug($"Create Enemy Control for {bgEntity?.Name1}");
            try
            {
                this.mainWindow = mainWindow;
                this.updateView(bgEntity);

                // Wait for the localized width and the hosting canvas to finish layout.
                Loaded += PlaceCard;
                this.MouseRightButtonUp += EnemyControl_MouseRightButtonDown;
                WinApiBindings.WinAPIBindings.SetForegroundWindow(Configuration.HWndPtr);
                WinApiBindings.WinAPIBindings.SetFocus(Configuration.HWndPtr);
                Logger.Debug($"Create Enemy Control for {bgEntity?.Name1}: Done");
            } catch (Exception ex)
            {
                Logger.Error("Enemy Control error!", ex);
            }            
        }

        private void PlaceCard(object sender, RoutedEventArgs e)
        {
            Loaded -= PlaceCard;
            var width = mainWindow.MainCanvas.ActualWidth;
            var x = BGEntity.ViewportWidth > 0
                ? width * ((double)BGEntity.X - BGEntity.MousePosX1) / BGEntity.ViewportWidth
                : width / 2;
            var left = x > width / 2 ? x - ActualWidth - 50 : x + 50;
            Canvas.SetLeft(this, Math.Clamp(left, 0, Math.Max(0, width - ActualWidth)));
        }

        private void fetchWeaponEffects(BGEntity bgEntity)
        {
            var reader = bgEntity.Reader;
            if (!bgEntity.HasEquippedWeapon || string.IsNullOrEmpty(reader?.EquippedWeaponName))
            {
                WeaponPanel.Visibility = Visibility.Collapsed;
                return;
            }

            var lines = reader.OnHitEffectsStrings;
            var launcher = bgEntity.EquippedLauncher;
            WeaponIcon.DataContext = launcher.HasValue ? launcher.Value.Icon : reader.EquippedWeaponIcon;
            WeaponDescription.Text = launcher?.Name ?? lines[0];
            WeaponEffects.Text = string.Join("\n", lines.Skip(1));
            WeaponEffects.Visibility = lines.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            Damage.Content = new WeaponDamageDisplay(reader.WeaponDamageType, bgEntity.DamageRange);

            var offHand = bgEntity.OffHandWeapon;
            SecondaryWeaponDescription.Visibility = launcher.HasValue || offHand != null ? Visibility.Visible : Visibility.Collapsed;
            SecondaryWeaponIcon.DataContext = launcher.HasValue ? reader.EquippedWeaponIcon : offHand?.Icon;
            SecondaryWeaponDescription.Text = launcher.HasValue ? lines[0] : offHand?.IdentifiedName;
            SecondaryWeaponDamage.Visibility = offHand != null ? Visibility.Visible : Visibility.Collapsed;
            SecondaryWeaponDamage.Content = offHand != null
                ? new WeaponDamageDisplay(offHand.DamageType, bgEntity.OffHandDamageRange)
                : null;
            WeaponPanel.Visibility = Visibility.Visible;
        }

        private sealed class WeaponDamageDisplay(DamageType? type, string range)
        {
            public string Range { get; } = range ?? "—";
            public string AccessibleName => string.Format(RadarLocalization.Get("Str_DamageRange"), Range);
            public bool Slashing { get; } = type is DamageType.Slashing or DamageType.Piercing_or_Slashing or DamageType.Crushing_or_Slashing;
            public bool Piercing { get; } = type is DamageType.Piercing or DamageType.Piercing_or_Crushing or DamageType.Piercing_or_Slashing;
            public bool Blunt { get; } = type is DamageType.Crushing or DamageType.Crushing_AC or DamageType.Crushing_Uses_Base_AC
                or DamageType.Piercing_or_Crushing or DamageType.Crushing_or_Slashing or DamageType.Blunt_Missile;
            public bool Missile { get; } = type is DamageType.Missile or DamageType.Blunt_Missile;
        }

        private void EnemyControl_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            this.Label_MouseDown(null, null);
        }

        public BGEntity BGEntity { get; private set; }

        public void Label_MouseDown(object sender, MouseButtonEventArgs e)
        {
            var ofs                 = this.RenderTransform.Value.OffsetY;
            ThicknessAnimation anim = new ThicknessAnimation();
            anim.From               = this.Margin;
            var newMargin           = this.Margin;
            newMargin.Top           = -this.ActualHeight - ofs;
            anim.To                 = newMargin;
            anim.EasingFunction     = new BackEase() { Amplitude = .5, EasingMode = EasingMode.EaseIn };
            anim.Duration           = TimeSpan.FromSeconds(.45);
            anim.Completed         += (o, e) =>
            {
                mainWindow.deleteEnemyControlByTag(BGEntity.tag);
                (this.Parent as Canvas)?.Children.Remove(this);
            };
            anim.FillBehavior = FillBehavior.HoldEnd;
            this.BeginAnimation(System.Windows.Controls.UserControl.MarginProperty, anim, HandoffBehavior.SnapshotAndReplace);
            WinApiBindings.WinAPIBindings.SetForegroundWindow(Configuration.HWndPtr);
            WinApiBindings.WinAPIBindings.SetFocus(Configuration.HWndPtr);
        }

        /// <summary>
        /// Sets every stat label's Content directly instead of relying on XAML's
        /// ContentStringFormat="{DynamicResource ...}" - that binding only re-evaluates when
        /// WPF decides the Content itself changed, so it can end up stuck showing the format
        /// string as-is (or going stale after a locale switch) instead of the localized text.
        /// Doing it here, every updateView() call, keeps these in lockstep with the locale the
        /// same way BuffControl/App.xaml's font resources already work for everything else.
        /// </summary>
        private void updateLocalizedLabels(BGEntity item)
        {
            string L(string key) => RadarLocalization.Get(key);
            string F(string key, object value) => string.Format(L(key), value);

            // Rolls the number down (see RollingLabel) over one second when it decreases, the
            // same way MainWindow's enemy list already does for HP, instead of jumping straight
            // to the new value.
            void R(RollingLabel label, string key, int value)
            {
                label.Format = L(key);
                label.Value  = value;
            }

            // Multiclass level is a composite string (e.g. "9/9/12"), not one number - can't
            // roll-down animate that, so it stays a plain Label.
            this.Level.Content     = F("Str_Level", item.DerivedStatsTemp.Level);
            this.Race.Content      = F("Str_Race", item.Race);

            // Only the base score animates - the "/exceptional" percentile (e.g. 18/76), when
            // present, is baked into the format string as a literal suffix, like Health's max HP.
            var strSuffix = item.DerivedStatsTemp.STRExceptional > 0 ? "/" + item.DerivedStatsTemp.STRExceptional : "";
            this.STR.Format = string.Format(L("Str_STR"), "{0}" + strSuffix);
            this.STR.Value  = item.DerivedStatsTemp.STR;
            R(this.DEX, "Str_DEX", item.DerivedStatsTemp.DEX);
            R(this.CON, "Str_CON", item.DerivedStatsTemp.CON);
            R(this.INT, "Str_INT", item.DerivedStatsTemp.INT);
            R(this.WIS, "Str_WIS", item.DerivedStatsTemp.WIS);
            R(this.CHA, "Str_CHA", item.DerivedStatsTemp.CHA);

            // Only current HP animates - max HP is baked into the format string as literal text.
            this.Health.Format = string.Format(L("Str_Health"), "{0}/" + item.DerivedStatsTemp.MaxHP);
            this.Health.Value  = item.CurrentHP;
            // No "{0}" in these Format values (Attacks/ShortAlignment aren't plain numbers), so
            // RollingLabel's string.Format just renders them as-is and Value is irrelevant.
            this.APR.Format              = F("Str_Attacks", item.Attacks);
            R(this.AC, "Str_ArmorClass", item.DerivedStatsTemp.ArmorClass);
            R(this.THAC0, "Str_THAC0", item.THAC0);
            R(this.XP, "Str_Experience", item.Reader.XPGained);
            this.Alignment.Format       = F("Str_Alignment", item.ShortAlignment);
            R(this.SaveDeath, "Str_SaveDeath", item.DerivedStatsTemp.SaveVsDeath);
            R(this.SaveWands, "Str_SaveWands", item.DerivedStatsTemp.SaveVsWands);
            R(this.SavePolymorph, "Str_SavePoly", item.DerivedStatsTemp.SaveVsPoly);
            R(this.SaveBreath, "Str_SaveBreath", item.DerivedStatsTemp.SaveVsBreath);
            R(this.SaveSpells, "Str_SaveSpells", item.DerivedStatsTemp.SaveVsSpell);

            R(this.ResFire, "Str_ResFire", item.DerivedStatsTemp.ResistFire);
            R(this.ResCold, "Str_ResCold", item.DerivedStatsTemp.ResistCold);
            R(this.ResElectro, "Str_ResElectricity", item.DerivedStatsTemp.ResistElectricity);
            R(this.ResAcid, "Str_ResAcid", item.DerivedStatsTemp.ResistAcid);
            R(this.ResMagic, "Str_ResMagic", item.DerivedStatsTemp.ResistMagic);
            R(this.ResMagicDamage, "Str_ResMagicDamage", item.DerivedStatsTemp.ResistMagicDamage);
            R(this.ResPoison, "Str_ResPoison", item.DerivedStatsTemp.ResistPoison);
            R(this.ResSlashing, "Str_ResSlashing", item.DerivedStatsTemp.ResistSlashing);
            R(this.ResCrushing, "Str_ResCrushing", item.DerivedStatsTemp.ResistCrushing);
            R(this.ResPiercing, "Str_ResPiercing", item.DerivedStatsTemp.ResistPiercing);
            R(this.ResMissile, "Str_ResMissile", item.DerivedStatsTemp.ResistMissile);

            this.PocketsHeader.Content  = L("Str_Pockets");
        }

        internal void updateView(BGEntity item)
        {
            try
            {
                this.BGEntity = item;
                if (item.Type != 49)
                    return;
                this.BGEntity.LoadCREResource();
                if (this.BGEntity.Reader != null)
                {
                    //this.BGEntity.CreResourceFilename = this.BGEntity.Reader.ShortName;
                }
                this.BGEntity.loadTimedEffects();
                this.BGEntity.LoadDerivedStats();
                this.fetchWeaponEffects(item);
                this.DataContext = this.BGEntity;
                this.updateLocalizedLabels(item);

                BuffStack.CenterRows = Configuration.CenterBuffIcons;
                foreach (var buff in buffs)
                {
                    if ((buff.Value.BuffDurationAbsolute != 0 && buff.Value.BuffDurationAbsolute < this.BGEntity.GameTime)
                            || !this.BGEntity.SpellProtection.Any(x => x.Item1 == buff.Key))
                    {
                        this.BuffStack.Children.Remove(buff.Value);
                        buffs.Remove(buff.Key);
                    }
                }

                if (this.BGEntity.SpellProtection.Count > 0)
                {
                    this.BGEntity.SpellProtection.ForEach(x =>
                    {
                        int rounds = ((int)x.Item3 - (int)this.BGEntity.GameTime) / 15 / 6;
                        float durationFloat = (float)rounds / 10;

                        bool isPresent = buffs.ContainsKey(x.Item1);
                        if (isPresent)
                        {
                            buffs[x.Item1].Invalidate();
                            buffs[x.Item1].BuffDurationAbsolute = x.Item3;
                            buffs[x.Item1].BuffDuration = x.Item3 == uint.MaxValue ? float.MaxValue : durationFloat;
                            buffs[x.Item1].UpdateTooltip(this.BGEntity.GameTime);
                            return;
                        }
                        BitmapSource newIcon;
                        if (x.Item2 != null)
                        {
                            newIcon = Imaging.CreateBitmapSourceFromHBitmap(
                            x.Item2.GetHbitmap(),
                            IntPtr.Zero,
                            Int32Rect.Empty,
                            BitmapSizeOptions.FromWidthAndHeight(x.Item2.Width, x.Item2.Height));
                        }
                        else
                        {
                            newIcon = emptyBuffIcon;
                        }

                        var buffControl = new BuffControl()
                        {
                            BuffName = x.Item1,
                            BuffDuration = x.Item3 == uint.MaxValue ? float.MaxValue : durationFloat,
                            Icon = newIcon,
                            BuffDurationAbsolute = x.Item3
                        };
                        buffControl.UpdateTooltip(this.BGEntity.GameTime);
                        BuffStack.Children.Add(buffControl);
                        this.buffs.Add(x.Item1, buffControl);
                    });
                }
                BuffStack.Visibility = BuffStack.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

                protectionsPanel.Visibility = item.Protections.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                pocketsSection.Visibility = item.Pockets.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            } catch (Exception ex)
            {
                Logger.Error("Enemy Control: Update View error!", ex);
            }
            
        }

        private void UserControl_MouseLeave(object sender, MouseEventArgs e)
        {
            if (WinApiBindings.WinAPIBindings.GetForegroundWindow() != Configuration.HWndPtr)
            {
                WinApiBindings.WinAPIBindings.SetForegroundWindow(Configuration.HWndPtr);
                WinApiBindings.WinAPIBindings.SetFocus(Configuration.HWndPtr);
            }            
        }

        private void UserControl_MouseUp(object sender, MouseButtonEventArgs e)
        {
            UserControl_MouseLeave(null, null);
        }
    }
}
