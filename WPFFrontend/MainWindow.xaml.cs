using System.Collections.ObjectModel;
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

        public MainWindow()
        {
            InitializeComponent();

            EnemyTextEntries = new ObservableCollection<BGEntity>();
            BindingOperations.EnableCollectionSynchronization(EnemyTextEntries, _stocksLock);
            listView.Items.Clear();
            listView.ItemsSource = EnemyTextEntries;
            Task.Factory.StartNew(() =>
            {
                var ph = new ProcessHacker();
                ph.Init();

                while (true)
                {
                    ph.MainLoop();
                    EnemyTextEntries.Clear();
                foreach (var item in ph.NearestEnemies)
                    {
                        EnemyTextEntries.Add(item);
                    }
                }
            });
        }

        private void listView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var content = (BGEntity)listView.SelectedItem;
            if (content == null) return;
            MainGrid.Children.Add(new EnemyControl(content));
        }
    }
}
