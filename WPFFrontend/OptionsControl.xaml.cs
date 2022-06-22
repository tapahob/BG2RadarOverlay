using BGOverlay;
using System;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
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
            this.MouseUp                    += OptionsControl_MouseUp;
            this.CloseBtn.MouseUp           += Label_MouseDown;
            var app = System.Windows.Application.Current;
            this.Font1.Content = $"{Configuration.Font1}, {Configuration.FontSize1}";
            this.Font2.Content = $"{Configuration.Font2}, {Configuration.FontSize2}";
            this.Font3.Content = Configuration.BigBuffIcons 
                ? $"{Configuration.Font3}, {Configuration.FontSize3Big}"
                : $"{Configuration.Font3}, {Configuration.FontSize3Small}";
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
            Configuration.HidePartyMembers = (bool)this.HidePartyMembers.IsChecked;
            Configuration.HideNeutrals     = (bool)this.HideNeutrals.IsChecked;
            Configuration.HideAllies       = (bool)this.HideAllies.IsChecked;
            Configuration.Borderless       = (bool)this.EnableBorderlessMode.IsChecked;
            Configuration.RefreshTimeMS    = int.Parse(this.RefreshRate.Text);
            Configuration.BigBuffIcons     = (bool)this.BigBuffIcons.IsChecked;

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
