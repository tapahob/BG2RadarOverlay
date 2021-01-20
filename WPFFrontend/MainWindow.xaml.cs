using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
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
            RenderTransform = new TranslateTransform();

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
                    Thread.Sleep(500);
                }
            });
            
        
        }

        private void TextBlock_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            TextBlock obj = (TextBlock)sender;
            BGEntity context = (BGEntity)obj.DataContext;

            MainGrid.Children.Add(new EnemyControl(context, MainCanvas));
        }
    }
}
