using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BGOverlay;
using BGOverlay.Input;

namespace WPFFrontend
{
    public partial class MainWindow
    {
        internal double CardLayoutWidth => _insideGame ? MainCanvas.ActualWidth : SystemParameters.PrimaryScreenWidth;
        private const int FrameHeaderSize = 56;
        private const int MaxFrameSide = 2048;
        private int _framePid, _revision;
        private IntPtr _gameWindow;
        private DispatcherTimer _frameTimer;
        private MemoryMappedFile _frameMap;
        private MemoryMappedViewAccessor _frameView;
        private Mutex _frameLock;
        private EventWaitHandle _frameReady;
        private byte[] _frameBytes, _publishedBytes;
        private int _publishedWidth, _publishedHeight;
        private Rect _publishedArea;
        private Size _publishedViewport;
        private RenderTargetBitmap _frameBitmap;
        private bool _insideGame;
        private long _nextFrameProbe;
        private TimeSpan _lastRenderingTime = TimeSpan.MinValue;
        private volatile GameTarget[] _gameTargets = Array.Empty<GameTarget>();
        private GamePress _gamePress;
        private EnemyListRow _hoveredGameRow;
        private ActorOutline _actorOutline;
        private ListViewItem _desktopHover;
        private bool _gameIconHovered;
        private GameHandCursor _gameHand;
        private sealed class GamePress
        {
            public GameTarget Target;
            public Point Origin;
            public bool Left;
            // Updated only on the UI thread.
            public bool Dragging;
            public long DragStarted;
            public Point StartOffset;
            public double PanelBaseLeft;
        }

        private sealed record GameTarget(Rect Bounds, Action Click, Action RightClick, FrameworkElement DragElement, EnemyListRow Row = null);

        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out ClientRect rect);
        [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref System.Drawing.Point pt);
        [StructLayout(LayoutKind.Sequential)]
        private struct ClientRect { public int Left, Top, Right, Bottom; }

        private void startGameFrames(int pid, IntPtr hwnd)
        {
            stopGameFrames();
            _framePid = pid;
            _gameWindow = hwnd;
            _frameTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _frameTimer.Tick += publishGameFrame;
            _frameTimer.Start();
        }

        private void stopGameFrames()
        {
            stopDesktopOutline();
            _actorOutline = null;
            closeGameOptions();
            CompositionTarget.Rendering -= onGameRendering;
            _lastRenderingTime = TimeSpan.MinValue;
            _frameTimer?.Stop();
            _frameTimer = null;
            _frameView?.Dispose();
            _frameMap?.Dispose();
            _frameLock?.Dispose();
            _frameReady?.Dispose();
            _frameReady = null;
            _frameView = null;
            _frameMap = null;
            _frameLock = null;
            _frameBitmap = null;
            _frameBytes = null;
            _publishedBytes = null;
            _publishedWidth = _publishedHeight = 0;
            _nextFrameProbe = 0;
            _gameTargets = Array.Empty<GameTarget>();
            updateGameHover(null);
            Interlocked.Exchange(ref _gamePress, null);
            _gameHand?.Dispose();
            _gameHand = null;
            if (_insideGame) { Opacity = 1; Topmost = true; }
            _insideGame = false;
        }

        private bool connectGameFrames()
        {
            if (_frameView != null) return true;
            if (Environment.TickCount64 < _nextFrameProbe) return false;
            _nextFrameProbe = Environment.TickCount64 + 2000;
            string name = $@"Local\BG2Radar.Frame.1.{_framePid}";
            try
            {
                _frameMap = MemoryMappedFile.OpenExisting(name);
                _frameLock = Mutex.OpenExisting(name + ".Lock");
                _frameView = _frameMap.CreateViewAccessor(0, FrameHeaderSize + MaxFrameSide * MaxFrameSide * 4);
                EventWaitHandle.TryOpenExisting(name + ".Updated", out _frameReady);
                _revision = _frameView.ReadInt32(16);
                return true;
            }
            catch (FileNotFoundException) { return false; }
            finally
            {
                if (_frameView == null)
                {
                    _frameMap?.Dispose();
                    _frameLock?.Dispose();
                    _frameMap = null;
                    _frameLock = null;
                }
            }
        }

        // Sample after WPF has advanced its animation clocks and run layout. The
        // timer also keeps the companion alive when WPF throttles an occluded window.
        private void onGameRendering(object sender, EventArgs args)
        {
            var time = ((RenderingEventArgs)args).RenderingTime;
            if (time == _lastRenderingTime) return;
            _lastRenderingTime = time;
            publishGameFrame(sender, args);
        }

        private void publishGameFrame(object sender, EventArgs args)
        {
            try
            {
                if (!connectGameFrames()) return;
                long lastPresent = _frameView.ReadInt64(0);
                if (lastPresent == 0 || Environment.TickCount64 - lastPresent > 1500)
                {
                    clearGameInput();
                    // An exclusive fullscreen device can stop presenting during Alt+Tab.
                    return;
                }
                if (!_insideGame)
                {
                    stopDesktopOutline();
                    Topmost = false;
                    _insideGame = true;
                    _gameHand = new GameHandCursor(_framePid, _processHacker.ResourceManager);
                    CompositionTarget.Rendering += onGameRendering;
                    _frameTimer.Interval = TimeSpan.FromMilliseconds(16);
                }
                Opacity = IsActive ? 1 : 0;
                _gameWindow = new IntPtr(_frameView.ReadInt64(48));
                Configuration.HWndPtr = _gameWindow;
                if (!GetClientRect(_gameWindow, out var client) || client.Right <= 0 || client.Bottom <= 0)
                {
                    clearGameInput();
                    return;
                }
                var origin = new System.Drawing.Point();
                if (!ClientToScreen(_gameWindow, ref origin)) return;
                bounds = new System.Drawing.Rectangle(origin.X, origin.Y, client.Right, client.Bottom);
                var dpi = VisualTreeHelper.GetDpi(this);
                if (WindowState != WindowState.Normal) return;
                Left = bounds.Left / dpi.DpiScaleX;
                Top = bounds.Top / dpi.DpiScaleY;
                Width = bounds.Width / dpi.DpiScaleX;
                Height = bounds.Height / dpi.DpiScaleY;
                if (MainCanvas.ActualWidth < 1 || MainCanvas.ActualHeight < 1) return;

                bool pointerInGame = WinApiBindings.WinAPIBindings.GetForegroundWindow() == _gameWindow;
                var mouse = System.Windows.Forms.Cursor.Position;
                var pointer = new Point(mouse.X, mouse.Y);
                if (!pointerInGame) cancelGamePress();
                var press = Volatile.Read(ref _gamePress);
                // The hook consumes button-down before Windows updates its async key state.
                // The matching hook button-up or loss of game focus ends the gesture.
                if (press?.Left == true && pointerInGame)
                    updateGameDrag(press, pointer);

                var layers = new List<(FrameworkElement Element, Rect Area)>();
                var targets = new List<GameTarget>();
                var viewport = new Rect(MainCanvas.RenderSize);
                Rect areaOf(FrameworkElement e) =>
                    e.TransformToAncestor(MainCanvas).TransformBounds(new Rect(e.RenderSize));
                void addLayer(FrameworkElement e)
                {
                    if (e.IsVisible && e.ActualWidth > 0 && e.ActualHeight > 0)
                        layers.Add((e, areaOf(e)));
                }
                void addTarget(Rect rect, Action click, Action rightClick, FrameworkElement dragElement, EnemyListRow row = null)
                {
                    rect.Intersect(viewport);
                    if (rect.IsEmpty) return;
                    var screen = new Rect(bounds.X + rect.X * bounds.Width / viewport.Width,
                        bounds.Y + rect.Y * bounds.Height / viewport.Height,
                        rect.Width * bounds.Width / viewport.Width, rect.Height * bounds.Height / viewport.Height);
                    targets.Add(new GameTarget(screen, click, rightClick, dragElement, row));
                }

                addLayer(ListView);
                if (ListView.IsVisible)
                {
                    addTarget(areaOf(ListView), null, null, StackPanel);
                    foreach (EnemyListRow row in EnemyTextEntries)
                    {
                        if (ListView.ItemContainerGenerator.ContainerFromItem(row) is not ListViewItem item) continue;
                        var rect = areaOf(item);
                        rect.Intersect(areaOf(ListView));
                        addTarget(rect, () => changeEnemyControlStateByEntity(
                            _processHacker.entityList.FirstOrDefault(e => e.tag == row.Tag)), null,
                            StackPanel, row);
                    }
                }
                addLayer(MinMaxBtn);
                addTarget(areaOf(MinMaxBtn), () => MinMaxBtn_Click(null, null), showGameOptions, MinMaxBtn);
                foreach (var card in MainCanvas.Children.OfType<EnemyControl>())
                {
                    addLayer(card);
                    if (!card.IsVisible) continue;
                    var rect = areaOf(card);
                    addTarget(rect, null, () => card.Label_MouseDown(null, null), card);
                    addTarget(new Rect(rect.Right - 22, rect.Top, 22, 25), () => card.Label_MouseDown(null, null),
                        () => card.Label_MouseDown(null, null), null);
                }
                if (_optionsHost != null && positionGameOptions(client, out var panel))
                {
                    // Also draw the child in the composed frame so it stays above cards,
                    // including when the graphics backend uses exclusive fullscreen.
                    var rect = new Rect(panel.X * viewport.Width / client.Right, 0,
                        panel.Width * viewport.Width / client.Right, panel.Height * viewport.Height / client.Bottom);
                    layers.Add((_options, rect));
                    addTarget(rect, null, null, null);
                }
                var hit = pointerInGame ? targets.LastOrDefault(t => t.Bounds.Contains(pointer)) : null;
                updateGameHover(press?.Target ?? hit);
                var outlineArea = locateOutline(press == null ? hit?.Row : null);
                // The child HWND owns its hardware cursor. Publishing another cursor
                // for the parent here made the two input threads replace each other's.
                bool hover = press != null || (hit != null && !isGameOptionsInput(mouse.X, mouse.Y));
                int cursorFrame = press == null ? 0 : 1;
                if (press?.Dragging == true)
                    cursorFrame = Environment.TickCount64 - press.DragStarted < 100 ? 44 : 45;
                _gameHand.SetFrame(hover ? cursorFrame : -1);
                var handArea = Rect.Empty;
                if (hover && _gameHand.UseSoftwareCursor)
                {
                    var local = new Point((mouse.X - bounds.X) * viewport.Width / bounds.Width,
                        (mouse.Y - bounds.Y) * viewport.Height / bounds.Height);
                    handArea = _gameHand.AreaAt(local, dpi);
                }
                var crop = handArea;
                crop.Union(outlineArea);
                foreach (var layer in layers) crop.Union(layer.Area);
                crop.Intersect(viewport);
                if (crop.IsEmpty || crop.Width < 1 || crop.Height < 1) return;
                // Anchor the raster to the game window. A hovered actor or the
                // glove can extend the crop by a fraction of a pixel. Fitting
                // text to that crop again changes the antialiasing of every row.
                int rasterWidth = Math.Min(client.Right, MaxFrameSide);
                int rasterHeight = Math.Min(client.Bottom, MaxFrameSide);
                double scaleX = rasterWidth / viewport.Width, scaleY = rasterHeight / viewport.Height;
                int left = (int)Math.Floor(crop.Left * scaleX);
                int top = (int)Math.Floor(crop.Top * scaleY);
                int right = Math.Min(rasterWidth, (int)Math.Ceiling(crop.Right * scaleX));
                int bottom = Math.Min(rasterHeight, (int)Math.Ceiling(crop.Bottom * scaleY));
                int w = right - left, h = bottom - top;
                var placement = new Rect((double)left / rasterWidth, (double)top / rasterHeight,
                    (double)w / rasterWidth, (double)h / rasterHeight);
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    dc.PushTransform(new MatrixTransform(scaleX, 0, 0, scaleY, -left, -top));
                    if (!outlineArea.IsEmpty) dc.DrawImage(_actorOutline.Image, outlineArea);
                    foreach (var (element, area) in layers)
                    {
                        var brush = new VisualBrush(element) { Stretch = Stretch.Fill };
                        dc.DrawRectangle(brush, null, area);
                    }
                    if (!handArea.IsEmpty) dc.DrawImage(_gameHand.Image, handArea);
                }
                // A sliding panel changes its clipped size by a few pixels every frame.
                // Reuse the bitmap and staging buffers throughout that animation.
                int bw = (w + 63) & ~63, bh = (h + 63) & ~63;
                if (_frameBitmap == null || _frameBitmap.PixelWidth != bw || _frameBitmap.PixelHeight != bh)
                    _frameBitmap = new RenderTargetBitmap(bw, bh, 96, 96, PixelFormats.Pbgra32);
                int byteCount = w * h * 4;
                if (_frameBytes == null || _frameBytes.Length < byteCount)
                    _frameBytes = new byte[bw * bh * 4];
                _frameBitmap.Clear();
                _frameBitmap.Render(visual);
                _frameBitmap.CopyPixels(new Int32Rect(0, 0, w, h), _frameBytes, w * 4, 0);
                bool changed = _publishedArea != placement || _publishedViewport != viewport.Size
                    || _publishedWidth != w || _publishedHeight != h
                    || !_frameBytes.AsSpan(0, byteCount).SequenceEqual(_publishedBytes.AsSpan(0, byteCount));
                bool acquired;
                try { acquired = _frameLock.WaitOne(0); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) return;
                try
                {
                    if (changed)
                    {
                        _frameView.WriteArray(FrameHeaderSize, _frameBytes, 0, byteCount);
                        _frameView.Write(16, ++_revision);
                        (_publishedBytes, _frameBytes) = (_frameBytes, _publishedBytes);
                        _publishedWidth = w;
                        _publishedHeight = h;
                        _publishedArea = placement;
                        _publishedViewport = viewport.Size;
                    }
                    _frameView.Write(20, w);
                    _frameView.Write(24, h);
                    _frameView.Write(28, (float)placement.Left);
                    _frameView.Write(32, (float)placement.Top);
                    _frameView.Write(36, (float)placement.Right);
                    _frameView.Write(40, (float)placement.Bottom);
                    _frameView.Write(8, Environment.TickCount64);
                }
                finally { _frameLock.ReleaseMutex(); }
                if (changed) _frameReady?.Set();
                _gameTargets = targets.ToArray();
            }
            catch (Exception ex)
            {
                Logger.Error("In-game frame publisher stopped", ex);
                stopGameFrames();
            }
        }

        private void updateGameDrag(GamePress press, Point position)
        {
            var element = press.Target.DragElement;
            if (element == null || !element.IsDescendantOf(MainCanvas)) return;
            var dpi = VisualTreeHelper.GetDpi(this);
            double dx = (position.X - press.Origin.X) / dpi.DpiScaleX;
            double dy = (position.Y - press.Origin.Y) / dpi.DpiScaleY;
            bool card = element is EnemyControl;
            if (!press.Dragging)
            {
                if (Math.Abs(dx) < SystemParameters.MinimumHorizontalDragDistance &&
                    (!card || Math.Abs(dy) < SystemParameters.MinimumVerticalDragDistance)) return;
                press.Dragging = true;
                press.DragStarted = Environment.TickCount64;
                if (card)
                {
                    double x = Canvas.GetLeft(element), y = Canvas.GetTop(element);
                    press.StartOffset = new Point(double.IsNaN(x) ? 0 : x, double.IsNaN(y) ? 0 : y);
                }
                else
                {
                    var transform = (TranslateTransform)element.RenderTransform;
                    press.StartOffset = new Point(transform.X, 0);
                    press.PanelBaseLeft = element.TranslatePoint(new Point(), MainCanvas).X - transform.X;
                }
            }
            if (card)
            {
                Canvas.SetLeft(element, press.StartOffset.X + dx);
                Canvas.SetTop(element, press.StartOffset.Y + dy);
                // Capture and hit-test the arranged card at its new location in this same frame.
                MainCanvas.UpdateLayout();
            }
            else
            {
                double min = -press.PanelBaseLeft;
                double max = Math.Max(min, MainCanvas.ActualWidth - element.ActualWidth + min);
                ((TranslateTransform)element.RenderTransform).X = Math.Clamp(press.StartOffset.X + dx, min, max);
            }
        }

        private void saveGameDrag(GamePress press)
        {
            if (press?.Dragging != true) return;
            var element = press.Target.DragElement;
            if (element is EnemyControl) return;
            int x = (int)Math.Round(((TranslateTransform)element.RenderTransform).X);
            if (element == MinMaxBtn) Configuration.RadarIconXOffset = x;
            else Configuration.EnemyListXOffset = x;
            Configuration.SaveConfig();
        }

        private Rect locateOutline(EnemyListRow row)
        {
            if (row?.Entity == null || _processHacker?.ResourceManager == null) return Rect.Empty;
            _actorOutline ??= new ActorOutline(_processHacker.ResourceManager);
            return _actorOutline.Locate(row.Entity, MainCanvas.RenderSize);
        }

        private void ActorRow_Enter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_insideGame) return;
            stopDesktopOutline();
            _desktopHover = (ListViewItem)sender;
            CompositionTarget.Rendering += drawDesktopOutline;
        }

        private void ActorRow_Leave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_desktopHover == sender) stopDesktopOutline();
        }

        private void stopDesktopOutline()
        {
            CompositionTarget.Rendering -= drawDesktopOutline;
            _desktopHover = null;
            DesktopActorOutline.Visibility = Visibility.Collapsed;
            DesktopActorOutline.Source = null;
        }

        private void drawDesktopOutline(object sender, EventArgs args)
        {
            if (_desktopHover?.IsVisible != true || !_desktopHover.IsMouseOver)
            {
                stopDesktopOutline();
                return;
            }
            var area = locateOutline(_desktopHover.DataContext as EnemyListRow);
            if (area.IsEmpty)
            {
                DesktopActorOutline.Visibility = Visibility.Collapsed;
                return;
            }
            DesktopActorOutline.Source = _actorOutline.Image;
            DesktopActorOutline.Width = area.Width;
            DesktopActorOutline.Height = area.Height;
            Canvas.SetLeft(DesktopActorOutline, area.Left);
            Canvas.SetTop(DesktopActorOutline, area.Top);
            DesktopActorOutline.Visibility = Visibility.Visible;
        }

        private void updateGameHover(GameTarget hit)
        {
            if (_hoveredGameRow != hit?.Row)
            {
                if (_hoveredGameRow != null) _hoveredGameRow.IsGameHovered = false;
                _hoveredGameRow = hit?.Row;
                if (_hoveredGameRow != null) _hoveredGameRow.IsGameHovered = true;
            }
            bool icon = hit != null && hit.DragElement == MinMaxBtn;
            if (icon == _gameIconHovered) return;
            _gameIconHovered = icon;
            if (icon) MinMaxBtn_MouseEnter(null, null);
            else MinMaxBtn_MouseLeave(null, null);
        }

        private void clearGameInput()
        {
            _gameTargets = Array.Empty<GameTarget>();
            updateGameHover(null);
            cancelGamePress();
            _gameHand?.SetFrame(-1);
        }

        private void cancelGamePress() => saveGameDrag(Interlocked.Exchange(ref _gamePress, null));

        // Only button edges cross the hook thread. The presenter samples the latest
        // pointer position once per game frame, even outside the original rectangle.
        private bool handleGameButton(MouseMessageEventArgs e)
        {
            var code = (MouseMessageCode)e.MessageCode;
            if (isGameOptionsInput(e.X, e.Y) && Volatile.Read(ref _gamePress) == null) return false;
            bool left = code == MouseMessageCode.LeftButtonDown || code == MouseMessageCode.LeftButtonUp;
            var point = new Point(e.X, e.Y);
            var hit = _gameTargets.LastOrDefault(t => t.Bounds.Contains(point));
            if (!left && code != MouseMessageCode.RightButtonDown && code != MouseMessageCode.RightButtonUp)
                return hit != null || Volatile.Read(ref _gamePress) != null;
            if (code == MouseMessageCode.LeftButtonDown || code == MouseMessageCode.RightButtonDown)
            {
                if (hit == null) return Volatile.Read(ref _gamePress) != null;
                if (!left && hit.DragElement == MinMaxBtn)
                {
                    Dispatcher.BeginInvoke(new Action(showGameOptions));
                    return true;
                }
                var next = new GamePress { Target = hit, Origin = point, Left = left };
                Interlocked.CompareExchange(ref _gamePress, next, null);
                return true;
            }
            var press = Volatile.Read(ref _gamePress);
            if (press == null || press.Left != left || Interlocked.CompareExchange(ref _gamePress, null, press) != press)
                return false;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_insideGame) return;
                if (left) updateGameDrag(press, point);
                if (press.Dragging) saveGameDrag(press);
                else if (press.Target.Bounds.Contains(point)) (left ? press.Target.Click : press.Target.RightClick)?.Invoke();
            }));
            return true;
        }
    }
}
