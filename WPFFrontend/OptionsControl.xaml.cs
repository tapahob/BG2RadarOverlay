using BGOverlay;
using System.Text.RegularExpressions;
using System.Windows.Controls;
using System.Windows.Input;

namespace WPFFrontend
{
    /// <summary>
    /// Interaction logic for OptionsControl.xaml
    /// </summary>
    public partial class OptionsControl : UserControl
    {
        private bool initializedOnce = false;

        public OptionsControl()
        {
            InitializeComponent();

            this.HidePartyMembers.Click += updateConfig;
            this.HideNeutrals.Click += updateConfig;
            this.HideAllies.Click += updateConfig;
            this.EnableBorderlessMode.Click += updateConfig;
            this.RefreshRate.TextChanged += updateConfig;
            this.BigBuffIcons.Click += updateConfig;
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
            this.Visibility = System.Windows.Visibility.Collapsed;
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

        private void Label_MouseDown(object sender, MouseButtonEventArgs e)
        {
            this.Visibility = System.Windows.Visibility.Collapsed;
            this.initializedOnce = false;
            this.Init();
        }
    }
}
