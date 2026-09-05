using BGOverlay;
using System;
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
        private string cachedWeaponName;

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

                double left = 0;
                var height  = System.Windows.SystemParameters.PrimaryScreenHeight;
                var width   = System.Windows.SystemParameters.PrimaryScreenWidth;
                var x       = width * (bgEntity.X - bgEntity.MousePosX1) / bgEntity.ViewportWidth;
                var y       = height * (bgEntity.Y - bgEntity.MousePosY1) / bgEntity.ViewportHeight;

                if (x > (width / 2))
                {
                    left = x - this.Width - 50;
                }
                else
                {
                    left = x + 50;
                }
                //Canvas.SetTop(this, y / 2);
                Canvas.SetLeft(this, left);
                this.MouseRightButtonUp += EnemyControl_MouseRightButtonDown;
                WinApiBindings.WinAPIBindings.SetForegroundWindow(Configuration.HWndPtr);
                WinApiBindings.WinAPIBindings.SetFocus(Configuration.HWndPtr);
                Logger.Debug($"Create Enemy Control for {bgEntity?.Name1}: Done");
            } catch (Exception ex)
            {
                Logger.Error("Enemy Control error!", ex);
            }            
        }

        private void fetchWeaponEffects(BGEntity bgEntity)
        {
            if (bgEntity.Reader.EquippedWeaponName != this.cachedWeaponName 
                && !(bgEntity.Reader.EquippedWeaponName == "None" && bgEntity.Reader.Enchantment == 0) 
                && this.BGEntity.Reader.OnHitEffectsStrings.Count > 0)
            {
                this.itemEffectsListView.Items.Clear();
                BitmapSource newIcon;
                var icon = bgEntity.Reader.EquippedWeaponIcon;
                if (icon != null)
                {
                    newIcon = Imaging.CreateBitmapSourceFromHBitmap(
                    icon.GetHbitmap(),
                    IntPtr.Zero,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromWidthAndHeight(icon.Width, icon.Height));
                    var item = new StackPanel() { Orientation = Orientation.Horizontal };
                    item.Children.Add(new Image() { MaxHeight = 24, Source = newIcon });
                    item.Children.Add(weaponLabel(this.BGEntity.Reader.OnHitEffectsStrings[0]));
                    this.itemEffectsListView.Items.Add(item);
                    // Only add a second row when there's actually more content - an empty Label
                    // still takes up its own height, which left an unbalanced gap under the
                    // weapon name/icon row whenever OnHitEffectsStrings had just the one entry.
                    if (this.BGEntity.Reader.OnHitEffectsStrings.Count > 1)
                    {
                        var extra = weaponLabel(string.Join("\n", this.BGEntity.Reader.OnHitEffectsStrings.Skip(1)));
                        extra.Padding = new Thickness(0, 0, 0, -10);
                        this.itemEffectsListView.Items.Add(extra);
                    }
                }
                else
                {
                    this.itemEffectsListView.Items.Add(weaponLabel(string.Join("\n", this.BGEntity.Reader.OnHitEffectsStrings)));
                }
                this.cachedWeaponName = bgEntity.Reader.EquippedWeaponName;
                this.itemEffectsListView.Visibility = Visibility.Visible;
            }
        }

        /// <summary>
        /// A Label for the Weapon section using FontFamily2/FontSize2 explicitly (matching the
        /// default Label style it'd get anyway, but pinned here so it stays in lockstep with
        /// Pockets' own explicit choice rather than relying on the implicit style). SetResourceReference
        /// (not a one-time value read) so it keeps tracking a live font change from Options, the
        /// same way DynamicResource would in XAML.
        /// </summary>
        private Label weaponLabel(string content)
        {
            var label = new Label { Content = content };
            label.SetResourceReference(Control.FontFamilyProperty, "FontFamily2");
            label.SetResourceReference(Control.FontSizeProperty, "FontSize2");
            return label;
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
            this.Alignment.Format       = F("Str_Alignment", item.Reader.ShortAlignment);
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
                this.BGEntity.loadTimedEffects();
                this.BGEntity.LoadDerivedStats();
                this.fetchWeaponEffects(item);
                this.DataContext = this.BGEntity;
                this.updateLocalizedLabels(item);

                if (Configuration.BigBuffIcons && BuffStack.Columns == 16)
                {
                    BuffStack.Columns = 8;
                    foreach (var buff in buffs)
                    {
                        buff.Value.Invalidate();
                    }
                }

                if (!Configuration.BigBuffIcons && BuffStack.Columns == 8)
                {
                    BuffStack.Columns = 16;
                    foreach (var buff in buffs)
                    {
                        buff.Value.Invalidate();
                    }
                }

                foreach (var buff in buffs)
                {
                    if ((buff.Value.BuffDurationAbsolute != 0 && buff.Value.BuffDurationAbsolute - this.BGEntity.GameTime < 0)
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
                        var timeString = rounds > 10 ? $"{(float)rounds / 10}" : $"{rounds}";

                        float durationFloat = rounds > 10 ? (float)rounds / 10 : rounds;

                        bool isPresent = buffs.ContainsKey(x.Item1);
                        if (isPresent)
                        {
                            buffs[x.Item1].BuffDuration = x.Item3 == uint.MaxValue ? float.MaxValue : durationFloat;
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
                            newIcon = new BitmapImage(new Uri($"icons/not_found.png", UriKind.Relative));
                        }

                        var buffControl = new BuffControl()
                        {
                            BuffName = x.Item1,
                            BuffDuration = x.Item3 == uint.MaxValue ? float.MaxValue : durationFloat,
                            Icon = newIcon,
                            BuffDurationAbsolute = x.Item3
                        };
                        BuffStack.Children.Add(buffControl);
                        this.buffs.Add(x.Item1, buffControl);
                    });
                    this.BuffStack.Visibility = Visibility.Visible;
                }

                if (this.BGEntity.Protections.Count > 0)
                {
                    this.protectionsListView.Visibility = Visibility.Visible;
                }

                if (this.BGEntity.Pockets.Count > 0)
                {
                    this.pocketsSection.Visibility = Visibility.Visible;
                }
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
