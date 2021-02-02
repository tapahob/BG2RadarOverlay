using BGOverlay;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
//using System.Windows.Media;

namespace WPFFrontend
{
    /// <summary>
    /// Interaction logic for EnemyControl.xaml
    /// </summary>
    public partial class EnemyControl : UserControl
    {
        public EnemyControl(BGEntity bgEntity)
        {
            InitializeComponent();
            this.BGEntity = bgEntity;
            this.DataContext = bgEntity;  
            if (this.BGEntity.Protections.Count > 0)
            {
                this.protectionsListView.Visibility = Visibility.Visible;
            }
        }

        public BGEntity BGEntity { get; }

        private void Label_MouseDown(object sender, MouseButtonEventArgs e)
        {
            (this.Parent as Grid)?.Children.Remove(this);
        }
    }
}
