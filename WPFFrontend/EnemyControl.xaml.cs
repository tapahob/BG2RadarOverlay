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
        public EnemyControl(BGEntity bgEntity, Canvas canvas)
        {
            InitializeComponent();
            this.canvas = canvas;
            this.BGEntity = bgEntity;
            this.DataContext = bgEntity;            
        }

        private Canvas canvas;

        public BGEntity BGEntity { get; }
        public Point CurrentMousePosition { get; private set; }
        public Point StartMousePosition { get; private set; }

        private void UserControl_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {


        }

        private void UserControl_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {

        }

        private void UserControl_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {

        }

        private void Label_MouseDown(object sender, MouseButtonEventArgs e)
        {
            (this.Parent as Grid).Children.Remove(this);
        }
    }
}
