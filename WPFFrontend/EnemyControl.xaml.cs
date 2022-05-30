using BGOverlay;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
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
            this.BGEntity.LoadDerivedStats();
            this.BGEntity.loadTimedEffects();
            this.DataContext = bgEntity;

            if (this.BGEntity.SpellProtection.Count > 0)
            {
                this.BGEntity.SpellProtection.ForEach(x =>
                {
                    this.BuffStack.Children.Add(new Image() { Margin = new Thickness(0, 0, 3, 0), ToolTip = x.Item1, Source = new BitmapImage(new Uri($"icons/{x.Item2}00001.png", UriKind.Relative)), MaxHeight = 24 });
                });
                this.BuffStack.Visibility = Visibility.Visible;
            }

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
