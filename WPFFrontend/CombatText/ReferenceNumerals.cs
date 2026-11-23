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

    static readonly Lazy<BitmapSource> Skeleton = new(() => FloatingText.LoadFrame("WeaponIneffectiveSkeleton.png"));

    sealed record BackstabArt(BitmapSource Backdrop, BitmapSource Sample, Point NumberCenter);
    static readonly Lazy<BackstabArt> Backstab = new(() => LoadBackstab("Backstab", new Point(388, 227)));
    static readonly Lazy<BackstabArt> BackstabCritical = new(() => LoadBackstab("BackstabCritical", new Point(459, 235)));

    static BackstabArt LoadBackstab(string name, Point center) => new(
        FloatingText.LoadFrame(name + ".png"), FloatingText.LoadFrame(name + "Sample.png"), center);

    static Point DrawBackstab(DrawingContext dc, DamageKind kind, bool sample)
    {
        var art = (kind == DamageKind.Backstab ? Backstab : BackstabCritical).Value;
        var image = sample ? art.Sample : art.Backdrop;
        double width = FloatingText.Styles[(int)kind].Width;
        double height = width * image.PixelHeight / image.PixelWidth;
        dc.DrawImage(image, new Rect(-width / 2, -height / 2, width, height));
        double scale = width / image.PixelWidth;
        return new Point(art.NumberCenter.X * scale - width / 2, art.NumberCenter.Y * scale - height / 2);
    }

    static BitmapSource Crop(BitmapSource sheet, Int32Rect bounds)
    {
        var crop = new CroppedBitmap(sheet, bounds);
        crop.Freeze();
        return crop;
    }

    static Dictionary<DamageKind, Row> LoadRows()
    {
        var sheet = FloatingText.LoadFrame("ReferenceDigits.png");
        var backstabDigits = FloatingText.LoadFrame("BackstabDigits.png");
        var backdrops = FloatingText.LoadFrame("ReferenceBackdrops.png");
        using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/BG Radar Overlay;component/CombatText/Art/ReferenceDigits.json")).Stream;
        var layouts = JsonSerializer.Deserialize<Dictionary<DamageKind, Metrics>>(stream)!;
        return layouts.ToDictionary(pair => pair.Key, pair =>
        {
            var layout = pair.Value;
            bool backstab = pair.Key is DamageKind.Backstab or DamageKind.BackstabCritical;
            var glyphs = layout.Widths.Select((width, index) => Crop(backstab ? backstabDigits : sheet, new Int32Rect(
                index * CellWidth, layout.Row * CellHeight, width + 2 * Padding, CellHeight))).ToArray();
            var backdrop = backstab || pair.Key == DamageKind.Normal ? null :
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
        if (kind is DamageKind.Miss or DamageKind.WeaponIneffective)
        {
            var image = kind == DamageKind.Miss ? Miss : Skeleton;
            dc.DrawImage(image.Value, bounds);
            return;
        }

        if (kind is DamageKind.Backstab or DamageKind.BackstabCritical)
        {
            bool sample = Math.Abs((long)damage) == (kind == DamageKind.Backstab ? 28 : 67);
            var center = DrawBackstab(dc, kind, sample);
            if (!sample)
            {
                // The two rows have 124 and 128 pixels of ink height in atlas coordinates.
                double glyphScale = scale * (kind == DamageKind.Backstab ? 107.0 / 124 : 120.0 / 128);
                DrawDigits(dc, Rows.Value[kind], damage, glyphScale, center.X, center.Y - 85 * glyphScale);
            }
            return;
        }

        var row = Rows.Value[kind];
        if (row.Backdrop != null)
            dc.DrawImage(row.Backdrop, bounds);

        DrawDigits(dc, row, damage, scale, bounds.X + row.Layout.Center * scale, bounds.Y + row.Layout.Top * scale);
    }

    static void DrawDigits(DrawingContext dc, Row row, int damage, double scale, double center, double top)
    {
        var layout = row.Layout;
        string digits = Math.Abs((long)damage).ToString(CultureInfo.InvariantCulture);
        int minusGap = layout.AfterMinus[digits[0] - '0' + 1];
        double width = layout.Widths[0] + minusGap + (digits.Length - 1) * layout.Gap;
        foreach (char digit in digits) width += layout.Widths[digit - '0' + 1];

        // Center the ink, not the transparent margins of the sprites.
        double x = -width / 2;
        void DrawGlyph(int index)
        {
            var glyph = row.Glyphs[index];
            dc.DrawImage(glyph, new Rect(center + (x - Padding) * scale, top,
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
