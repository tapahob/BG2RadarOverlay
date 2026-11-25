using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Box = Vortice.Mathematics.Box;

namespace WPFFrontend;

// One composition target on the radar's own HWND. DWM samples the offsets at
// display refresh even if neither the game nor WPF submits another frame.
internal sealed class CardExitComposition : IDisposable
{
    private readonly ID3D11Device gpu;
    private readonly IDCompositionDevice device;
    private readonly IDCompositionTarget target;
    private readonly IDCompositionVisual root;
    private readonly Queue<(IDCompositionVisual Visual, long Expires)> active = new();
    private readonly DispatcherTimer cleanup = new();

    internal CardExitComposition(IntPtr window)
    {
        try
        {
            gpu = D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport);
            using var dxgi = gpu.QueryInterface<IDXGIDevice>();
            device = DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgi);
            device.CreateTargetForHwnd(window, true, out target).CheckError();
            root = device.CreateVisual();
            target.SetRoot(root).CheckError();
            cleanup.Tick += RemoveFinished;
        }
        catch { Dispose(); throw; }
    }

    internal void Start(FrameworkElement card, Rect pixels, BackEase ease, TimeSpan duration)
    {
        int w = checked((int)Math.Ceiling(pixels.Width)), h = checked((int)Math.Ceiling(pixels.Height));
        // Upload the card once, at its on-screen resolution and with its alpha intact.
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen())
            dc.DrawRectangle(new VisualBrush(card) { Stretch = Stretch.Fill }, null, new Rect(0, 0, w, h));
        var image = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        image.Render(drawing);
        var data = new byte[checked(w * h * 4)];
        image.CopyPixels(data, w * 4, 0);
        device.CreateSurface((uint)w, (uint)h, Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied, out var surface).CheckError();
        using (surface)
        {
            using (var texture = surface.BeginDraw<ID3D11Texture2D>(null, out var origin))
            {
                var box = new Box(origin.X, origin.Y, 0, origin.X + w, origin.Y + h, 1);
                gpu.ImmediateContext.UpdateSubresource(data, texture, 0, (uint)(w * 4), 0, box);
            }
            surface.EndDraw().CheckError();
            using var motion = device.CreateAnimation();
            // These linear segments describe the original WPF easing curve.
            // DWM interpolates between them on each refresh of the target display.
            const int segments = 128;
            double step = duration.TotalSeconds / segments;
            for (int i = 0; i < segments; i++)
            {
                double y = pixels.Top - pixels.Bottom * ease.Ease((double)i / segments);
                double next = pixels.Top - pixels.Bottom * ease.Ease((i + 1.0) / segments);
                motion.AddCubic(i * step, (float)y, (float)((next - y) / step), 0, 0).CheckError();
            }
            motion.End(duration.TotalSeconds, -h).CheckError();
            var visual = device.CreateVisual();
            try
            {
                visual.SetContent(surface).CheckError();
                visual.SetOffsetX((float)pixels.Left).CheckError();
                visual.SetOffsetY(motion).CheckError();
                root.AddVisual(visual, true, null).CheckError();
                device.Commit().CheckError();
            }
            catch { root.RemoveVisual(visual); visual.Dispose(); throw; }
            // By cleanup time the final offset has already taken it out of view.
            active.Enqueue((visual, Environment.TickCount64 + (long)duration.TotalMilliseconds + 100));
            if (!cleanup.IsEnabled)
            {
                cleanup.Interval = duration + TimeSpan.FromMilliseconds(100);
                cleanup.Start();
            }
        }
    }

    private void RemoveFinished(object sender, EventArgs args)
    {
        cleanup.Stop();
        long now = Environment.TickCount64;
        while (active.TryPeek(out var item) && item.Expires <= now)
        {
            root.RemoveVisual(item.Visual);
            active.Dequeue().Visual.Dispose();
        }
        device.Commit();
        if (active.TryPeek(out var next))
        {
            cleanup.Interval = TimeSpan.FromMilliseconds(Math.Max(1, next.Expires - now));
            cleanup.Start();
        }
    }

    public void Dispose()
    {
        cleanup.Stop();
        while (active.TryDequeue(out var item)) item.Visual.Dispose();
        root?.Dispose();
        target?.Dispose();
        device?.Dispose();
        gpu?.Dispose();
    }
}
