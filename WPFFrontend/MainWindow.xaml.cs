using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;

using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
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
        
        
        public MainWindow()
        {
            InitializeComponent();
            MouseHook.MouseEvent += MouseHook_MouseEvent;
            MouseHook.InstallHook();
            

            EnemyTextEntries = new ObservableCollection<BGEntity>();
            BindingOperations.EnableCollectionSynchronization(EnemyTextEntries, _stocksLock);
            listView.Items.Clear();
            listView.ItemsSource = EnemyTextEntries;

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
                        continue;
                    EnemyTextEntries.Clear();
                foreach (var item in ph.NearestEnemies)
                    {
                        EnemyTextEntries.Add(item);
                    }
                    
                }
            });
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
            MainGrid.Children.Add(new EnemyControl(entry));
        }

        private void listView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var list = (sender as ListView);
            if (list.SelectedIndex == -1) return;
            var content = (BGEntity)listView.SelectedItem;
            if (content == null) return;
            MainGrid.Children.Add(new EnemyControl(content));
            list.SelectedIndex = -1;
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            
        }
    }
}
