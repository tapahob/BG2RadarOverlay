using BGOverlay;
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
                if (value == float.MaxValue) 
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
            if (Configuration.BigBuffIcons && Label1.FontSize == 12)
            {
                Label1.FontSize = 16;
                Label1.Margin = new System.Windows.Thickness(1, 25, 0, 0);

                Label2.FontSize = 16;
                Label2.Margin = new System.Windows.Thickness(0, 25, 0, 0);

                this.Width   = 40;
                this.Height  = 40;
                Image.Width  = 40;
                Image.Height = 40;
            }
            if (!Configuration.BigBuffIcons && Label1.FontSize == 16)
            {
                Label1.FontSize = 12;
                Label1.Margin = new System.Windows.Thickness(1, 14, 0, 0);

                Label2.FontSize = 12;
                Label2.Margin = new System.Windows.Thickness(0, 13, 0, 0);

                this.Width   = 24;
                this.Height  = 24;
                Image.Width  = 24;
                Image.Height = 24;
            }
        }
    }
}
