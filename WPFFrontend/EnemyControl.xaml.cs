using BGOverlay;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace WPFFrontend
{
    /// <summary>
    /// Interaction logic for EnemyControl.xaml
    /// </summary>
    public partial class EnemyControl : UserControl
    {
        private MainWindow mainWindow;

        private Dictionary<String, BuffControl> buffs = new Dictionary<string, BuffControl>();

        public EnemyControl(BGEntity bgEntity, MainWindow mainWindow, int left)
        {
            InitializeComponent();
            this.UserControl.Margin = new Thickness(left, 0, 0, 0);
            this.mainWindow = mainWindow;
            this.updateView(bgEntity);
            this.MouseRightButtonUp += EnemyControl_MouseRightButtonDown;
        }

        private void EnemyControl_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            this.Label_MouseDown(null, null);
        }

        public BGEntity BGEntity { get; private set; }

        public void Label_MouseDown(object sender, MouseButtonEventArgs e)
        {
            mainWindow.deleteMe(BGEntity.tag);
            (this.Parent as Grid)?.Children.Remove(this);            
        }

        internal void updateView(BGEntity item)
        {
            this.BGEntity = item;
            this.BGEntity.LoadDerivedStats();            
            this.BGEntity.loadTimedEffects();
            this.DataContext = this.BGEntity;

            foreach(var buff in buffs)
            {
                if (buff.Value.BuffDurationAbsolute - this.BGEntity.GameTime < 0
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
                        buffs[x.Item1].BuffDuration = durationFloat;
                        return;
                    }

                    var uri = new Uri($"icons/{x.Item2}00001.png", UriKind.Relative);
                    if (!this.BGEntity.spellProtectionIcons.Contains(x.Item2))
                        uri = new Uri($"icons/not_found.png", UriKind.Relative);

                    var buffControl = new BuffControl() 
                    { 
                        BuffName = x.Item1, 
                        BuffDuration = durationFloat, 
                        Icon = uri, 
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
