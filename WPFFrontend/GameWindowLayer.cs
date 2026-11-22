using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using WinApiBindings;

namespace WPFFrontend;

// The brush keeps the live WPF scene on the compositor. Moving a panel does
// not require rendering it to a CPU bitmap or uploading its pixels again.
internal sealed class GameWindowLayer : IDisposable
{
    private readonly HwndSource source;
    private readonly DrawingVisual panels = new();
    private readonly ContainerVisual root = new();
    private readonly DrawingVisual adornments = new();
    private readonly VisualBrush brush;
    private Size pixels, canvasSize;
    private bool hasAdornments;
    public IntPtr Parent { get; }
    public bool IsDisposed => source.IsDisposed;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    public GameWindowLayer(IntPtr parent, FrameworkElement canvas)
    {
        Parent = parent;
        brush = new VisualBrush(canvas) { ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
        const int transparent = 0x20, noActivate = 0x08000000, layered = 0x80000;
        source = new HwndSource(new HwndSourceParameters("Radar display layer")
        {
            ParentWindow = parent,
            WindowStyle = (int)(WinAPIBindings.WindowStyles.WS_CHILD | WinAPIBindings.WindowStyles.WS_CLIPSIBLINGS),
            ExtendedWindowStyle = transparent | noActivate,
            UsesPerPixelTransparency = true,
            Width = 1, Height = 1
        });
        try
        {
            if ((GetWindowLong(source.Handle, -20) & layered) == 0)
                throw new NotSupportedException("Layered child windows are unavailable.");
            source.CompositionTarget.BackgroundColor = Colors.Transparent;
            source.SizeToContent = SizeToContent.Manual;
            root.Children.Add(panels);
            root.Children.Add(adornments);
            source.RootVisual = root;
        }
        catch { source.Dispose(); throw; }
    }

    public void Resize(int width, int height, Size canvas)
    {
        var size = new Size(width, height);
        if (pixels != size)
        {
            // Keep the interactive options HWND above this click-through child.
            const uint noActivate = 0x10, show = 0x40;
            var bottom = new IntPtr(1);
            if (!WinAPIBindings.SetWindowPos(source.Handle, bottom, 0, 0, width, height, noActivate | show))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        var dip = source.CompositionTarget.TransformFromDevice.Transform(new Vector(width, height));
        var scale = root.Transform?.Value ?? Matrix.Identity;
        if (pixels == size && canvasSize == canvas && scale.M11 == dip.X / canvas.Width
            && scale.M22 == dip.Y / canvas.Height) return;
        pixels = size;
        canvasSize = canvas;
        root.Transform = new ScaleTransform(dip.X / canvas.Width, dip.Y / canvas.Height);
        brush.Viewbox = new Rect(canvas);
        using var dc = panels.RenderOpen();
        dc.DrawRectangle(brush, null, brush.Viewbox);
    }

    public void DrawPointer(ImageSource hand, Rect area, Drawing tooltip)
    {
        bool visible = !area.IsEmpty || tooltip != null;
        if (!visible && !hasAdornments) return;
        hasAdornments = visible;
        using var dc = adornments.RenderOpen();
        if (!area.IsEmpty) dc.DrawImage(hand, area);
        if (tooltip != null) dc.DrawDrawing(tooltip);
    }

    public void Dispose() => source.Dispose();
}
