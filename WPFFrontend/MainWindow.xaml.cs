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
        private object _stocksLock2 = new object();
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
            var proc = Process.GetProcessesByName("Baldur")[0];
            var hook = new MouseHook(proc.Id, MouseMessageTypes.Click);

            hook.AddHandler(MouseMessageCode.RightButtonUp, MouseHook_MouseEvent);
            hook.InstallAsync();
            this.Closed += (o, e) =>
            {
                hook.Uninstall();
            };

            Task.Factory.StartNew(() =>
            {
                while (true)
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
            });
        }

        private void UpdateStyles()
        {
            var app                         = System.Windows.Application.Current;
            app.Resources["FontFamily1"]    = new FontFamily(Configuration.Font1);
            app.Resources["FontFamily2"]    = new FontFamily(Configuration.Font2);
            app.Resources["FontFamilyBuff"] = new FontFamily(Configuration.Font3);
            app.Resources["FontSize1"]      = Convert.ToDouble(Configuration.FontSize1);
            app.Resources["FontSize2"]      = Convert.ToDouble(Configuration.FontSize2);
            app.Resources["FontSize3Big"]   = Convert.ToDouble(Configuration.FontSize3Big);
            app.Resources["FontSize3Small"] = Convert.ToDouble(Configuration.FontSize3Small);
        }

        private void updateControls(BGEntity item)
        {
            this.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (currentControls.ContainsKey(item.tag))
                    currentControls[item.tag].updateView(item);
            }));
            
        }

        private void MinMaxBtn_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            ThicknessAnimation anim = new ThicknessAnimation();
            anim.From = this.MinMaxBtn.Margin;
            var newMargin = this.MinMaxBtn.Margin;
            newMargin.Top = -15;
            anim.To = newMargin;
            anim.Duration = TimeSpan.FromSeconds(.5);
            anim.EasingFunction = new BackEase() { Amplitude = .7, EasingMode = EasingMode.EaseOut };
            anim.FillBehavior = FillBehavior.HoldEnd;
            this.MinMaxBtn.BeginAnimation(Button.MarginProperty, anim, HandoffBehavior.SnapshotAndReplace);
            this.MinMaxBtn.Opacity = .25;
        }

        private void MinMaxBtn_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            ThicknessAnimation anim = new ThicknessAnimation();
            anim.From = this.MinMaxBtn.Margin;
            var newMargin = this.MinMaxBtn.Margin;
            newMargin.Top = -5;
            anim.To = newMargin;
            this.MinMaxBtn.Opacity = 1;
            //anim.EasingFunction = new BackEase() { Amplitude = .7, EasingMode = EasingMode.EaseIn };
            anim.Duration = TimeSpan.FromSeconds(.15);
            anim.FillBehavior = FillBehavior.HoldEnd;
            this.MinMaxBtn.BeginAnimation(Button.MarginProperty, anim, HandoffBehavior.SnapshotAndReplace);            
        }

        private void addOrRemove(BGEntity bgEntity, int left)
        {
            EnemyControl enemyControl;
            if (!MainWindow.currentControls.TryGetValue(bgEntity.tag, out enemyControl))
            {
                enemyControl = new EnemyControl(bgEntity, this, left);
                MainWindow.currentControls[bgEntity.tag] = enemyControl;
                MainGrid.Children.Add(enemyControl);
            }
            else
            {
                MainWindow.currentControls[bgEntity.tag].Label_MouseDown(null, null);

                MainWindow.currentControls.Remove(bgEntity.tag, out enemyControl);
            }
        }
        
        private int left(BGEntity entity)
        {
            if (entity.MousePosX > 650)
            {
                return (int)((entity.MousePosX - 650) * 1.53) - 200;
            }
            else
            {
                return -(int)((650 - entity.MousePosX) * 1.53) + 500;
            }
        }
        
        private void MouseHook_MouseEvent(object sender, MouseMessageEventArgs e)
        {
            this.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (e.MessageCode != (int)MouseMessageCode.RightButtonUp)
                    return;

                BGEntity entry = null;

                entry = ph.entityList.FirstOrDefault(
                    x => x.X > 0 &&
                    Math.Abs(x.MousePosX + x.MousePosX1 - x.X) < 18
                    && Math.Abs(x.MousePosY + x.MousePosY1 - x.Y) < 18);

                if (entry == null)
                {
                    return;
                }

                int left = this.left(entry);

                addOrRemove(entry, left);
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

            this.addOrRemove(content, 0);
            list.SelectedIndex = -1;
        }
        bool toShowEnemyList = true;
        private MouseHook mouseHook;

        private void MinMaxBtn_Click(object sender, RoutedEventArgs e)
        {                        
            toShowEnemyList = !toShowEnemyList;
            //this.listView.Visibility = this.listView.Visibility == Visibility.Collapsed ? Visibility.Visible : Visibility.Collapsed;            
            
            if (!toShowEnemyList)
            {                
                ThicknessAnimation anim = new ThicknessAnimation();
                anim.From               = this.StackPanel.Margin;
                var newMargin           = this.StackPanel.Margin;
                newMargin.Top           = -this.StackPanel.ActualHeight;
                anim.To                 = newMargin;
                anim.EasingFunction     = new BackEase() { Amplitude = .3, EasingMode = EasingMode.EaseIn };
                anim.Duration           = TimeSpan.FromSeconds(.45);
                anim.FillBehavior       = FillBehavior.HoldEnd;
                anim.Completed += (o, e) => this.StackPanel.Visibility = Visibility.Collapsed;
                this.StackPanel.BeginAnimation(StackPanel.MarginProperty, anim, HandoffBehavior.SnapshotAndReplace);
            }
            else
            {
                ThicknessAnimation anim = new ThicknessAnimation();
                var newMargin1          = this.StackPanel.Margin;
                newMargin1.Top         /= 2;
                anim.From               = newMargin1;                
                var newMargin           = this.StackPanel.Margin;
                newMargin.Top           = 0;
                anim.To                 = newMargin;
                anim.EasingFunction     = new PowerEase() { Power = 10, EasingMode = EasingMode.EaseOut };                
                anim.Duration           = TimeSpan.FromSeconds(.85);
                anim.FillBehavior       = FillBehavior.HoldEnd;
                this.StackPanel.Visibility = Visibility.Visible;
                this.StackPanel.BeginAnimation(StackPanel.MarginProperty, anim, HandoffBehavior.SnapshotAndReplace);
            }
            
        }

        private void MinMaxBtn_MouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            options.Init();
            options.Show();
        }
    }
}
