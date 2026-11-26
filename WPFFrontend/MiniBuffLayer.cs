using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BGOverlay;
using Bitmap = System.Drawing.Bitmap;
using static WinApiBindings.WinAPIBindings;

namespace WPFFrontend;

public sealed class MiniBuffLayer : FrameworkElement
{
    private readonly ActorProjection projection = new();
    private readonly DrawingGroup frame = new();
    private sealed record BuffIcon(Rect Bounds, string Name, uint Expires);
    private sealed record Bar(DrawingGroup Drawing, BuffIcon[] Icons);
    private readonly ConditionalWeakTable<BGEntity, Bar> bars = new();
    private readonly ConditionalWeakTable<Bitmap, BitmapSource> icons = new();
    private readonly Dictionary<int, (string Name, Bitmap Icon)> castingSpells = new();
    private long nextSpellScan;
    private readonly BitmapToImageSourceConverter converter = new();
    private readonly BitmapSource missingIcon = new BitmapImage(new Uri("pack://application:,,,/icons/not_found.png"));
    internal Drawing Frame => frame;
    internal bool IsIconHovered { get; private set; }

    internal void Paint(Rect viewport, IReadOnlyList<BGEntity> actors, bool showBuffs, Point? pointer, double scale)
    {
        // Poll the simulation for spell changes separately from positioning the visible labels.
        long now = Environment.TickCount64;
        if (Configuration.ShowCastingSpell && now >= nextSpellScan)
        {
            nextSpellScan = now + 32;
            castingSpells.Clear();
            foreach (var entity in actors)
            {
                const byte hostileCutoff = 200; // EVILCUTOFF in EA.IDS.
                if (Configuration.HideEnemyCastingSpells && entity.EnemyAlly >= hostileCutoff) continue;
                if (entity.ReadCastingSpell(entity.GetCurrentSpriteAddress()) is { } spell)
                    castingSpells[entity.Id] = spell;
            }
        }
        if (!Configuration.ShowCastingSpell) castingSpells.Clear();
        if (!showBuffs && castingSpells.Count == 0)
        {
            IsIconHovered = false;
            if (frame.Children.Count > 0) frame.Children.Clear();
            return;
        }
        // Keep the actor anchors, glyphs and pointer in a single coordinate space.
        var toCanvas = new ScaleTransform(scale, scale);
        viewport = toCanvas.Inverse.TransformBounds(viewport);
        if (pointer.HasValue) pointer = toCanvas.Inverse.Transform(pointer.Value);
        double textDpi = VisualTreeHelper.GetDpi(this).PixelsPerDip * scale;
        using var dc = frame.Open();
        dc.PushTransform(toCanvas);
        dc.PushClip(new RectangleGeometry(viewport));
        BuffIcon hovered = null;
        BGEntity hoveredActor = null;
        foreach (var entity in actors)
        {
            bool casting = castingSpells.TryGetValue(entity.Id, out var spell);
            if (!showBuffs && !casting) continue;
            var actor = entity.GetCurrentSpriteAddress();
            if (!projection.TryLocate(actor, out int x, out int y, out int width, out int height)
                || x < 0 || x >= width || y < 0 || y >= height) continue;
            // CGameObject::m_canBeSeen tracks visibility from the party.
            if (ReadInt16(actor + 0x4C) <= 0 || (entity.EnemyAlly != 2 && ReadByte(actor + 0x3F73) != 0)) continue;

            // Each radar scan publishes fresh entity snapshots. Resolve their effects once,
            // while position and zoom continue to follow the current game frame.
            var bar = showBuffs ? bars.GetValue(entity, BuildBar) : null;
            if (bar?.Icons.Length == 0) bar = null;
            if (bar == null && !casting) continue;
            var animation = ReadPointer(actor + 0x3C40);
            int overhead = animation == IntPtr.Zero ? 0 : ReadInt32(animation + 0x1C);
            if (overhead <= 0) overhead = 44;
            double sx = viewport.Width / width, sy = viewport.Height / height;
            var origin = new Vector(viewport.Left + x * sx, viewport.Top + (y - overhead) * sy - 16);
            if (casting)
            {
                // Buff rows grow upwards. Keep the spell above the top row.
                var bottom = origin.Y + (bar?.Drawing.Bounds.Top ?? 0);
                DrawCast(dc, viewport, spell.Name, spell.Icon, origin.X, bottom, textDpi);
            }
            if (bar == null) continue;
            dc.PushTransform(new TranslateTransform(origin.X, origin.Y));
            dc.DrawDrawing(bar.Drawing);
            dc.Pop();
            if (pointer is Point mouse && bar.Drawing.Bounds.Contains(mouse - origin))
            {
                foreach (var icon in bar.Icons)
                    if (icon.Bounds.Contains(mouse - origin))
                    {
                        // Later bars are drawn on top of earlier ones.
                        hovered = icon;
                        hoveredActor = entity;
                        break;
                    }
            }
        }
        IsIconHovered = hovered != null;
        if (hovered != null)
            DrawTooltip(dc, viewport, pointer.Value,
                BuffControl.TooltipText(hovered.Name, hovered.Expires, hoveredActor.ReadGameTime()),
                textDpi, 1, abovePointer: true);
        dc.Pop();
        dc.Pop();
    }

    private Bar BuildBar(BGEntity entity)
    {
        try
        {
            entity.loadTimedEffects();
            var effects = entity.SpellProtection.Where(e => e.Expires == 0 || e.Expires >= entity.GameTime).ToArray();
            return ArrangeIcons(effects);
        }
        catch (Exception ex)
        {
            Logger.Error($"Could not read overhead buffs for actor {entity.Id}", ex);
            return new Bar(new DrawingGroup(), Array.Empty<BuffIcon>());
        }
    }

    private Bar ArrangeIcons((string Key, string Name, Bitmap Icon, uint Expires)[] effects)
    {
        const int columns = 5, size = 18, gap = 2;
        int rows = (effects.Length + columns - 1) / columns;
        var entries = new BuffIcon[effects.Length];
        var bar = new DrawingGroup();
        using (var dc = bar.Open())
        {
            for (int i = 0; i < effects.Length; i++)
            {
                int row = i / columns;
                int count = Math.Min(columns, effects.Length - row * columns);
                double left = -(count * (size + gap) - gap) / 2.0 + (i % columns) * (size + gap);
                double top = -(rows - row) * (size + gap) + gap;
                var bounds = new Rect(left, top, size, size);
                var (_, name, bitmap, expires) = effects[i];
                entries[i] = new BuffIcon(bounds, name, expires);
                var image = bitmap == null ? missingIcon : icons.GetValue(bitmap,
                    b => converter.BuffIcon(b, missingIcon));
                dc.DrawRoundedRectangle(Brushes.Wheat, null, bounds, 2, 2);
                DrawIcon(dc, image, left + 1, top + 1, size - 2);
            }
        }
        bar.Freeze();
        return new Bar(bar, entries);
    }

    private static readonly Typeface tooltipFont = new("Segoe UI");

    private static void DrawIcon(DrawingContext dc, BitmapSource image, double left, double top, double size)
    {
        double scale = size / Math.Max(image.PixelWidth, image.PixelHeight);
        double width = image.PixelWidth * scale, height = image.PixelHeight * scale;
        dc.DrawImage(image, new Rect(left + (size - width) / 2, top + (size - height) / 2, width, height));
    }

    private void DrawCast(DrawingContext dc, Rect viewport, string spell, Bitmap icon, double x, double bottom, double textDpi)
    {
        const double pad = 4, iconSize = 18, gap = 4;
        double iconSpace = icon == null ? 0 : iconSize + gap;
        var text = new FormattedText(spell, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            tooltipFont, 14, Brushes.PaleGoldenrod, textDpi)
        {
            MaxTextWidth = Math.Clamp(viewport.Width - 2 * pad - iconSpace, 1, 280),
            MaxLineCount = 2,
            Trimming = TextTrimming.CharacterEllipsis
        };
        double width = text.Width + 2 * pad + iconSpace;
        double height = Math.Max(text.Height, icon == null ? 0 : iconSize) + pad;
        double left = Math.Max(viewport.Left, Math.Min(x - width / 2, viewport.Right - width));
        double top = Math.Max(viewport.Top, bottom - height - 2);
        dc.DrawRoundedRectangle(Brushes.Black, null, new Rect(left, top, width, height), 3, 3);
        if (icon != null)
        {
            var image = icons.GetValue(icon,
                b => (BitmapSource)converter.Convert(b, typeof(BitmapSource), null, null));
            DrawIcon(dc, image, left + pad, top + (height - iconSize) / 2, iconSize);
        }
        dc.DrawText(text, new Point(left + pad + iconSpace, top + (height - text.Height) / 2));
    }

    internal static void DrawTooltip(DrawingContext dc, Rect viewport, Point mouse, string content,
        double pixelsPerDip, double scale, bool abovePointer = false)
    {
        double padding = 6 * scale, offset = 14 * scale;
        var text = new FormattedText(content, CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight, tooltipFont, 13 * scale, Brushes.White, pixelsPerDip)
        {
            MaxTextWidth = Math.Clamp(viewport.Width - 2 * padding, 1, 300 * scale)
        };
        double width = text.Width + 2 * padding, height = text.Height + 2 * padding;
        double left = Math.Max(viewport.Left, Math.Min(mouse.X + offset, viewport.Right - width));
        double top = mouse.Y + offset + height <= viewport.Bottom ? mouse.Y + offset
            : Math.Max(viewport.Top, mouse.Y - offset - height);
        if (abovePointer)
        {
            double above = mouse.Y - offset - height;
            if (above >= viewport.Top)
                top = above;
            else
                top = Math.Min(mouse.Y + 48 * scale, Math.Max(viewport.Top, viewport.Bottom - height));
        }
        dc.DrawRoundedRectangle(Brushes.Black, null, new Rect(left, top, width, height), 3 * scale, 3 * scale);
        dc.DrawText(text, new Point(left + padding, top + padding));
    }

    internal void Clear()
    {
        nextSpellScan = 0;
        castingSpells.Clear();
        IsIconHovered = false;
        if (frame.Children.Count > 0) frame.Children.Clear();
    }

    protected override void OnRender(DrawingContext dc) => dc.DrawDrawing(frame);
}
