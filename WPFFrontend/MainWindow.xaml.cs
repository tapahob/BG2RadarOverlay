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
using System.Windows.Media.Animation;
using BGOverlay;

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

        internal void deleteMe(int tag)
        {
            EnemyControl trash;
            currentControls.Remove(tag, out trash);
        }

        public MainWindow()
        {
            InitializeComponent();
            MouseHook.MouseEvent += MouseHook_MouseEvent;
            MouseHook.InstallHook();

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
            
            Task.Factory.StartNew(() =>
            {
                ph = new ProcessHacker();
                ph.Init();
                
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
                        //updateControls();
                    }                    
                }
            });
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
            this.MinMaxBtn.Opacity = .05;
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

        private void MouseHook_MouseEvent()
        {
            BGEntity entry = null;
            
            entry = ph.entityList.FirstOrDefault(
                x => x.X > 0 &&
                Math.Abs(x.MousePosX + x.MousePosX1 - x.X) < 18
                && Math.Abs(x.MousePosY + x.MousePosY1 - x.Y) < 18);

            if (entry == null)
            {
                return;
            }
            var newControl = new EnemyControl(entry, this);
            MainWindow.currentControls[entry.tag] = newControl;
            MainGrid.Children.Add(newControl);
        }

        private void listView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var list = (sender as ListView);
            if (list.SelectedIndex == -1) return;
            var content = (BGEntity)ListView.SelectedItem;
            if (content == null) return;
            var newControl = new EnemyControl(content, this);
            MainWindow.currentControls[content.tag] = newControl;
            MainGrid.Children.Add(newControl);
            list.SelectedIndex = -1;
        }
        bool toShow = true;
        private void MinMaxBtn_Click(object sender, RoutedEventArgs e)
        {
            toShow = !toShow;
            //this.listView.Visibility = this.listView.Visibility == Visibility.Collapsed ? Visibility.Visible : Visibility.Collapsed;
            
            
            if (!toShow)
            {                
                ThicknessAnimation anim = new ThicknessAnimation();
                anim.From = this.StackPanel.Margin;
                var newMargin = this.StackPanel.Margin;
                newMargin.Top = -this.StackPanel.ActualHeight;
                anim.To = newMargin;
                anim.EasingFunction = new BackEase() { Amplitude = .3, EasingMode = EasingMode.EaseIn };
                anim.Duration = TimeSpan.FromSeconds(.45);
                anim.FillBehavior = FillBehavior.HoldEnd;
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
                this.StackPanel.BeginAnimation(StackPanel.MarginProperty, anim, HandoffBehavior.SnapshotAndReplace);
            }
            
        }
    }
}
