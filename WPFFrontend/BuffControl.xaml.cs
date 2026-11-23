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
        public string BuffName { get; set; }
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
        public uint BuffDurationAbsolute { get; set; }

        internal void UpdateTooltip(uint gameTime)
        {
            var tooltip = (ToolTip)ToolTip;
            string text = TooltipText(BuffName, BuffDurationAbsolute, gameTime);
            if (!Equals(tooltip.Content, text)) tooltip.Content = text;
        }

        internal static string TooltipText(string name, uint expires, uint now)
        {
            // The game clock advances 15 ticks per second and stops while paused.
            long seconds = (Math.Max(0L, (long)expires - now) + 14) / 15;
            string remaining = expires is 0 or uint.MaxValue ? "\u221e" : $"{seconds / 60}:{seconds % 60:00}";
            return name + "\n" + remaining;
        }
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

            double Luminance(int i) =>
                0.2126 * pixels[i + 2] + 0.7152 * pixels[i + 1] + 0.0722 * pixels[i];

            // Sample the body of the icon, excluding transparent padding and faint edges.
            // For entirely translucent artwork, use its most opaque pixels instead.
            int maxAlpha = 0;
            for (int i = 3; i < pixels.Length; i += 4)
                maxAlpha = Math.Max(maxAlpha, (int)pixels[i]);
            int sampleAlpha = Math.Min(128, maxAlpha);
            var samples = new double[pixels.Length / 4];
            int count = 0;
            for (int i = 0; i < pixels.Length; i += 4)
                if (pixels[i + 3] >= sampleAlpha)
                    samples[count++] = Luminance(i);

            Array.Sort(samples, 0, count);
            double Percentile(double fraction) => samples[(int)Math.Round((count - 1) * fraction)];
            double midtone = Percentile(0.55);
            double highlightRange = Math.Max(12, Percentile(0.98) - midtone);

            for (int i = 0; i < pixels.Length; i += 4)
            {
                double luminance = Luminance(i);
                double highlight = Math.Clamp((luminance - midtone) / highlightRange, 0, 1);
                double baseTone = luminance / 255;
                byte inkTone = (byte)Math.Round(6 * (baseTone * baseTone) + 130 * Math.Pow(highlight, 1.7));
                pixels[i] = pixels[i + 1] = pixels[i + 2] = inkTone;
            }

            var ink = BitmapSource.Create(bitmap.PixelWidth, bitmap.PixelHeight, source.DpiX, source.DpiY,
                PixelFormats.Bgra32, null, pixels, stride);
            ink.Freeze();
            return ink;
        }

        public BuffControl()
        {
            InitializeComponent();
            ToolTip = new ToolTip
            {
                Placement = System.Windows.Controls.Primitives.PlacementMode.Top,
                VerticalOffset = -6
            };
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
