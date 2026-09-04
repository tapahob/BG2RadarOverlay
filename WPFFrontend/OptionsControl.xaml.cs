using BGOverlay;
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
            this.MouseUp                    += OptionsControl_MouseUp;
            this.CloseBtn.MouseUp           += Label_MouseDown;
            var app                          = System.Windows.Application.Current;
            this.Font1.Content               = $"{Configuration.Font1}, {Configuration.FontSize1}";
            this.Font2.Content               = $"{Configuration.Font2}, {Configuration.FontSize2}";
            this.Font3.Content               = Configuration.BigBuffIcons 
                ? $"{Configuration.Font3}, {Configuration.FontSize3Big}"
                : $"{Configuration.Font3}, {Configuration.FontSize3Small}";
            initLocale();
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
        }

        private void OptionsControl_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (this.hidden || e != null && e.ChangedButton != MouseButton.Right)
                return;
            
            var ofs                 = this.RenderTransform.Value.OffsetY;
            ThicknessAnimation anim = new ThicknessAnimation();
            anim.From               = this.Margin;
            var newMargin           = this.Margin;
            newMargin.Top           = -this.ActualHeight - ofs;
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

            this.Font3.Content = Configuration.BigBuffIcons 
                ? $"{Configuration.Font3}, {Configuration.FontSize3Big}"
                : $"{Configuration.Font3}, {Configuration.FontSize3Small}";
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

        private bool _isDraggingOptions;
        private Point _optionsDragStartMouse;
        private double _optionsDragStartOffsetX;
        private double _optionsDragStartOffsetY;

        private void OptionsControl_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            try
            {
                // Just note where the click/drag started - don't capture the mouse or mark the
                // event handled yet, so a plain click on a checkbox/button/etc. inside the
                // panel still reaches its own handler untouched.
                _isDraggingOptions = false;
                _optionsDragStartMouse = e.GetPosition((IInputElement)this.Parent);
                var transform = (TranslateTransform)this.RenderTransform;
                _optionsDragStartOffsetX = transform.X;
                _optionsDragStartOffsetY = transform.Y;
            }
            catch (Exception ex)
            {
                Logger.Error($"{nameof(OptionsControl_PreviewMouseLeftButtonDown)} error!", ex);
            }
        }

        private void OptionsControl_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            try
            {
                if (e.LeftButton != MouseButtonState.Pressed)
                    return;

                var current = e.GetPosition((IInputElement)this.Parent);
                var deltaX  = current.X - _optionsDragStartMouse.X;
                var deltaY  = current.Y - _optionsDragStartMouse.Y;

                if (!_isDraggingOptions)
                {
                    // Free 2D drag (unlike the enemy list's horizontal-only drag), so arm as
                    // soon as movement on either axis exceeds its own threshold.
                    if (Math.Abs(deltaX) < SystemParameters.MinimumHorizontalDragDistance
                        && Math.Abs(deltaY) < SystemParameters.MinimumVerticalDragDistance)
                        return;

                    _isDraggingOptions = true;
                    this.CaptureMouse();
                }

                e.Handled = true;

                var transform = (TranslateTransform)this.RenderTransform;
                transform.X = _optionsDragStartOffsetX + deltaX;
                transform.Y = _optionsDragStartOffsetY + deltaY;
            }
            catch (Exception ex)
            {
                Logger.Error($"{nameof(OptionsControl_PreviewMouseMove)} error!", ex);
            }
        }

        private void OptionsControl_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            try
            {
                if (!_isDraggingOptions)
                    return;

                _isDraggingOptions = false;
                this.ReleaseMouseCapture();
                e.Handled = true;
            }
            catch (Exception ex)
            {
                Logger.Error($"{nameof(OptionsControl_PreviewMouseLeftButtonUp)} error!", ex);
            }
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
