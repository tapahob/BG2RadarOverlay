using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WPFFrontend
{
    /// <summary>
    /// Converts a System.Drawing.Bitmap (as returned by the game-resource readers, e.g. a
    /// spell's icon) into a WPF ImageSource for direct XAML binding - the manual
    /// CreateBitmapSourceFromHBitmap dance already happens a few times in EnemyControl.xaml.cs's
    /// code-behind for buff/weapon icons; this centralizes it for the per-spell icon list so it
    /// can bind straight from a SpellIconEntry.Icon without code-behind conversion.
    /// Returns null for a null Bitmap, which NullToVisibilityConverter uses to hide the icon
    /// slot entirely rather than drawing a placeholder.
    /// </summary>
    public class BitmapToImageSourceConverter : IValueConverter
    {
        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not System.Drawing.Bitmap bitmap)
                return null;

            var hBitmap = bitmap.GetHbitmap();
            try
            {
                var image = Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap,
                    IntPtr.Zero,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromWidthAndHeight(bitmap.Width, bitmap.Height));
                image.Freeze();
                return image;
            }
            finally
            {
                // GetHbitmap() allocates a new unmanaged GDI bitmap handle every call - it isn't
                // tracked/freed by CreateBitmapSourceFromHBitmap, so it has to be deleted here or
                // it leaks for the lifetime of the process.
                DeleteObject(hBitmap);
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// Visibility.Collapsed for a null value, Visibility.Visible otherwise - used to hide an
    /// icon <see cref="System.Windows.Controls.Image"/> entirely (rather than rendering an
    /// empty slot) when the bound resource has no icon.
    /// </summary>
    public class NullToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value == null ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// Visibility.Collapsed for true, Visibility.Visible for false - used to hide the trailing
    /// comma after the last spell in a SpellImmunityLine's icon/name list (SpellIconEntry.IsLast).
    /// </summary>
    public class InverseBooleanToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is bool b && b ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// Visibility.Collapsed for a null or empty string, Visibility.Visible otherwise - used to
    /// hide the Pockets FlagsText line entirely (rather than leaving a blank line still taking
    /// up its own row height) when there's no flag text to show.
    /// </summary>
    public class EmptyStringToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// A PocketItemEntry.Count of 2 or more becomes " x{count}" (e.g. " x3"); 1 or less becomes
    /// an empty string, so a single copy of an item shows no suffix at all. Not localized - "x"
    /// as a multiplication/quantity marker reads the same in every language this radar ships.
    /// </summary>
    public class PocketCountToSuffixConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is int count && count > 1 ? $" x{count}" : "";

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
