using BGOverlay;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace WPFFrontend
{
    /// <summary>
    /// Interaction logic for EnemyControl.xaml
    /// </summary>
    public partial class EnemyControl : UserControl
    {
        private MainWindow mainWindow;

        private Dictionary<String, BuffControl> buffs = new Dictionary<string, BuffControl>();

        public EnemyControl(BGEntity bgEntity, MainWindow mainWindow)
        {
            InitializeComponent();
            
            
            //this.UserControl.Margin = new Thickness(left, 0, 0, 0);
            this.mainWindow         = mainWindow;
            this.updateView(bgEntity);
            double left = 0;
            var height = System.Windows.SystemParameters.PrimaryScreenHeight;
            var width = System.Windows.SystemParameters.PrimaryScreenWidth;
            var x = width * (bgEntity.X - bgEntity.MousePosX1) / bgEntity.ViewportWidth;
            var y = height * (bgEntity.Y - bgEntity.MousePosY1) / bgEntity.ViewportHeight;

            if (x > (width / 2))
            {
                left = x - this.Width - 50;
            }
            else
            {
                left = x + 50;
            }
            Canvas.SetTop(this, y / 2);
            Canvas.SetLeft(this, left);
            this.MouseRightButtonUp += EnemyControl_MouseRightButtonDown;
        }

        private void EnemyControl_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            this.Label_MouseDown(null, null);
        }

        public BGEntity BGEntity { get; private set; }

        public void Label_MouseDown(object sender, MouseButtonEventArgs e)
        {
            var ofs                 = this.RenderTransform.Value.OffsetY;
            ThicknessAnimation anim = new ThicknessAnimation();
            anim.From               = this.Margin;
            var newMargin           = this.Margin;
            newMargin.Top           = -this.ActualHeight - ofs;
            anim.To                 = newMargin;
            anim.EasingFunction     = new BackEase() { Amplitude = .5, EasingMode = EasingMode.EaseIn };
            anim.Duration           = TimeSpan.FromSeconds(.45);
            anim.Completed         += (o, e) =>
            {
                mainWindow.deleteMe(BGEntity.tag);
                (this.Parent as Canvas).Children.Remove(this);
            };
            anim.FillBehavior = FillBehavior.HoldEnd;
            this.BeginAnimation(System.Windows.Controls.UserControl.MarginProperty, anim, HandoffBehavior.SnapshotAndReplace);            
        }

        internal void updateView(BGEntity item)
        {
            this.BGEntity = item;            
            if (item.Type != 49)
                return;
            this.BGEntity.LoadCREResource();
            this.BGEntity.loadTimedEffects();
            this.BGEntity.LoadDerivedStats();            
            this.DataContext = this.BGEntity;

            if (Configuration.BigBuffIcons && BuffStack.Columns == 16)
            {
                BuffStack.Columns = 8;
                foreach(var buff in buffs) 
                {
                    buff.Value.Invalidate();
                }
            }

            if (!Configuration.BigBuffIcons && BuffStack.Columns == 8)
            {
                BuffStack.Columns = 16;
                foreach (var buff in buffs)
                {
                    buff.Value.Invalidate();
                }
            }


            foreach (var buff in buffs)
            {
                if ((buff.Value.BuffDurationAbsolute != 0 && buff.Value.BuffDurationAbsolute - this.BGEntity.GameTime < 0)
                        || !this.BGEntity.SpellProtection.Any(x => x.Item1 == buff.Key))
                {
                    this.BuffStack.Children.Remove(buff.Value);
                    buffs.Remove(buff.Key);
                }                
            }

            if (this.BGEntity.SpellProtection.Count > 0)
            {
                this.BGEntity.SpellProtection.ForEach(x =>
                {
                    int rounds = ((int)x.Item3 - (int)this.BGEntity.GameTime) / 15 / 6;
                    var timeString = rounds > 10 ? $"{(float)rounds / 10}" : $"{rounds}";

                    float durationFloat = rounds > 10 ? (float)rounds / 10 : rounds;

                    bool isPresent = buffs.ContainsKey(x.Item1);
                    if (isPresent)
                    {
                        buffs[x.Item1].BuffDuration = x.Item3 == uint.MaxValue ? float.MaxValue : durationFloat;
                        return;                    
                    }
                    BitmapSource newIcon;
                    if (x.Item2 != null)
                    {
                       newIcon = Imaging.CreateBitmapSourceFromHBitmap(
                       x.Item2.GetHbitmap(),
                       IntPtr.Zero,
                       Int32Rect.Empty,
                       BitmapSizeOptions.FromWidthAndHeight(x.Item2.Width, x.Item2.Height));
                    } else
                    {
                        newIcon = new BitmapImage(new Uri($"icons/not_found.png", UriKind.Relative));
                    }                    

                    var buffControl = new BuffControl() 
                    { 
                        BuffName = x.Item1, 
                        BuffDuration = x.Item3 == uint.MaxValue ? float.MaxValue : durationFloat, 
                        Icon = newIcon, 
                        BuffDurationAbsolute = x.Item3 
                    };
                    BuffStack.Children.Add(buffControl);
                    this.buffs.Add(x.Item1, buffControl);
                });
                this.BuffStack.Visibility = Visibility.Visible;
            }

            if (this.BGEntity.Protections.Count > 0)
            {
                this.protectionsListView.Visibility = Visibility.Visible;
            }
        }
    }
}
