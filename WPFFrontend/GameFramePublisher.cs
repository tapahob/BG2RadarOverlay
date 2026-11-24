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
using WPFFrontend.CombatText;

namespace WPFFrontend
{
    public partial class MainWindow
    {
        private const int FrameHeaderSize = 56;
        private int _maxFrameSide;
        private int _framePid, _revision;
        private IntPtr _gameWindow;
        private DispatcherTimer _frameTimer;
        private MemoryMappedFile _frameMap;
        private MemoryMappedViewAccessor _frameView;
        private Mutex _frameLock;
        private EventWaitHandle _frameReady;
        private EventWaitHandle _windowLayerAllowed;
        private GameWindowLayer _windowLayer;
        private long _nextLayerAttempt;
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
        private IntPtr _desktopCursorBeforeBuff;
        private CombatEventReader _combatEvents;
        private volatile bool _hasCombatEvents;
        private readonly ActorProjection _combatProjection = new();
        private long _lastCombatFrame, _lastOverheadFrame;
        private sealed class GamePress
        {
            public GameTarget Target;
            public Point Origin;
            public bool Left;
            // Updated only on the UI thread.
            public bool Dragging;
            public long DragStarted;
            public Matrix CardStart;
            public MatrixTransform CardTransform;
            public double PanelStartX, PanelBaseLeft;
        }

        private sealed record GameTarget(Rect Bounds, Action Click, Action RightClick, FrameworkElement DragElement, EnemyListRow Row = null);

        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out ClientRect rect);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out ClientRect rect);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int virtualKey);
        [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref System.Drawing.Point pt);
        [StructLayout(LayoutKind.Sequential)]
        private struct ClientRect { public int Left, Top, Right, Bottom; }

        private void startGameFrames(int pid, IntPtr hwnd)
        {
            stopGameFrames();
            _framePid = pid;
            _gameWindow = hwnd;
            _combatEvents = new CombatEventReader(pid);
            _combatEvents.Poll(Environment.TickCount64, CombatLayer.Add);
            _hasCombatEvents = _combatEvents.Connected;
            _frameTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(32) };
            _frameTimer.Tick += publishGameFrame;
            _frameTimer.Start();
        }

        private void stopGameFrames()
        {
            _combatEvents?.Dispose();
            _combatEvents = null;
            _hasCombatEvents = false;
            _framePid = 0;
            CombatLayer.Clear();
            BuffLayer.Clear();
            updateDesktopBuffCursor(false);
            stopDesktopOutline();
            _actorOutline = null;
            closeGameOptions();
            CompositionTarget.Rendering -= onGameRendering;
            _lastRenderingTime = TimeSpan.MinValue;
            _frameTimer?.Stop();
            _frameTimer = null;
            closeWindowLayer();
            _windowLayerAllowed?.Dispose();
            _windowLayerAllowed = null;
            _nextLayerAttempt = 0;
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
                _frameView = _frameMap.CreateViewAccessor();
                // Both modules use a square BGRA buffer after the same header. Read
                // its capacity so an existing 2048-side installation still works.
                _maxFrameSide = (int)Math.Sqrt((_frameView.Capacity - FrameHeaderSize) / 4);
                EventWaitHandle.TryOpenExisting(name + ".Updated", out _frameReady);
                EventWaitHandle.TryOpenExisting(name + ".WindowLayer", out _windowLayerAllowed);
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

        private void closeWindowLayer()
        {
            if (_windowLayer == null) return;
            _windowLayer.Dispose();
            _windowLayer = null;
            DesktopActorOutline.Visibility = Visibility.Collapsed;
            DesktopActorOutline.Source = null;
            if (_frameTimer != null) _frameTimer.Interval = TimeSpan.FromMilliseconds(16);
        }

        private void prepareWindowLayer(ClientRect client)
        {
            bool allowed = _windowLayerAllowed?.WaitOne(0) == true;
            if (_windowLayer != null && (!allowed || _windowLayer.Parent != _gameWindow || _windowLayer.IsDisposed))
                closeWindowLayer();
            if (!allowed || Environment.TickCount64 < _nextLayerAttempt) return;
            try
            {
                if (_windowLayer == null)
                {
                    // Retire any bitmap from fallback mode or an earlier companion.
                    bool acquired;
                    try { acquired = _frameLock.WaitOne(0); }
                    catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired) return;
                    try
                    {
                        _frameView.Write(20, 0);
                        _frameView.Write(24, 0);
                        _frameView.Write(16, ++_revision);
                        _publishedWidth = _publishedHeight = 0;
                    }
                    finally { _frameLock.ReleaseMutex(); }
                    _frameReady?.Set();
                    _windowLayer = new GameWindowLayer(_gameWindow, MainCanvas);
                    // The visible child drives Rendering at display cadence.
                    _frameTimer.Interval = TimeSpan.FromMilliseconds(200);
                }
                _windowLayer.Resize(client.Right, client.Bottom, MainCanvas.RenderSize);
            }
            catch (Exception ex)
            {
                closeWindowLayer();
                _nextLayerAttempt = Environment.TickCount64 + 5000;
                Logger.Error("Could not display the radar in a layered child window", ex);
            }
        }

        private void publishGameFrame(object sender, EventArgs args)
        {
            try
            {
                updateActorLayers();
                if (!connectGameFrames()) return;
                long lastPresent = _frameView.ReadInt64(0);
                if (lastPresent == 0 || Environment.TickCount64 - lastPresent > 1500)
                {
                    closeWindowLayer();
                    clearGameInput();
                    // An exclusive fullscreen device can stop presenting during Alt+Tab.
                    return;
                }
                if (!_insideGame)
                {
                    updateDesktopBuffCursor(false);
                    stopDesktopOutline();
                    Topmost = false;
                    _insideGame = true;
                    _gameHand ??= new GameHandCursor(_framePid, _processHacker.ResourceManager);
                    CompositionTarget.Rendering += onGameRendering;
                    _frameTimer.Interval = TimeSpan.FromMilliseconds(16);
                }
                Opacity = IsActive ? 1 : 0;
                _gameWindow = new IntPtr(_frameView.ReadInt64(48));
                Configuration.HWndPtr = _gameWindow;
                if (!GetClientRect(_gameWindow, out var client) || client.Right <= 0 || client.Bottom <= 0)
                {
                    closeWindowLayer();
                    clearGameInput();
                    return;
                }
                var origin = new System.Drawing.Point();
                if (!ClientToScreen(_gameWindow, ref origin)) return;
                bounds = new System.Drawing.Rectangle(origin.X, origin.Y, client.Right, client.Bottom);
                var dpi = VisualTreeHelper.GetDpi(this);
                if (WindowState != WindowState.Normal) { closeWindowLayer(); return; }
                FitOverlayToBounds();
                if (MainCanvas.ActualWidth < 1 || MainCanvas.ActualHeight < 1) return;
                prepareWindowLayer(client);

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
                    if (_windowLayer == null && e.IsVisible && e.ActualWidth > 0 && e.ActualHeight > 0)
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
                    addTarget(areaOf(card.CloseTarget), () => card.Label_MouseDown(null, null),
                        () => card.Label_MouseDown(null, null), null);
                }
                if (_optionsHost != null && positionGameOptions(client, out var panel))
                {
                    // Also draw the child in the composed frame so it stays above cards,
                    // including when the graphics backend uses exclusive fullscreen.
                    var rect = new Rect(panel.X * viewport.Width / client.Right, 0,
                        panel.Width * viewport.Width / client.Right, panel.Height * viewport.Height / client.Bottom);
                    if (_windowLayer == null) layers.Add((_options, rect));
                    addTarget(rect, null, null, null);
                    bool overOptions = pointerInGame && press == null && WindowFromPoint(mouse) == _optionsHwnd;
                    _options.UpdateGameTooltip(overOptions ? pointer : null);
                }
                var hit = pointerInGame ? targets.LastOrDefault(t => t.Bounds.Contains(pointer)) : null;
                var localPointer = new Point((mouse.X - bounds.X) * viewport.Width / bounds.Width,
                    (mouse.Y - bounds.Y) * viewport.Height / bounds.Height);
                DrawingGroup cardTooltip = null;
                if (press == null && hit?.DragElement is EnemyControl hoveredCard
                    && !isGameOptionsInput(mouse.X, mouse.Y))
                {
                    var cardPointer = MainCanvas.TranslatePoint(localPointer, hoveredCard);
                    string content = hoveredCard.TooltipAt(cardPointer);
                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        cardTooltip = new DrawingGroup();
                        using var tooltip = cardTooltip.Open();
                        MiniBuffLayer.DrawTooltip(tooltip, viewport, localPointer, content, dpi.PixelsPerDip, abovePointer: true);
                    }
                }
                updateGameHover(press?.Target ?? hit);
                var outlineArea = locateOutline(press == null ? hit?.Row : null);
                // The child HWND owns its hardware cursor. Publishing another cursor
                // for the parent here made the two input threads replace each other's.
                bool hover = press != null || (hit != null && !isGameOptionsInput(mouse.X, mouse.Y));
                int cursorFrame = press == null ? 0 : 1;
                if (press?.Dragging == true)
                    cursorFrame = Environment.TickCount64 - press.DragStarted < 100 ? 44 : 45;
                bool buffHover = pointerInGame && !hover && BuffLayer.IsIconHovered;
                _gameHand.SetFrame(hover || buffHover ? cursorFrame : -1);
                var handArea = Rect.Empty;
                if ((hover || buffHover) && _gameHand.UseSoftwareCursor)
                {
                    handArea = _gameHand.AreaAt(localPointer, dpi);
                }
                if (_windowLayer != null)
                {
                    displayActorOutline(outlineArea);
                    _windowLayer.DrawPointer(handArea.IsEmpty ? null : _gameHand.Image, handArea, cardTooltip);
                    _gameTargets = targets.ToArray();
                    return;
                }
                var crop = handArea;
                crop.Union(outlineArea);
                crop.Union(CombatLayer.Frame.Bounds);
                crop.Union(BuffLayer.Frame.Bounds);
                if (cardTooltip != null) crop.Union(cardTooltip.Bounds);
                foreach (var layer in layers) crop.Union(layer.Area);
                crop.Intersect(viewport);
                if (crop.IsEmpty || crop.Width < 1 || crop.Height < 1) return;
                // Pick the pixel grid before cropping. On a wide display a hovered
                // actor can push the crop past the buffer limit; fitting that crop to
                // the buffer would resample even stationary text and options.
                int rasterWidth = Math.Min(client.Right, _maxFrameSide);
                int rasterHeight = Math.Min(client.Bottom, _maxFrameSide);
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
                    dc.DrawDrawing(BuffLayer.Frame);
                    dc.DrawDrawing(CombatLayer.Frame);
                    if (!outlineArea.IsEmpty) dc.DrawImage(_actorOutline.Image, outlineArea);
                    foreach (var (element, area) in layers)
                    {
                        var brush = new VisualBrush(element) { Stretch = Stretch.Fill };
                        dc.DrawRectangle(brush, null, area);
                    }
                    if (!handArea.IsEmpty) dc.DrawImage(_gameHand.Image, handArea);
                    if (cardTooltip != null) dc.DrawDrawing(cardTooltip);
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

        private void onHealthLost(int pid, BGEntity actor, int amount)
        {
            if (!Configuration.ShowCombatText || _hasCombatEvents || Dispatcher.HasShutdownStarted) return;
            long detected = Environment.TickCount64;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!Configuration.ShowCombatText || _hasCombatEvents || pid != _framePid || Environment.TickCount64 - detected > 500) return;
                if (!_combatProjection.TryLocate(actor, out _, out int x, out int y, out int width, out int height)) return;
                var anchor = new Point((double)x / width, (double)(y - 55) / height);
                if (anchor.X is >= 0 and <= 1 && anchor.Y is >= 0 and <= 1)
                {
                    CombatLayer.Advance(Environment.TickCount64);
                    CombatLayer.Add(new CombatImpact(actor.Id, DamageKind.Normal, amount, anchor));
                }
            }));
        }

        private void updateActorLayers()
        {
            long now = Environment.TickCount64;
            if (_combatEvents == null || now - _lastCombatFrame < 8) return;
            _lastCombatFrame = now;
            CombatLayer.Advance(now);
            _combatEvents.Poll(now, CombatLayer.Add);
            _hasCombatEvents = _combatEvents.Connected;
            bool foreground = WinApiBindings.WinAPIBindings.GetForegroundWindow() == _gameWindow;
            bool showBuffs = foreground && GetAsyncKeyState(0x12) < 0; // VK_MENU covers either Alt key.
            var viewport = Rect.Empty;
            bool needsViewport = CombatLayer.HasEffects || showBuffs || (foreground && Configuration.ShowCastingSpell);
            if (needsViewport && _insideGame)
                viewport = new Rect(MainCanvas.RenderSize);
            else if (needsViewport && _gameWindow == WinApiBindings.WinAPIBindings.GetForegroundWindow()
                && GetClientRect(_gameWindow, out var client) && client.Right > 0 && client.Bottom > 0)
            {
                var origin = new System.Drawing.Point();
                if (ClientToScreen(_gameWindow, ref origin))
                    viewport = new Rect(MainCanvas.PointFromScreen(new Point(origin.X, origin.Y)),
                        MainCanvas.PointFromScreen(new Point(origin.X + client.Right, origin.Y + client.Bottom)));
            }
            CombatLayer.Paint(viewport);
            if (foreground && (Configuration.ShowCastingSpell || showBuffs) && !viewport.IsEmpty && viewport.Width > 0 && viewport.Height > 0)
            {
                // The game simulation advances at 15 Hz. Avoid polling idle sprites
                // at the monitor refresh rate when only casting labels are enabled.
                if (!showBuffs && now - _lastOverheadFrame < 32) return;
                _lastOverheadFrame = now;
                Point? pointer = null;
                if (showBuffs)
                {
                    var cursor = System.Windows.Forms.Cursor.Position;
                    var local = MainCanvas.PointFromScreen(new Point(cursor.X, cursor.Y));
                    if (viewport.Contains(local) && MainCanvas.InputHitTest(local) == null
                        && !isGameOptionsInput(cursor.X, cursor.Y)) pointer = local;
                }
                BuffLayer.Paint(viewport, _processHacker.OverheadActors, showBuffs, pointer);
            }
            else
                BuffLayer.Clear();
            if (!_insideGame) updateDesktopBuffCursor(BuffLayer.IsIconHovered);
        }

        private void updateDesktopBuffCursor(bool hovering)
        {
            if (!hovering && _desktopCursorBeforeBuff == IntPtr.Zero) return;
            if (hovering)
                _gameHand ??= new GameHandCursor(_framePid, _processHacker.ResourceManager);
            var hand = _gameHand?.PointingHandle ?? IntPtr.Zero;
            var current = GameHandCursor.VisibleCursor;
            if (hovering && hand != IntPtr.Zero && current != IntPtr.Zero && current != hand)
            {
                _desktopCursorBeforeBuff = current;
                SetCursor(hand);
            }
            else if (!hovering && _desktopCursorBeforeBuff != IntPtr.Zero)
            {
                if (current == hand) SetCursor(_desktopCursorBeforeBuff);
                _desktopCursorBeforeBuff = IntPtr.Zero;
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
                    press.CardStart = element.RenderTransform.Value;
                    press.CardTransform = new MatrixTransform(press.CardStart);
                    element.RenderTransform = press.CardTransform;
                }
                else
                {
                    var transform = (TranslateTransform)element.RenderTransform;
                    press.PanelStartX = transform.X;
                    press.PanelBaseLeft = element.TranslatePoint(new Point(), MainCanvas).X - transform.X;
                }
            }
            if (card)
            {
                var positionTransform = press.CardStart;
                positionTransform.Translate(dx, dy);
                press.CardTransform.Matrix = positionTransform;
            }
            else
            {
                double min = -press.PanelBaseLeft;
                double max = Math.Max(min, MainCanvas.ActualWidth - element.ActualWidth + min);
                ((TranslateTransform)element.RenderTransform).X = Math.Clamp(press.PanelStartX + dx, min, max);
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
            displayActorOutline(locateOutline(_desktopHover.DataContext as EnemyListRow));
        }

        private void displayActorOutline(Rect area)
        {
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
            if (_optionsHost != null) _options.UpdateGameTooltip(null);
            _gameTargets = Array.Empty<GameTarget>();
            updateGameHover(null);
            cancelGamePress();
            _gameHand?.SetFrame(-1);
        }

        private void cancelGamePress() => saveGameDrag(Interlocked.Exchange(ref _gamePress, null));

        // Only button edges cross the hook thread. The presenter samples the latest
        // pointer position on each overlay update, even outside the original rectangle.
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
