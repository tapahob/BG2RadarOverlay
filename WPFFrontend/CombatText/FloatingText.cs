using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BGOverlay;

namespace WPFFrontend.CombatText;

// Order is shared with NativeRadar/CombatEvents.cpp.
internal enum DamageKind { Normal, Miss, Critical, Fire, Cold, Acid, Lightning, Magic }

internal static class FloatingText
{
    internal const double Lifetime = .8;
    internal record Style(double Rise, string Ink, string Light,
        Int32Rect Crop, double Width);

    internal static readonly Style[] Styles =
    {
        // The parchment crops have different margins. Size each for ~42 DIPs of visible ink, like the elemental digits.
        new(36, "#E8D9B5", "#FFF0CE", new(82, 237, 205, 153), 100),
        new(28, "#F3E6C8", "#FFF7DC", new(393, 235, 289, 148), 166),
        new(44, "#D00000", "#FFD966", new(690, 114, 420, 407), 123),
        new(40, "#FF6A00", "#FFD16A", new(1116, 143, 327, 330), 93),
        new(32, "#86C5FF", "#DDF8FF", new(15, 612, 328, 337), 96),
        new(28, "#7EE000", "#D7FF57", new(378, 608, 287, 337), 85),
        new(38, "#FFF4B0", "#E7D6FF", new(701, 590, 378, 372), 109),
        new(42, "#B56BFF", "#F2C6FF", new(1086, 587, 346, 361), 100)
    };

    internal static BitmapSource LoadFrame(string path)
    {
        var uri = new Uri("pack://application:,,,/BG Radar Overlay;component/CombatText/Art/" + path);
        var frame = BitmapDecoder.Create(uri, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        frame.Freeze();
        return frame;
    }

    static SolidColorBrush BrushOf(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    internal sealed class Effect
    {
        readonly DamageKind kind;
        readonly Style style;
        readonly DrawingGroup label = new();
        readonly SolidColorBrush light, ink;
        readonly Pen hairline;
        readonly (double Angle, double Phase, double Size)[] particles;
        readonly List<double> pushes = new();
        readonly bool FlyLeft;
        double UserScale => Configuration.CombatTextScale / 100.0;
        double PushStrength => Configuration.CombatTextPush / 100.0;
        internal int Target { get; }
        internal Point Anchor { get; }
        internal double Start { get; }

        internal Effect(int target, Point anchor, DamageKind kind, int damage, double start, bool flyLeft)
        {
            Target = target;
            Anchor = anchor;
            Start = start;
            FlyLeft = flyLeft;
            this.kind = kind;
            style = Styles[(int)kind];
            using (var dc = label.Open()) ReferenceNumerals.Draw(dc, kind, damage);
            label.Freeze();
            light = BrushOf(style.Light);
            ink = BrushOf(style.Ink);
            hairline = new Pen(light, .85);
            hairline.Freeze();
            var random = new Random(719 + (int)kind);
            particles = Enumerable.Range(0, 16).Select(i => (
                (i + random.NextDouble()) * Math.Tau / 16, random.NextDouble(), .6 + random.NextDouble())).ToArray();
        }

        internal void Push(double now)
        {
            if (PushStrength > 0 && now - Start < Lifetime && pushes.Count < 32)
                pushes.Add(now - Start);
        }

        static double Ease(double t, CombatEasing curve) => curve switch
        {
            CombatEasing.CubicOut => 1 - Math.Pow(1 - t, 3),
            CombatEasing.SineOut => Math.Sin(t * Math.PI / 2),
            CombatEasing.CubicInOut => t < .5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2,
            CombatEasing.QuadIn => t * t,
            CombatEasing.QuartOut => 1 - Math.Pow(1 - t, 4),
            CombatEasing.ExpoOut => (1 - Math.Exp(-7 * t)) / (1 - Math.Exp(-7)),
            CombatEasing.CircOut => Math.Sqrt(t * (2 - t)),
            CombatEasing.BackOut => 1 + 2.25 * Math.Pow(t - 1, 3) + 1.25 * Math.Pow(t - 1, 2),
            CombatEasing.SpringOut => 1 - Math.Pow(1 - t, 3) * Math.Cos(3 * Math.PI * t),
            CombatEasing.SmoothStep => t * t * t * (t * (6 * t - 15) + 10),
            CombatEasing.Linear => t,
            _ => 1 - (1 - t) * (1 - t)
        };

        internal Vector OffsetAt(double age)
        {
            double progress = Ease(Math.Clamp(age / Lifetime, 0, 1), Configuration.CombatTextEasing);
            foreach (double push in pushes)
            {
                // Each impact advances the older label along the same flight path over 140 ms.
                double impulse = Math.Clamp((age - push) / .14, 0, 1);
                progress += .95 * UserScale * PushStrength * Ease(impulse, CombatEasing.CubicOut);
            }
            double x = 76 * progress;
            double rise = style.Rise;
            double y = -rise * (1 - Math.Exp(-2 * progress)) - 6 * progress;
            return Configuration.CombatTextPath switch
            {
                CombatTrajectory.Parabola => new Vector(x, progress <= 1
                    ? -rise * (4 * progress - 3 * progress * progress)
                    : rise * (-1 + 2 * (progress - 1))),
                CombatTrajectory.Up => new Vector(0, -rise * progress),
                CombatTrajectory.ArcLeft => new Vector(-x, y),
                CombatTrajectory.Diagonal => new Vector(.8 * x, -47 * progress),
                CombatTrajectory.Wave => new Vector(19 * Math.Sin(progress * Math.Tau), -72 * progress),
                CombatTrajectory.Fan => new Vector((FlyLeft ? -1 : 1) * x, y),
                _ => new Vector(x, y)
            };
        }

        internal void Draw(DrawingContext dc, double age, Rect viewport)
        {
            double t = Math.Clamp(age / Lifetime, 0, 1);
            if (t <= 0 || t >= 1) return;
            var offset = OffsetAt(age);
            double fade = Math.Min(1, t / .08) * Math.Clamp((1 - t) / .47, 0, 1);
            double growth = kind is DamageKind.Normal or DamageKind.Miss ? .12 : .15;
            double scale = UserScale * (1 + growth * Ease(t, Configuration.CombatTextEasing));
            dc.PushOpacity(fade);
            dc.PushTransform(new TranslateTransform(viewport.X + Anchor.X * viewport.Width + offset.X,
                viewport.Y + Anchor.Y * viewport.Height + offset.Y));
            dc.PushTransform(new ScaleTransform(scale, scale));
            DrawParticles(dc, t);
            dc.DrawDrawing(label);
            dc.Pop();
            dc.Pop();
            dc.Pop();
        }

        void DrawParticles(DrawingContext dc, double t)
        {
            if (kind is DamageKind.Normal or DamageKind.Miss) return;
            for (int i = 0; i < particles.Length; i++)
            {
                var (angle, phase, size) = particles[i];
                double c = Math.Cos(angle), s = Math.Sin(angle);
                double x = c * (24 + 16 * phase), y = s * (17 + 9 * phase);
                double drift = t * (16 + 16 * phase);
                switch (kind)
                {
                    case DamageKind.Critical:
                        double burst = Math.Sin(Math.PI * Math.Min(1, t / .52));
                        dc.DrawLine(new Pen(i % 3 == 0 ? light : ink, burst * 1.6),
                            new Point(x * 1.05, y * 1.05), new Point(x + c * drift * 2, y + s * drift * 2));
                        break;
                    case DamageKind.Fire:
                        x += 7 * Math.Sin(angle + 5 * t) + drift * .22;
                        y -= drift;
                        dc.DrawLine(hairline, new Point(x - 1, y + size * 2), new Point(x, y));
                        dc.DrawEllipse(i % 3 == 0 ? light : ink, null, new Point(x, y), size, size * .75);
                        break;
                    case DamageKind.Cold:
                        x += c * drift * .7;
                        y += drift * t;
                        dc.DrawLine(hairline, new Point(x, y), new Point(x + size * 2, y - size * 4));
                        if (i % 3 == 0) Star(dc, x, y, size * (2 + Math.Sin(10 * t + angle)));
                        break;
                    case DamageKind.Acid:
                        x += c * drift * .4;
                        y += s < 0 ? -drift * .5 : drift * t;
                        var radius = size * (1 + t);
                        dc.DrawEllipse(ink, hairline, new Point(x, y), radius, radius * (s > 0 ? 1.5 : 1));
                        break;
                    case DamageKind.Lightning:
                        if (i % 3 != 0) break;
                        double flash = .5 + .5 * Math.Cos(26 * t + angle);
                        dc.PushOpacity(flash);
                        var kink = new Point(x + 6 * s * Math.Sin(16 * t + angle), y);
                        dc.DrawLine(hairline, new Point(c * 24, s * 16), kink);
                        var end = new Point(c * (43 + drift * .5), s * (36 + phase * 9));
                        dc.DrawLine(hairline, kink, end);
                        dc.DrawLine(hairline, kink, new Point(end.X - 7 * s, end.Y + 9 * c));
                        dc.Pop();
                        break;
                    case DamageKind.Magic:
                        double arc = angle + .75 * t;
                        x = 43 * Math.Cos(arc);
                        y = 39 * Math.Sin(arc);
                        Star(dc, x, y, size * (1.5 + Math.Sin(angle + t * 8)));
                        break;
                }
            }
        }

        void Star(DrawingContext dc, double x, double y, double r)
        {
            dc.DrawLine(hairline, new Point(x - r, y), new Point(x + r, y));
            dc.DrawLine(hairline, new Point(x, y - r * 1.4), new Point(x, y + r * 1.4));
        }
    }

}
