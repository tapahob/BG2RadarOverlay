using BGOverlay;
using System;
using System.Text.RegularExpressions;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace WPFFrontend
{
    /// <summary>
    /// Interaction logic for OptionsControl.xaml
    /// </summary>
    public partial class OptionsControl : UserControl
    {
        private bool initializedOnce = false;
        private bool hidden = true;

        public OptionsControl()
        {
            InitializeComponent();

            this.HidePartyMembers.Click += updateConfig;
            this.HideNeutrals.Click += updateConfig;
            this.HideAllies.Click += updateConfig;
            this.EnableBorderlessMode.Click += updateConfig;
            this.RefreshRate.TextChanged += updateConfig;
            this.BigBuffIcons.Click += updateConfig;
            this.MouseUp += OptionsControl_MouseUp;
            this.CloseBtn.MouseUp += Label_MouseDown;
        }

        private void OptionsControl_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e != null && e.ChangedButton != MouseButton.Right)
                return;
            this.hidden = true;
            ThicknessAnimation anim = new ThicknessAnimation();
            anim.From = this.Margin;
            var newMargin = this.Margin;
            newMargin.Top = -this.ActualHeight;
            anim.To = newMargin;
            anim.EasingFunction = new BackEase() { Amplitude = .3, EasingMode = EasingMode.EaseIn };
            anim.Duration = TimeSpan.FromSeconds(.45);
            //anim.FillBehavior = FillBehavior.HoldEnd;
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
            Configuration.HideNeutrals = (bool)this.HideNeutrals.IsChecked;
            Configuration.HideAllies = (bool)this.HideAllies.IsChecked;
            Configuration.Borderless = (bool)this.EnableBorderlessMode.IsChecked;
            Configuration.RefreshTimeMS = int.Parse(this.RefreshRate.Text);
            Configuration.BigBuffIcons = (bool)this.BigBuffIcons.IsChecked;
        }

        public void Init()
        {
            if (this.initializedOnce)
                return;
            this.HidePartyMembers.IsChecked = Configuration.HidePartyMembers;
            this.HideNeutrals.IsChecked = Configuration.HideNeutrals;
            this.HideNeutrals.IsChecked = Configuration.HideAllies;
            this.EnableBorderlessMode.IsChecked = Configuration.Borderless;
            this.RefreshRate.Text = Configuration.RefreshTimeMS.ToString();
            this.BigBuffIcons.IsChecked = Configuration.BigBuffIcons;
        }

        public void Show()
        {
            if (this.hidden == false)
                return;
            this.Visibility = System.Windows.Visibility.Visible;
            this.hidden = false;
            ThicknessAnimation anim = new ThicknessAnimation();
            var margin1 = this.Margin;
            margin1.Top /= 2;
            anim.From = margin1;
            var newMargin = this.Margin;
            newMargin.Top = 0;
            anim.To = newMargin;
            anim.EasingFunction = new PowerEase() { Power = 10, EasingMode = EasingMode.EaseOut };
            anim.Duration = TimeSpan.FromSeconds(.85);
            anim.FillBehavior = FillBehavior.HoldEnd;
            this.BeginAnimation(UserControl.MarginProperty, anim, HandoffBehavior.SnapshotAndReplace);
        }

        private void Label_MouseDown(object sender, MouseButtonEventArgs e)
        {
            OptionsControl_MouseUp(null, null);
        }
    }
}
