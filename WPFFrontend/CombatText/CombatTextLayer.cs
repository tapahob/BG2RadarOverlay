using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using BGOverlay;

namespace WPFFrontend.CombatText;

public sealed class CombatTextLayer : FrameworkElement
{
    readonly List<FloatingText.Effect> effects = new();
    readonly DrawingGroup frame = new();
    long lastTick;
    double time;
    bool flyLeft;
    internal bool HasEffects => effects.Count > 0;
    internal Drawing Frame => frame;

    public CombatTextLayer() => RenderOptions.SetBitmapScalingMode(frame, BitmapScalingMode.HighQuality);

    internal void Advance(long tick)
    {
        if (!Configuration.ShowCombatText)
        {
            if (effects.Count > 0) Clear();
            return;
        }
        if (lastTick != 0) time += (tick - lastTick) * Configuration.CombatTextSpeed / 100000.0;
        lastTick = tick;
        effects.RemoveAll(effect => time - effect.Start >= FloatingText.Lifetime);
    }

    internal void Add(CombatImpact impact)
    {
        if (!Configuration.ShowCombatText) return;
        foreach (var effect in effects)
            if (effect.Target == impact.Actor) effect.Push(time);
        if (effects.Count == 128) effects.RemoveAt(0);
        effects.Add(new FloatingText.Effect(impact.Actor, impact.Anchor, impact.Kind, impact.Amount, time, flyLeft));
        flyLeft = !flyLeft;
    }

    internal void Paint(Rect viewport)
    {
        if (effects.Count == 0 && frame.Children.Count == 0) return;
        using var dc = frame.Open();
        if (!viewport.IsEmpty && effects.Count > 0)
        {
            dc.PushClip(new RectangleGeometry(viewport));
            foreach (var effect in effects) effect.Draw(dc, time - effect.Start, viewport);
            dc.Pop();
        }
    }

    internal void Clear()
    {
        effects.Clear();
        frame.Children.Clear();
        time = 0;
        lastTick = 0;
    }

    protected override void OnRender(DrawingContext dc) => dc.DrawDrawing(frame);
}
