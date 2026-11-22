using BGOverlay;
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
                var str = value.ToString("0.0", new CultureInfo("en-US"));
                if (value == float.MaxValue || value < 0) 
                    str = "∞";
                this.Label1.Content = str; this.Label2.Content = str; 
            } 
        }
        public float BuffDurationAbsolute { get; set; }
        public BitmapSource Icon
        {
            get { return _icon; }
            set
            {
                _icon = value;
                _inkIcon = null;
                UpdateIcon();
            }
        }

        private BitmapSource _icon;
        private BitmapSource _inkIcon;

        private void UpdateIcon()
        {
            Image.Source = Configuration.BuffInkFilter && _icon != null
                ? _inkIcon ??= CreateInkIcon(_icon)
                : _icon;
        }

        internal static BitmapSource CreateInkIcon(BitmapSource source)
        {
            var bitmap = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            int stride = bitmap.PixelWidth * 4;
            var pixels = new byte[stride * bitmap.PixelHeight];
            bitmap.CopyPixels(pixels, stride, 0);

            // Match the preview's grayscale, 55% sepia, 53% brightness and 94% contrast.
            // Work in straight BGRA so antialiased edges retain their original opacity.
            static byte InkChannel(double luminance, double sepiaGain) =>
                (byte)Math.Round(Math.Min(255, luminance * sepiaGain) * 0.53 * 0.94 + 255 * 0.03);

            for (int i = 0; i < pixels.Length; i += 4)
            {
                double luminance = 0.2126 * pixels[i + 2] + 0.7152 * pixels[i + 1] + 0.0722 * pixels[i];
                pixels[i]     = InkChannel(luminance, 0.45 + 0.55 * 0.937);
                pixels[i + 1] = InkChannel(luminance, 0.45 + 0.55 * 1.203);
                pixels[i + 2] = InkChannel(luminance, 0.45 + 0.55 * 1.351);
            }

            var ink = BitmapSource.Create(bitmap.PixelWidth, bitmap.PixelHeight, source.DpiX, source.DpiY,
                PixelFormats.Bgra32, null, pixels, stride);
            ink.Freeze();
            return ink;
        }

        public BuffControl()
        {
            InitializeComponent();
            Image.ToolTip = BuffName;
            Invalidate();
        }

        public void Invalidate()
        {
            UpdateIcon();
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
