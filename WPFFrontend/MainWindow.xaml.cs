using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;

using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using BGOverlay;
using Winook;

namespace WPFFrontend
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        ObservableCollection<BGEntity> EnemyTextEntries { get; set; }

        private MouseHook _mouseHook;
        private readonly object _stocksLock = new();
        private readonly ProcessHacker _processHacker = new();
        private readonly ConcurrentDictionary<int, EnemyControl> _currentEnemyControls = new();
        private readonly OptionsControl _options = new();
        private bool _toShowEnemyList = true;

        internal void DeleteEnemyControlByTag(int tag)
        {
            _currentEnemyControls.Remove(tag, out _);
        }

        public MainWindow()
        {
            InitializeComponent();
            this.MainGrid.ColumnDefinitions[0].Width = new GridLength(130 + Configuration.EnemyListXOffset);
            
            _processHacker.ProcessDestroyed += ProcessHacker_ProcessDestroyed;
            _processHacker.ProcessHooked += ProcessHacker_ProcessHooked;

            _processHacker.Init();
            
            UpdateStyles();

            MainGrid.Children.Add(_options);   
            this.MinMaxBtn.MouseEnter += MinMaxBtn_MouseEnter;
            this.MinMaxBtn.MouseLeave += MinMaxBtn_MouseLeave;            

            EnemyTextEntries = new ObservableCollection<BGEntity>();
            BindingOperations.EnableCollectionSynchronization(EnemyTextEntries, _stocksLock);
            ListView.Items.Clear();
            ListView.ItemsSource = EnemyTextEntries;

            while (Process.GetProcessesByName("Baldur").Length == 0)
            {
                Thread.Sleep(3000);
            }

            this.Closed += (o, e) =>
            {
                _mouseHook.Uninstall();
                Logger.flush();
            };

            Task.Factory.StartNew(() =>
            {
                Logger.Debug("Main loop started");
                
                while (true)
                {
                    try
                    {
                        _processHacker.MainLoop();
                        if (_processHacker.NearestEnemies.Count() == EnemyTextEntries.Count && _processHacker.NearestEnemies.All(x => EnemyTextEntries.Any(y => y.ToString() == x.ToString())))
                        {
                            foreach (var item in _processHacker.NearestEnemies)
                            {
                                UpdateControls(item);
                            }
                            continue;
                        }

                        EnemyTextEntries.Clear();
                        foreach (var item in _processHacker.NearestEnemies)
                        {
                            EnemyTextEntries.Add(item);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Error("Main loop error!", ex);
                    }
                } 
            });
        }

        private void ProcessHacker_ProcessHooked(string processName, int pid)
        {
            // Initialize mouse hook.

            _mouseHook = new MouseHook(pid, MouseMessageTypes.Click);

            Logger.Debug("Initializing mouse hook");

            _mouseHook.AddHandler(MouseMessageCode.RightButtonUp, MouseHook_MouseEvent);
            _mouseHook.InstallAsync().GetAwaiter().GetResult();
            
            Logger.Debug("Mouse hooks installed");
        }

        private void ProcessHacker_ProcessDestroyed(string processName, int pid)
        {
            EnemyTextEntries.Clear();
            _mouseHook.Uninstall();
        }

        private void UpdateStyles()
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

        private void UpdateControls(BGEntity item)
        {
            this.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (_currentEnemyControls.TryGetValue(item.tag, out EnemyControl enemyControl))
                        enemyControl.updateView(item);

                } catch (Exception ex)
                {
                    Logger.Error($"{nameof(UpdateControls)} error!", ex);
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

        private void RemoveAllEnemyControls()
        {
            EnemyControl enemyControl;

            try
            {
                foreach (int key in _currentEnemyControls.Keys)
                {
                    enemyControl = _currentEnemyControls[key];
                    enemyControl.Label_MouseDown(null, null);

                    MainCanvas.Children.Remove(enemyControl);
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"{nameof(RemoveAllEnemyControls)} Error!", ex);
            }
        }

        private void ChangeEnemyControlStateByEntity(BGEntity bgEntity)
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
                Logger.Error($"{nameof(ChangeEnemyControlStateByEntity)} error!", ex);
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

                    if (e.MessageCode != (int)MouseMessageCode.RightButtonUp)
                        return;

                    BGEntity entry = null;

                    entry = _processHacker.entityList.FirstOrDefault(
                        x => x.X > 0 &&
                        Math.Abs(x.MousePosX + x.MousePosX1 - x.X) < 18
                        && Math.Abs(x.MousePosY + x.MousePosY1 - x.Y) < 18);

                    if (entry == null && Configuration.CloseWithRightClick)
                    {
                        this.RemoveAllEnemyControls();
                        return;
                    }

                    ChangeEnemyControlStateByEntity(entry);
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
            var content = (BGEntity)ListView.SelectedItem;
            if (content == null) return;

            //var bounds = ph.getScreenDimensions();
            //var x = bounds.Size.Width * (content.X - content.MousePosX1) / content.ViewportWidth;
            //var y = bounds.Size.Height * (content.Y - content.MousePosY1) / content.ViewportHeight;

            //DebugPointer.Margin = new Thickness(x, y, 0, 0);

            this.ChangeEnemyControlStateByEntity(content);
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
