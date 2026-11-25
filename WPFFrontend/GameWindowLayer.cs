using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using WinApiBindings;

namespace WPFFrontend;

// The brush keeps the live WPF scene on the compositor. Moving a panel does
// not require rendering it to a CPU bitmap or uploading its pixels again.
internal sealed class GameWindowLayer : IDisposable
{
    private readonly HwndSource source;
    private readonly DrawingVisual panels = new();
    private readonly DrawingVisual options = new();
    private readonly ContainerVisual root = new();
    private readonly DrawingVisual adornments = new();
    private readonly VisualBrush brush;
    private Size pixels, canvasSize;
    private System.Drawing.Point position;
    private Rect optionsArea = Rect.Empty;
    private FrameworkElement optionsContent;
    private bool hasAdornments;
    private CardExitComposition exits;
    public IntPtr Parent { get; }
    public bool IsPopup { get; }
    public bool IsDisposed => source.IsDisposed;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    public static bool IsPopupWindow(IntPtr hwnd) =>
        ((uint)GetWindowLong(hwnd, -16) & (uint)WinAPIBindings.WindowStyles.WS_POPUP) != 0;

    public GameWindowLayer(IntPtr parent, FrameworkElement canvas, bool popup)
    {
        Parent = parent;
        IsPopup = popup;
        brush = new VisualBrush(canvas) { ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
        const int transparent = 0x20, noActivate = 0x08000000, layered = 0x80000;
        const int appWindow = 0x40000, noParentNotify = 0x4;
        // WGC requires APPWINDOW to capture this owned popup on its own.
        // A stable title lets OBS reconnect when the player returns to fullscreen.
        source = new HwndSource(new HwndSourceParameters(popup ? "BG Radar - Fullscreen Overlay" : "Radar display layer")
        {
            ParentWindow = parent,
            WindowStyle = popup ? unchecked((int)WinAPIBindings.WindowStyles.WS_POPUP)
                : (int)(WinAPIBindings.WindowStyles.WS_CHILD | WinAPIBindings.WindowStyles.WS_CLIPSIBLINGS),
            ExtendedWindowStyle = transparent | noActivate | (popup ? appWindow : noParentNotify),
            UsesPerPixelTransparency = true,
            Width = 1, Height = 1
        });
        try
        {
            if ((GetWindowLong(source.Handle, -20) & layered) == 0)
                throw new NotSupportedException("Layered windows are unavailable.");
            source.CompositionTarget.BackgroundColor = Colors.Transparent;
            source.SizeToContent = SizeToContent.Manual;
            root.Children.Add(panels);
            root.Children.Add(options);
            root.Children.Add(adornments);
            source.RootVisual = root;
        }
        catch { source.Dispose(); throw; }
    }

    public void Resize(System.Drawing.Rectangle bounds, Size canvas)
    {
        int width = bounds.Width, height = bounds.Height;
        var size = new Size(width, height);
        var origin = IsPopup ? bounds.Location : System.Drawing.Point.Empty;
        if (pixels != size || position != origin)
        {
            // The popup stays above its owner. A child stays below the options HWND.
            const uint noActivate = 0x10, show = 0x40, noZOrder = 0x4;
            if (!WinAPIBindings.SetWindowPos(source.Handle, new IntPtr(1), origin.X, origin.Y,
                    width, height, noActivate | show | (IsPopup ? noZOrder : 0)))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            position = origin;
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

    public void ShowOptions(FrameworkElement content, Rect area)
    {
        // Input still goes through the game's options child, underneath the popup.
        if (!IsPopup || (optionsContent == content && optionsArea == area)) return;
        optionsContent = content;
        optionsArea = area;
        using var dc = options.RenderOpen();
        if (content != null) dc.DrawRectangle(new VisualBrush(content) { Stretch = Stretch.Fill }, null, area);
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

    public bool TryAnimateExit(FrameworkElement card, Rect area, BackEase ease, TimeSpan duration)
    {
        if (IsDisposed || area.IsEmpty || area.Bottom <= 0 || canvasSize.Width <= 0 || canvasSize.Height <= 0)
            return false;
        area.Scale(pixels.Width / canvasSize.Width, pixels.Height / canvasSize.Height);
        if (area.Width < 1 || area.Height < 1) return false;
        try
        {
            exits ??= new CardExitComposition(source.Handle);
            exits.Start(card, area, ease, duration);
            return true;
        }
        catch (Exception e) when (e is SharpGen.Runtime.SharpGenException or DllNotFoundException
            or EntryPointNotFoundException or NotSupportedException or OverflowException)
        {
            BGOverlay.Logger.Error("Could not animate the closing card with DirectComposition", e);
            exits?.Dispose();
            exits = null;
            return false;
        }
    }

    public void Dispose()
    {
        exits?.Dispose();
        source.Dispose();
    }
}
