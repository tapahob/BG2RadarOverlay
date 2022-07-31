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

        private object _stocksLock = new object();
        private ProcessHacker ph;
        private static ConcurrentDictionary<int, EnemyControl> currentControls = new ConcurrentDictionary<int, EnemyControl>();
        private OptionsControl options;

        internal void deleteMe(int tag)
        {
            EnemyControl trash;
            currentControls.Remove(tag, out trash);
        }

        public MainWindow()
        {
            InitializeComponent();
            ph = new ProcessHacker();            
            ph.Init();
            UpdateStyles();
            options = new OptionsControl();
            MainGrid.Children.Add(options);   
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
            ph.ProcessCreated += ProcessLostHandler;
            var proc = Process.GetProcessesByName("Baldur")[0];
            var hook = new MouseHook(proc.Id, MouseMessageTypes.Click);
            Logger.Debug("Initializing mouse hooks");
            hook.AddHandler(MouseMessageCode.RightButtonUp, MouseHook_MouseEvent);
            hook.InstallAsync();            
            this.Closed += (o, e) =>
            {
                hook.Uninstall();
                Logger.flush();
            };
            Logger.Debug("Mouse hooks installed");
            Task.Factory.StartNew(() =>
            {
                Logger.Debug("Main loop started");
                
                while (true)
                {
                    try
                    {
                        ph.MainLoop();
                        if (ph.NearestEnemies.Count() == EnemyTextEntries.Count && ph.NearestEnemies.All(x => EnemyTextEntries.Any(y => y.ToString() == x.ToString())))
                        {
                            foreach (var item in ph.NearestEnemies)
                            {
                                updateControls(item);
                            }
                            continue;
                        }

                        EnemyTextEntries.Clear();
                        foreach (var item in ph.NearestEnemies)
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

        private void ProcessLostHandler(object sender, EventArgs e)
        {
            EnemyTextEntries.Clear();
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

        private void updateControls(BGEntity item)
        {
            this.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (currentControls.ContainsKey(item.tag))
                        currentControls[item.tag].updateView(item);
                } catch (Exception ex)
                {
                    Logger.Error("Update Controls error!", ex);
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

        private void removeAll()
        {
            EnemyControl enemyControl;
            try
            {
                foreach (int key in MainWindow.currentControls.Keys)
                {
                    enemyControl = MainWindow.currentControls[key];
                    enemyControl.Label_MouseDown(null, null);

                    MainCanvas.Children.Remove(enemyControl);
                }
            } catch (Exception ex)
            {
                Logger.Error("RemoveAll Error!", ex);
            }
            
        }

        private void addOrRemove(BGEntity bgEntity)
        {
            if (bgEntity == null)
                return;

            try
            {
                EnemyControl enemyControl;
                if (!MainWindow.currentControls.TryGetValue(bgEntity.tag, out enemyControl))
                {
                    enemyControl = new EnemyControl(bgEntity, this);
                    MainWindow.currentControls[bgEntity.tag] = enemyControl;
                    MainCanvas.Children.Add(enemyControl);
                }
                else
                {
                    MainWindow.currentControls[bgEntity.tag].Label_MouseDown(null, null);
                    MainWindow.currentControls.Remove(bgEntity.tag, out enemyControl);
                }
            } catch (Exception ex)
            {
                Logger.Error("AddOrRemove error!", ex);
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

                    entry = ph.entityList.FirstOrDefault(
                        x => x.X > 0 &&
                        Math.Abs(x.MousePosX + x.MousePosX1 - x.X) < 18
                        && Math.Abs(x.MousePosY + x.MousePosY1 - x.Y) < 18);

                    if (entry == null && Configuration.CloseWithRightClick)
                    {
                        this.removeAll();
                        return;
                    }

                    addOrRemove(entry);
                } catch (Exception ex)
                {
                    Logger.Error("Mouse Event Error!", ex);
                }                
            }));            
        }

        private void listView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var list = (sender as ListView);
            if (list.SelectedIndex == -1) return;
            var content = (BGEntity)ListView.SelectedItem;
            if (content == null) return;

            //var bounds = ph.getScreenDimensions();
            //var x = bounds.Size.Width * (content.X - content.MousePosX1) / content.ViewportWidth;
            //var y = bounds.Size.Height * (content.Y - content.MousePosY1) / content.ViewportHeight;

            //DebugPointer.Margin = new Thickness(x, y, 0, 0);

            this.addOrRemove(content);
            list.SelectedIndex = -1;
        }
        bool toShowEnemyList = true;
        private MouseHook mouseHook;

        private void MinMaxBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                WinApiBindings.WinAPIBindings.SetForegroundWindow(Configuration.HWndPtr);
                WinApiBindings.WinAPIBindings.SetFocus(Configuration.HWndPtr);
                toShowEnemyList = !toShowEnemyList;
                //this.listView.Visibility = this.listView.Visibility == Visibility.Collapsed ? Visibility.Visible : Visibility.Collapsed;            

                if (!toShowEnemyList)
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
                Logger.Error("Min/Max error!", ex);
            }
            
            
        }

        private void MinMaxBtn_MouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try
            {
                options.Init();
                options.Show();
            } catch (Exception ex)
            {
                Logger.Error("Options error!", ex);
            }            
        }
    }
}
