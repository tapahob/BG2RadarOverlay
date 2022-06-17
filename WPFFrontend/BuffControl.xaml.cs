using BGOverlay;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace WPFFrontend
{
    /// <summary>
    /// Interaction logic for BuffControl.xaml
    /// </summary>
    public partial class BuffControl : UserControl
    {
        public string BuffName 
        { 
            get { return ""; }
            set { Image.ToolTip = value; } 
        }
        public float BuffDuration 
        { 
            get { return 0; } 
            set 
            {
                var str = value.ToString();
                if (value == float.MaxValue || value < 0) 
                    str = "∞";
                this.Label1.Content = str; this.Label2.Content = str; 
            } 
        }
        public float BuffDurationAbsolute { get; set; }
        public BitmapSource Icon 
        {
            get { return _icon; }
            set { this.Image.Source = value; } 
        }

        private BitmapSource _icon;

        public BuffControl()
        {
            InitializeComponent();
            Image.ToolTip = BuffName;
            Invalidate();
        }

        public void Invalidate()
        {
            if (Configuration.BigBuffIcons && this.MaxWidth == 24)
            {
                Label1.Style    = Application.Current.Resources["BuffTimerBig"] as Style;
                Label2.Style    = Application.Current.Resources["BuffTimerBig"] as Style;
                Label1.Margin   = new System.Windows.Thickness(2, 25, 0, 0);
                Label2.Margin   = new System.Windows.Thickness(0, 23, 0, 0);
                this.MaxWidth   = 40;
                this.MaxHeight  = 40;                
            }
            if (!Configuration.BigBuffIcons && this.MaxWidth == 40)
            {
                Label1.Style    = Application.Current.Resources["BuffTimerSmall"] as Style;
                Label2.Style    = Application.Current.Resources["BuffTimerSmall"] as Style;
                Label1.Margin   = new System.Windows.Thickness(1, 14, 0, 0);
                Label2.Margin   = new System.Windows.Thickness(0, 13, 0, 0);
                this.MaxWidth   = 24;
                this.MaxHeight  = 24;                
            }
        }
    }
}
