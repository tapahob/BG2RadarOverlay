using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using BGOverlay;
using BGOverlay.Input;
using FontFamily = System.Windows.Media.FontFamily;

namespace WPFFrontend
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        ObservableCollection<EnemyListRow> EnemyTextEntries { get; set; }

        private MouseHook _mouseHook;
        private bool _toShowEnemyList = true;
        private System.Drawing.Rectangle bounds;
        private readonly object _stocksLock = new();
        private readonly ProcessHacker _processHacker = new();
        private readonly ConcurrentDictionary<int, EnemyControl> _currentEnemyControls = new();
        private readonly OptionsControl _options;

        internal void deleteEnemyControlByTag(int tag)
        {
            _currentEnemyControls.Remove(tag, out _);
        }

        public MainWindow()
        {
            InitializeComponent();

            _processHacker.ProcessDestroyed += ProcessHacker_ProcessDestroyed;
            _processHacker.ProcessHooked += ProcessHacker_ProcessHooked;

            _processHacker.Init();

            ApplyLocalization();

            updateEnemyListPosition();
            updateRadarIconPosition();

            _options = new();

            updateStyles();

            MainGrid.Children.Add(_options);
            this.MinMaxBtn.MouseEnter += MinMaxBtn_MouseEnter;
            this.MinMaxBtn.MouseLeave += MinMaxBtn_MouseLeave;

            EnemyTextEntries = new ObservableCollection<EnemyListRow>();
            BindingOperations.EnableCollectionSynchronization(EnemyTextEntries, _stocksLock);
            ListView.Items.Clear();
            ListView.ItemsSource = EnemyTextEntries;

            while (Process.GetProcessesByName(ProcessHacker.gameName).Length == 0)
            {
                Thread.Sleep(3000);
            }

            this.Closed += (o, e) =>
            {
                _mouseHook.Uninstall();
                Logger.flush();
            };
            moveToGameScreen();
            // LongRunning so this never-ending polling loop gets its own dedicated thread
            // instead of permanently occupying a ThreadPool worker.
            Task.Factory.StartNew(() =>
            {
                Logger.Debug("Main loop started");

                while (true)
                {
                    try
                    {
                        _processHacker.MainLoop();
                        foreach (var item in _processHacker.NearestEnemies)
                        {
                            updateControls(item);
                        }
                        syncEnemyList(_processHacker.NearestEnemies);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error("Main loop error!", ex);
                    }
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        private void moveToGameScreen()
        {
            this.Dispatcher.Invoke(() =>
            {
                try
                {
                    this.bounds = System.Windows.Forms.Screen.FromHandle(_processHacker.Proc.MainWindowHandle).Bounds;

                    // WindowState="Maximized" without an explicit Left/Top maximizes onto whatever
                    // monitor Windows picks at startup, which is not necessarily the game's monitor.
                    // Position and size the borderless window explicitly onto the game's monitor instead.
                    this.WindowState = WindowState.Normal;
                    this.Left        = bounds.Left;
                    this.Top         = bounds.Top;
                    this.Width       = bounds.Width;
                    this.Height      = bounds.Height;
                }
                catch (Exception ex)
                {
                    Logger.Error($"{nameof(moveToGameScreen)} error!", ex);
                }
            });
        }

        private void updateEnemyListPosition()
        {
            var transform = this.StackPanel.RenderTransform as TranslateTransform ?? new TranslateTransform();
            transform.X = Configuration.EnemyListXOffset;
            this.StackPanel.RenderTransform = transform;
        }

        private bool _isDraggingEnemyList;
        private System.Windows.Point _enemyListDragStartMouse;
        private double _enemyListDragStartOffsetX;
        // The panel's layout-only (pre-transform) left edge in window coordinates, captured
        // once when a drag starts, so clamping doesn't have to hardcode Grid column widths.
        private double _enemyListDragBaseLeft;

        private void EnemyList_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try
            {
                // Just note where the click/drag started - don't capture the mouse or mark the
                // event handled yet, so a plain click (no meaningful movement before button-up)
                // still reaches the ListView's own item-selection handling untouched.
                _isDraggingEnemyList = false;
                _enemyListDragStartMouse = e.GetPosition(this);
                _enemyListDragStartOffsetX = Configuration.EnemyListXOffset;
            }
            catch (Exception ex)
            {
                Logger.Error($"{nameof(EnemyList_PreviewMouseLeftButtonDown)} error!", ex);
            }
        }

        private void EnemyList_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            try
            {
                if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed)
                    return;

                var current = e.GetPosition(this);
                var deltaX  = current.X - _enemyListDragStartMouse.X;

                if (!_isDraggingEnemyList)
                {
                    // Horizontal-only drag, so only horizontal movement should arm it - a
                    // vertical wobble on what was meant to be a plain click on a list item
                    // shouldn't swallow that click.
                    if (Math.Abs(deltaX) < SystemParameters.MinimumHorizontalDragDistance)
                        return;

                    _isDraggingEnemyList = true;
                    this.StackPanel.CaptureMouse();

                    var transform = (TranslateTransform)this.StackPanel.RenderTransform;
                    var currentLeftInWindow = this.StackPanel.TransformToAncestor(this).Transform(new System.Windows.Point(0, 0)).X;
                    _enemyListDragBaseLeft = currentLeftInWindow - transform.X;
                }

                e.Handled = true;

                // Window-local X 0 is the game monitor's left edge and this.ActualWidth is its
                // right edge (moveToGameScreen() positions/sizes the window to exactly cover the
                // game's monitor), so clamping the panel's absolute left/right edges to
                // [0, ActualWidth] keeps it from being dragged off that monitor.
                var minOffset = -_enemyListDragBaseLeft;
                var maxOffset = this.ActualWidth - _enemyListDragBaseLeft - this.StackPanel.ActualWidth;
                if (maxOffset < minOffset)
                    maxOffset = minOffset; // panel wider than the monitor - pin it instead of inverting the clamp

                var proposedOffset = _enemyListDragStartOffsetX + deltaX;
                var clampedOffset  = Math.Max(minOffset, Math.Min(maxOffset, proposedOffset));

                ((TranslateTransform)this.StackPanel.RenderTransform).X = clampedOffset;
            }
            catch (Exception ex)
            {
                Logger.Error($"{nameof(EnemyList_PreviewMouseMove)} error!", ex);
            }
        }

        private void EnemyList_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try
            {
                if (!_isDraggingEnemyList)
                    return;

                _isDraggingEnemyList = false;
                this.StackPanel.ReleaseMouseCapture();
                e.Handled = true;

                var transform = (TranslateTransform)this.StackPanel.RenderTransform;
                Configuration.EnemyListXOffset = (int)Math.Round(transform.X);
                Configuration.SaveConfig();
            }
            catch (Exception ex)
            {
                Logger.Error($"{nameof(EnemyList_PreviewMouseLeftButtonUp)} error!", ex);
            }
        }

        private void updateRadarIconPosition()
        {
            var transform = this.MinMaxBtn.RenderTransform as TranslateTransform ?? new TranslateTransform();
            transform.X = Configuration.RadarIconXOffset;
            this.MinMaxBtn.RenderTransform = transform;
        }

        private bool _isDraggingRadarIcon;
        private System.Windows.Point _radarIconDragStartMouse;
        private double _radarIconDragStartOffsetX;
        // The button's layout-only (pre-transform) left edge in window coordinates, captured
        // once when a drag starts, so clamping doesn't have to hardcode Grid column widths.
        private double _radarIconDragBaseLeft;

        private void RadarIcon_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try
            {
                // Just note where the click/drag started - don't capture the mouse or mark the
                // event handled yet, so a plain click (no meaningful movement before button-up)
                // still reaches the button's own Click (show/hide the enemy list) untouched.
                _isDraggingRadarIcon = false;
                _radarIconDragStartMouse = e.GetPosition(this);
                _radarIconDragStartOffsetX = Configuration.RadarIconXOffset;
            }
            catch (Exception ex)
            {
                Logger.Error($"{nameof(RadarIcon_PreviewMouseLeftButtonDown)} error!", ex);
            }
        }

        private void RadarIcon_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            try
            {
                if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed)
                    return;

                var current = e.GetPosition(this);
                var deltaX  = current.X - _radarIconDragStartMouse.X;

                if (!_isDraggingRadarIcon)
                {
                    // Horizontal-only drag, so only horizontal movement should arm it - a
                    // vertical wobble on what was meant to be a plain click shouldn't swallow it.
                    if (Math.Abs(deltaX) < SystemParameters.MinimumHorizontalDragDistance)
                        return;

                    _isDraggingRadarIcon = true;
                    this.MinMaxBtn.CaptureMouse();

                    var transform = (TranslateTransform)this.MinMaxBtn.RenderTransform;
                    var currentLeftInWindow = this.MinMaxBtn.TransformToAncestor(this).Transform(new System.Windows.Point(0, 0)).X;
                    _radarIconDragBaseLeft = currentLeftInWindow - transform.X;
                }

                e.Handled = true;

                // Window-local X 0 is the game monitor's left edge and this.ActualWidth is its
                // right edge (moveToGameScreen() positions/sizes the window to exactly cover the
                // game's monitor), so clamping the button's absolute left/right edges to
                // [0, ActualWidth] keeps it from being dragged off that monitor.
                var minOffset = -_radarIconDragBaseLeft;
                var maxOffset = this.ActualWidth - _radarIconDragBaseLeft - this.MinMaxBtn.ActualWidth;
                if (maxOffset < minOffset)
                    maxOffset = minOffset; // button wider than the monitor - pin it instead of inverting the clamp

                var proposedOffset = _radarIconDragStartOffsetX + deltaX;
                var clampedOffset  = Math.Max(minOffset, Math.Min(maxOffset, proposedOffset));

                ((TranslateTransform)this.MinMaxBtn.RenderTransform).X = clampedOffset;
            }
            catch (Exception ex)
            {
                Logger.Error($"{nameof(RadarIcon_PreviewMouseMove)} error!", ex);
            }
        }

        private void RadarIcon_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try
            {
                if (!_isDraggingRadarIcon)
                    return;

                _isDraggingRadarIcon = false;
                this.MinMaxBtn.ReleaseMouseCapture();
                e.Handled = true;

                var transform = (TranslateTransform)this.MinMaxBtn.RenderTransform;
                Configuration.RadarIconXOffset = (int)Math.Round(transform.X);
                Configuration.SaveConfig();
            }
            catch (Exception ex)
            {
                Logger.Error($"{nameof(RadarIcon_PreviewMouseLeftButtonUp)} error!", ex);
            }
        }

        private void ProcessHacker_ProcessHooked(string processName, int pid)
        {
            // The game may have (re)started on a different monitor than last time.
            moveToGameScreen();

            // Initialize mouse hook.

            _mouseHook = new MouseHook(pid, MouseMessageTypes.Click);

            Logger.Debug("Initializing mouse hook");

            _mouseHook.AddHandler(MouseMessageCode.RightButtonDown, MouseHook_MouseEvent);
            _mouseHook.InstallAsync().GetAwaiter().GetResult();
            
            Logger.Debug("Mouse hooks installed");
        }

        private void ProcessHacker_ProcessDestroyed(string processName, int pid)
        {
            EnemyTextEntries.Clear();
            _mouseHook.Uninstall();
        }

        // Not text - a locale can widen EnemyControl/OptionsControl for languages whose
        // translated labels need more room than English does. Kept out of the plain string
        // resources below so they stay doubles (App.xaml's defaults are too); Width can't bind
        // to a string DynamicResource.
        private static readonly string[] WidthResourceKeys = { "EnemyControlWidth", "OptionsControlWidth" };

        /// <summary>
        /// (Re)loads Configuration.Locale's string table and pushes it into Application.Resources.
        /// Safe to call again after the constructor - e.g. when the user picks a different
        /// locale in OptionsControl - since every DynamicResource-bound Content/Width in
        /// EnemyControl and OptionsControl picks up the change immediately, the same way
        /// changing fonts already does.
        /// </summary>
        public static void ApplyLocalization()
        {
            Logger.Debug($"Loading locale strings for '{Configuration.Locale}' ..");
            RadarLocalization.Init(Configuration.Locale);
            var app = System.Windows.Application.Current;
            foreach (var entry in RadarLocalization.Strings)
            {
                if (Array.IndexOf(WidthResourceKeys, entry.Key) >= 0)
                    continue;
                app.Resources[entry.Key] = entry.Value;
            }

            // Only override a given control's width when the locale actually specifies a valid
            // number for it - otherwise that control keeps using App.xaml's default/fallback
            // width (the control's previous fixed width).
            foreach (var key in WidthResourceKeys)
            {
                if (RadarLocalization.Strings.TryGetValue(key, out var widthText)
                    && double.TryParse(widthText, NumberStyles.Float, CultureInfo.InvariantCulture, out var width))
                {
                    app.Resources[key] = width;
                }
            }
            Logger.Debug("Done!");
        }

        private void updateStyles()
        {
            Logger.Debug("Updating App Styles ..");
            var app                         = System.Windows.Application.Current;
            app.Resources["FontFamily1"]    = new FontFamily(Configuration.Font1);
            app.Resources["FontFamily2"]    = new FontFamily(Configuration.Font2);
            app.Resources["FontFamilyBuff"] = new FontFamily(Configuration.Font3);
            app.Resources["FontSize1"]      = Convert.ToDouble(Configuration.FontSize1);
            app.Resources["FontSize2"]      = Convert.ToDouble(Configuration.FontSize2);
            app.Resources["FontSize3Big"]   = Convert.ToDouble(Configuration.FontSize3Big);
            app.Resources["FontSize3Small"] = Convert.ToDouble(Configuration.FontSize3Small);
            Logger.Debug("Done!");
        }

        private void updateControls(BGEntity item)
        {
            this.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (_currentEnemyControls.TryGetValue(item.tag, out EnemyControl enemyControl))
                        enemyControl.updateView(item);

                } catch (Exception ex)
                {
                    Logger.Error($"{nameof(updateControls)} error!", ex);
                }
            }));
        }

        /// <summary>
        /// Updates EnemyTextEntries (the nearby-enemies ListView's items) to match
        /// nearestEnemies, by BGEntity.tag - updating existing rows' Name/CurrentHP in place
        /// rather than replacing the collection wholesale. This has to run on the UI thread since
        /// EnemyListRow's property setters raise INotifyPropertyChanged.PropertyChanged, which
        /// WPF's binding system expects to happen on the thread that owns the bound elements
        /// (unlike collection Add/Remove, which BindingOperations.EnableCollectionSynchronization
        /// already lets happen off-thread).
        ///
        /// Updating in place instead of the previous Clear()+re-Add() every tick keeps each row's
        /// ListViewItem/HpRollText instance alive across ticks, which is what lets HpRollText's
        /// roll-down animation see an actual old-to-new HP transition instead of a freshly
        /// re-templated row with no memory of the previous value.
        /// </summary>
        private void syncEnemyList(List<BGEntity> nearestEnemies)
        {
            this.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    var seenTags = new HashSet<int>();
                    foreach (var entity in nearestEnemies)
                    {
                        seenTags.Add(entity.tag);
                        var row = EnemyTextEntries.FirstOrDefault(r => r.Tag == entity.tag);
                        if (row == null)
                        {
                            EnemyTextEntries.Add(new EnemyListRow(entity.tag, entity.Name2, entity.CurrentHP));
                        }
                        else
                        {
                            // These setters no-op when the value hasn't actually changed, so this
                            // is cheap even though it runs every tick for every visible row.
                            row.Name = entity.Name2;
                            row.CurrentHP = entity.CurrentHP;
                        }
                    }

                    // Remove rows for enemies that left the nearest-enemies set (died, moved out
                    // of view, etc.) - snapshot via ToList() since we're removing from the live
                    // collection while iterating.
                    foreach (var row in EnemyTextEntries.Where(r => !seenTags.Contains(r.Tag)).ToList())
                    {
                        EnemyTextEntries.Remove(row);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"{nameof(syncEnemyList)} error!", ex);
                }
            }));
        }

        private void MinMaxBtn_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            ThicknessAnimation anim = new ThicknessAnimation();
            anim.From               = this.MinMaxBtn.Margin;
            var newMargin           = this.MinMaxBtn.Margin;
            newMargin.Top           = -15;
            anim.To                 = newMargin;
            anim.Duration           = TimeSpan.FromSeconds(.5);
            anim.EasingFunction     = new BackEase() { Amplitude = .7, EasingMode = EasingMode.EaseOut };
            anim.FillBehavior       = FillBehavior.HoldEnd;
            this.MinMaxBtn.Opacity = .25;
            this.MinMaxBtn.BeginAnimation(Button.MarginProperty, anim, HandoffBehavior.SnapshotAndReplace);            
        }

        private void MinMaxBtn_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            ThicknessAnimation anim = new ThicknessAnimation();
            anim.From               = this.MinMaxBtn.Margin;
            var newMargin           = this.MinMaxBtn.Margin;
            newMargin.Top           = -5;
            anim.To                 = newMargin;
            this.MinMaxBtn.Opacity  = 1;
            //anim.EasingFunction   = new BackEase() { Amplitude = .7, EasingMode = EasingMode.EaseIn };
            anim.Duration           = TimeSpan.FromSeconds(.15);
            anim.FillBehavior       = FillBehavior.HoldEnd;
            this.MinMaxBtn.BeginAnimation(Button.MarginProperty, anim, HandoffBehavior.SnapshotAndReplace);            
        }

        private void changeEnemyControlStateByEntity(BGEntity bgEntity)
        {
            if (bgEntity == null)
                return;

            try
            {
                EnemyControl enemyControl;
                if (!_currentEnemyControls.TryGetValue(bgEntity.tag, out enemyControl))
                {
                    enemyControl = new EnemyControl(bgEntity, this);
                    _currentEnemyControls[bgEntity.tag] = enemyControl;
                    MainCanvas.Children.Add(enemyControl);
                }
                else
                {
                    _currentEnemyControls[bgEntity.tag].Label_MouseDown(null, null);
                    _currentEnemyControls.Remove(bgEntity.tag, out enemyControl);
                }
            } catch (Exception ex)
            {
                Logger.Error($"{nameof(changeEnemyControlStateByEntity)} error!", ex);
            }
        }

        private void MouseHook_MouseEvent(object sender, MouseMessageEventArgs e)
        {
            
            this.Dispatcher.BeginInvoke(new Action(() =>
            {                
                try
                {
                    if (e.Shift != Configuration.UseShiftClick)
                    {
                        return;
                    }

                    if (e.MessageCode != (int)MouseMessageCode.RightButtonDown)
                        return;

                    BGEntity entry = null;

                    entry = _processHacker.entityList.FirstOrDefault(
                        x => x.X > 0 &&
                        Math.Abs(x.MousePosX + x.MousePosX1 - x.X) < 18
                        && Math.Abs(x.MousePosY + x.MousePosY1 - x.Y) < 18);

                    if (entry == null)
                        return;

                    var a = e.X;
                    var b = e.Y;
                    // e.X/e.Y are absolute virtual-desktop coordinates, so the game's monitor
                    // offset (bounds.Left/Top) has to be added back on top of the in-monitor
                    // fraction - otherwise this only lines up when the game sits at (0,0).
                    var xx = bounds.Left + bounds.Width * (entry.MousePosX) / entry.ViewportWidth;
                    var yy = bounds.Top + bounds.Height * (entry.MousePosY) / entry.ViewportHeight;

                    if (Math.Abs(e.X - xx) < 18 
                    && Math.Abs(e.Y - yy) < 18)
                    {
                        changeEnemyControlStateByEntity(entry);
                    }
                    
                } catch (Exception ex)
                {
                    Logger.Error($"{nameof(MouseHook_MouseEvent)} Mouse Event Error!", ex);
                }
            }));
        }

        private void ListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var list = (sender as ListView);
            if (list.SelectedIndex == -1) return;
            var row = (EnemyListRow)ListView.SelectedItem;
            if (row == null) return;

            // The ListView only carries the lightweight EnemyListRow view-model (name/HP for
            // display) - look the full, current BGEntity back up by tag for changeEnemyControlStateByEntity,
            // the same way MouseHook_MouseEvent already does for its own right-click hit test.
            var content = _processHacker.entityList.FirstOrDefault(x => x.tag == row.Tag);
            if (content == null) return;

            //var bounds = ph.getScreenDimensions();
            //var x = bounds.Size.Width * (content.X - content.MousePosX1) / content.ViewportWidth;
            //var y = bounds.Size.Height * (content.Y - content.MousePosY1) / content.ViewportHeight;

            //DebugPointer.Margin = new Thickness(x, y, 0, 0);

            this.changeEnemyControlStateByEntity(content);
            list.SelectedIndex = -1;
        }

        private void MinMaxBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                WinApiBindings.WinAPIBindings.SetForegroundWindow(Configuration.HWndPtr);
                WinApiBindings.WinAPIBindings.SetFocus(Configuration.HWndPtr);
                _toShowEnemyList = !_toShowEnemyList;
                //this.listView.Visibility = this.listView.Visibility == Visibility.Collapsed ? Visibility.Visible : Visibility.Collapsed;            

                if (!_toShowEnemyList)
                {
                    ThicknessAnimation anim = new ThicknessAnimation();
                    anim.From = this.StackPanel.Margin;
                    var newMargin = this.StackPanel.Margin;
                    newMargin.Top = -this.StackPanel.ActualHeight;
                    anim.To = newMargin;
                    anim.EasingFunction = new BackEase() { Amplitude = .3, EasingMode = EasingMode.EaseIn };
                    anim.Duration = TimeSpan.FromSeconds(.45);
                    anim.FillBehavior = FillBehavior.HoldEnd;
                    anim.Completed += (o, e) => this.StackPanel.Visibility = Visibility.Collapsed;
                    this.StackPanel.BeginAnimation(StackPanel.MarginProperty, anim, HandoffBehavior.SnapshotAndReplace);
                }
                else
                {
                    ThicknessAnimation anim = new ThicknessAnimation();
                    var newMargin1 = this.StackPanel.Margin;
                    newMargin1.Top /= 2;
                    anim.From = newMargin1;
                    var newMargin = this.StackPanel.Margin;
                    newMargin.Top = 0;
                    anim.To = newMargin;
                    anim.EasingFunction = new PowerEase() { Power = 10, EasingMode = EasingMode.EaseOut };
                    anim.Duration = TimeSpan.FromSeconds(.85);
                    anim.FillBehavior = FillBehavior.HoldEnd;
                    this.StackPanel.Visibility = Visibility.Visible;
                    this.StackPanel.BeginAnimation(StackPanel.MarginProperty, anim, HandoffBehavior.SnapshotAndReplace);
                }
            } catch (Exception ex)
            {
                Logger.Error($"{nameof(MinMaxBtn_Click)} Min/Max error!", ex);
            }
            
            
        }

        private void MinMaxBtn_MouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try
            {
                _options.Init();
                _options.Show();
            }
            catch (Exception ex)
            {
                Logger.Error($"{nameof(MinMaxBtn_MouseRightButtonDown)} Options error!", ex);
            }            
        }
    }
}
