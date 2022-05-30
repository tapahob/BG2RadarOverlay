using System;
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
        public float BuffDuration { get { return 0; } set { this.Label1.Content = value; this.Label2.Content = value; } }
        public float BuffDurationAbsolute { get; set; }
        public Uri Icon 
        {
            get { return _icon; }
            set { this.Image.Source = new BitmapImage(value); } 
        }

        private Uri _icon;

        public BuffControl()
        {
            InitializeComponent(); 
            Image.ToolTip = BuffName;
        }
    }
}
