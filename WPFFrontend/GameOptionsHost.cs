using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Controls;
using System.Windows.Media;
using BGOverlay;
using WinApiBindings;

namespace WPFFrontend
{
    public partial class MainWindow
    {
        private HwndSource _optionsHost;
        private IntPtr _optionsHwnd;
        private readonly ScaleTransform _optionsScale = new();
        private Rect _optionsPlacement = Rect.Empty;
        private bool _detachedOptions;
        private System.Drawing.Point _optionsScreenOrigin;

        [StructLayout(LayoutKind.Sequential)]
        private struct GuiThreadInfo
        {
            public int Size, Flags;
            public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
            public ClientRect CaretRect;
        }

        [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
        [DllImport("user32.dll")] private static extern IntPtr SetCursor(IntPtr cursor);

        private bool isGameOptionsInput(int x, int y)
        {
            var panel = Volatile.Read(ref _optionsHwnd);
            if (panel == IntPtr.Zero) return false;
            if (GetWindowRect(panel, out var r) && x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom)
                return true;
            // Combo dropdowns and slider drags retain mouse capture outside the panel.
            // Never ask another window to hit-test from the low-level input hook.
            uint thread = WinAPIBindings.GetWindowThreadProcessId(panel, out _);
            var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
            return thread != 0 && GetGUIThreadInfo(thread, ref info) && info.Capture != IntPtr.Zero
                && WinAPIBindings.GetWindowThreadProcessId(info.Capture, out _) == thread;
        }

        private void showGameOptions()
        {
            if (_options == null || _optionsHost != null || WinAPIBindings.GetForegroundWindow() != _gameWindow
                || !GetClientRect(_gameWindow, out var client) || IsIconic(_gameWindow)) return;
            _options.Init();
            try
            {
                // A child HWND attaches the two UI threads' input queues. Creating one
                // on an exclusive D3D9 surface also interrupts presentation during capture.
                _detachedOptions = _windowLayer == null || _windowLayer.IsPopup;
                const int toolWindow = 0x80, noActivate = 0x08000000, noParentNotify = 0x4;
                var parameters = new HwndSourceParameters(RadarLocalization.Get("Str_Options"))
                {
                    ParentWindow = _detachedOptions ? IntPtr.Zero : _gameWindow,
                    Width = 1, Height = 1,
                    WindowStyle = _detachedOptions ? unchecked((int)WinAPIBindings.WindowStyles.WS_POPUP)
                        : (int)(WinAPIBindings.WindowStyles.WS_CHILD | WinAPIBindings.WindowStyles.WS_CLIPSIBLINGS),
                    ExtendedWindowStyle = _detachedOptions ? toolWindow | noActivate : noParentNotify,
                    UsesPerPixelTransparency = _detachedOptions,
                    TreatAsInputRoot = true
                };
                _optionsHost = new HwndSource(parameters);
                if (_detachedOptions)
                {
                    _optionsHost.CompositionTarget.RenderMode = RenderMode.SoftwareOnly;
                    _optionsHost.CompositionTarget.BackgroundColor = Colors.Transparent;
                }
                _optionsHost.AddHook(optionsWindowMessage);
                Volatile.Write(ref _optionsHwnd, _optionsHost.Handle);
                DesktopOptionsHost.Child = null;
                _options.SetWindowHosted(true);
                _options.Cursor = _gameHand?.PointingCursor ?? System.Windows.Input.Cursors.Hand;
                _options.ForceCursor = true;
                _options.WindowCloseRequested += closeGameOptions;
                _optionsHost.SizeToContent = SizeToContent.WidthAndHeight;
                _optionsHost.RootVisual = new Viewbox { StretchDirection = StretchDirection.DownOnly, Child = _options };
                FitHostedOptions(client);
                _options.UpdateLayout();
                positionGameOptions(client, out _);
                Logger.Debug($"Opened radar options ({(_detachedOptions ? "independent window" : "game child")})");
            }
            catch (Exception ex)
            {
                closeGameOptions();
                Logger.Error("Could not open radar options in game window", ex);
            }
        }

        private IntPtr optionsWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            // Deliver the click to the detached panel while the fullscreen game stays active.
            const int wmMouseActivate = 0x0021, maNoActivate = 3;
            if (_detachedOptions && message == wmMouseActivate)
            {
                handled = true;
                return new IntPtr(maNoActivate);
            }

            // DefWindowProc asks the parent to choose the cursor first. BG2 would
            // install its world cursor before WPF gets each mouse-move notification.
            const int wmSetCursor = 0x0020, htClient = 1;
            var hand = _gameHand?.PointingHandle ?? IntPtr.Zero;
            if (message != wmSetCursor || (lParam.ToInt64() & 0xffff) != htClient || hand == IntPtr.Zero)
                return IntPtr.Zero;
            SetCursor(hand);
            handled = true;
            return new IntPtr(1);
        }

        private void UpdateOptionsScale(double scale)
        {
            // Keep the slider under the pointer while the rest of the radar previews the new size.
            if (_optionsScale.ScaleX == scale || _options?.RadarZoom.IsMouseCaptureWithin == true) return;
            _optionsScale.ScaleX = _optionsScale.ScaleY = scale;
        }

        private void FitHostedOptions(ClientRect client)
        {
            var device = _optionsHost.CompositionTarget.TransformToDevice;
            var root = (Viewbox)_optionsHost.RootVisual;
            root.MaxWidth = client.Right / device.M11;
            root.MaxHeight = client.Bottom / device.M22;
            UpdateOptionsScale(_panelPixelScale / device.M22);
        }

        private bool positionGameOptions(ClientRect game, out Rect panel)
        {
            panel = Rect.Empty;
            FitHostedOptions(game);
            if (!GetClientRect(_optionsHwnd, out var size) || size.Right <= 0 || size.Bottom <= 0) return false;
            int left = Math.Max(0, (game.Right - size.Right) / 2);
            panel = new Rect(left, 0, size.Right, size.Bottom);
            var origin = new System.Drawing.Point(left, 0);
            if (_detachedOptions && !ClientToScreen(_gameWindow, ref origin)) return false;
            if (_optionsPlacement == panel && origin == _optionsScreenOrigin) return true;
            const uint noSize = 0x0001, noActivate = 0x0010, show = 0x0040;
            if (!WinAPIBindings.SetWindowPos(_optionsHwnd, _detachedOptions ? new IntPtr(-1) : IntPtr.Zero,
                    origin.X, origin.Y, 0, 0, noSize | noActivate | show))
                return false;
            _optionsScreenOrigin = origin;
            _optionsPlacement = panel;
            return true;
        }

        private void closeGameOptions()
        {
            if (_optionsHost == null) return;
            var host = _optionsHost;
            _optionsHost = null;
            Volatile.Write(ref _optionsHwnd, IntPtr.Zero);
            _optionsPlacement = Rect.Empty;
            _options.WindowCloseRequested -= closeGameOptions;
            _options.ForceCursor = false;
            _options.ClearValue(FrameworkElement.CursorProperty);
            host.RemoveHook(optionsWindowMessage);
            if (host.RootVisual is Viewbox root) root.Child = null;
            host.RootVisual = null;
            host.Dispose();
            _options.SetWindowHosted(false);
            DesktopOptionsHost.Child = _options;
            _detachedOptions = false;
        }
    }
}
