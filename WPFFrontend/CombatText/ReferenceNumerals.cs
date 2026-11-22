using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WPFFrontend.CombatText;

internal static class ReferenceNumerals
{
    // Each atlas row contains the minus sign followed by the ten digits.
    // Padding carries the glow and the shadow outside the visible ink.
    const int CellWidth = 168, CellHeight = 220, Padding = 25;
    sealed record Metrics(int Row, int Top, double Center, int Gap, int[] Widths, int[] AfterMinus);
    sealed record Row(Metrics Layout, BitmapSource[] Glyphs, BitmapSource Backdrop);

    static readonly Lazy<Dictionary<DamageKind, Row>> Rows = new(LoadRows);
    static readonly Lazy<BitmapSource> Miss = new(() => FloatingText.LoadFrame("Miss.png"));

    static BitmapSource Crop(BitmapSource sheet, Int32Rect bounds)
    {
        var crop = new CroppedBitmap(sheet, bounds);
        crop.Freeze();
        return crop;
    }

    static Dictionary<DamageKind, Row> LoadRows()
    {
        var sheet = FloatingText.LoadFrame("ReferenceDigits.png");
        var backdrops = FloatingText.LoadFrame("ReferenceBackdrops.png");
        using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/BG Radar Overlay;component/CombatText/Art/ReferenceDigits.json")).Stream;
        var layouts = JsonSerializer.Deserialize<Dictionary<DamageKind, Metrics>>(stream)!;
        return layouts.ToDictionary(pair => pair.Key, pair =>
        {
            var layout = pair.Value;
            var glyphs = layout.Widths.Select((width, index) => Crop(sheet, new Int32Rect(
                index * CellWidth, layout.Row * CellHeight, width + 2 * Padding, CellHeight))).ToArray();
            var backdrop = pair.Key == DamageKind.Normal ? null :
                Crop(backdrops, FloatingText.Styles[(int)pair.Key].Crop);
            return new Row(layout, glyphs, backdrop);
        });
    }

    internal static void Draw(DrawingContext dc, DamageKind kind, int damage)
    {
        var style = FloatingText.Styles[(int)kind];
        double scale = style.Width / style.Crop.Width;
        double height = style.Crop.Height * scale;
        var bounds = new Rect(-style.Width / 2, -height / 2, style.Width, height);
        if (kind == DamageKind.Miss)
        {
            dc.DrawImage(Miss.Value, bounds);
            return;
        }

        var row = Rows.Value[kind];
        if (row.Backdrop != null)
            dc.DrawImage(row.Backdrop, bounds);

        var layout = row.Layout;
        string digits = Math.Abs((long)damage).ToString(CultureInfo.InvariantCulture);
        int minusGap = layout.AfterMinus[digits[0] - '0' + 1];
        double width = layout.Widths[0] + minusGap + (digits.Length - 1) * layout.Gap;
        foreach (char digit in digits) width += layout.Widths[digit - '0' + 1];

        // Center the ink, not the transparent margins of the sprites.
        double x = layout.Center - width / 2;
        void DrawGlyph(int index)
        {
            var glyph = row.Glyphs[index];
            dc.DrawImage(glyph, new Rect(bounds.X + (x - Padding) * scale, bounds.Y + layout.Top * scale,
                glyph.PixelWidth * scale, glyph.PixelHeight * scale));
        }
        DrawGlyph(0);
        x += layout.Widths[0] + minusGap;
        foreach (char digit in digits)
        {
            int index = digit - '0' + 1;
            DrawGlyph(index);
            x += layout.Widths[index] + layout.Gap;
        }
    }
}
