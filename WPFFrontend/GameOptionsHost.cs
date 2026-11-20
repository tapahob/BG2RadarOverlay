using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using BGOverlay;
using WinApiBindings;

namespace WPFFrontend
{
    public partial class MainWindow
    {
        private HwndSource _optionsHost;
        private IntPtr _optionsHwnd;
        private Rect _optionsPlacement = Rect.Empty;

        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(System.Drawing.Point point);
        [DllImport("user32.dll")] private static extern bool IsChild(IntPtr parent, IntPtr window);
        [DllImport("user32.dll")] private static extern IntPtr SetCursor(IntPtr cursor);

        private bool isGameOptionsInput(int x, int y)
        {
            var panel = Volatile.Read(ref _optionsHwnd);
            if (panel == IntPtr.Zero) return false;
            var underMouse = WindowFromPoint(new System.Drawing.Point(x, y));
            if (panel == underMouse || IsChild(panel, underMouse)) return true;
            // WPF combo boxes create their own popup HWND.
            WinAPIBindings.GetWindowThreadProcessId(underMouse, out var pid);
            return pid == Environment.ProcessId;
        }

        private void showGameOptions()
        {
            if (_options == null || _optionsHost != null || !GetClientRect(_gameWindow, out var client)) return;
            _options.Init();
            try
            {
                var parameters = new HwndSourceParameters(RadarLocalization.Get("Str_Options"))
                {
                    ParentWindow = _gameWindow,
                    WindowStyle = (int)(WinAPIBindings.WindowStyles.WS_CHILD | WinAPIBindings.WindowStyles.WS_CLIPSIBLINGS),
                    TreatAsInputRoot = true
                };
                _optionsHost = new HwndSource(parameters);
                _optionsHost.AddHook(optionsWindowMessage);
                Volatile.Write(ref _optionsHwnd, _optionsHost.Handle);
                MainGrid.Children.Remove(_options);
                _options.SetWindowHosted(true);
                _options.Cursor = _gameHand?.PointingCursor ?? System.Windows.Input.Cursors.Hand;
                _options.ForceCursor = true;
                _options.WindowCloseRequested += closeGameOptions;
                _optionsHost.SizeToContent = SizeToContent.WidthAndHeight;
                _optionsHost.RootVisual = _options;
                _options.UpdateLayout();
                positionGameOptions(client, out _);
                Logger.Debug("Opened radar options in game window");
            }
            catch (Exception ex)
            {
                closeGameOptions();
                Logger.Error("Could not open radar options in game window", ex);
            }
        }

        private IntPtr optionsWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
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

        private bool positionGameOptions(ClientRect game, out Rect panel)
        {
            panel = Rect.Empty;
            if (!GetClientRect(_optionsHwnd, out var size) || size.Right <= 0 || size.Bottom <= 0) return false;
            int left = Math.Max(0, (game.Right - size.Right) / 2);
            panel = new Rect(left, 0, size.Right, size.Bottom);
            if (_optionsPlacement == panel) return true;
            // SizeToContent owns the dimensions. Moving a child HWND does not activate
            // a second top-level window or switch the game out of fullscreen.
            const uint noSize = 0x0001, noActivate = 0x0010, show = 0x0040;
            if (!WinAPIBindings.SetWindowPos(_optionsHwnd, IntPtr.Zero, left, 0, 0, 0, noSize | noActivate | show))
                return false;
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
            host.RootVisual = null;
            host.Dispose();
            _options.SetWindowHosted(false);
            if (_options.Parent == null) MainGrid.Children.Add(_options);
            if (WinAPIBindings.GetForegroundWindow() == _gameWindow) WinAPIBindings.SetFocus(_gameWindow);
        }
    }
}
