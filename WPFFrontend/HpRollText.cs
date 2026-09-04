using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using BGOverlay;

namespace WPFFrontend
{
    /// <summary>
    /// A TextBlock that displays "HP:{value}" (via the Str_EnemyListHP locale key) and, when its
    /// bound HP decreases, rolls the displayed number down from the old value to the new one over
    /// one second instead of jumping straight to it - like an odometer/scoreboard counting down.
    /// An increase (healing, or a brand-new row's very first HP - HP always starts above the
    /// property's zero default, so that first assignment is always seen as an "increase") updates
    /// immediately with no animation.
    /// </summary>
    public class HpRollText : TextBlock
    {
        public static readonly DependencyProperty HPProperty =
            DependencyProperty.Register(nameof(HP), typeof(int), typeof(HpRollText),
                new PropertyMetadata(0, OnHPChanged));

        public int HP
        {
            get => (int)GetValue(HPProperty);
            set => SetValue(HPProperty, value);
        }

        // Drives what's actually shown; animated independently of HP itself so a roll-down can
        // interpolate through intermediate values instead of jumping straight to the new one.
        private static readonly DependencyProperty DisplayedHPProperty =
            DependencyProperty.Register("DisplayedHP", typeof(int), typeof(HpRollText),
                new PropertyMetadata(0, OnDisplayedHPChanged));

        private static void OnHPChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var self = (HpRollText)d;
            var oldValue = (int)e.OldValue;
            var newValue = (int)e.NewValue;

            if (newValue < oldValue)
            {
                var animation = new Int32Animation(oldValue, newValue, TimeSpan.FromSeconds(1))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                self.BeginAnimation(DisplayedHPProperty, animation, HandoffBehavior.SnapshotAndReplace);
            }
            else
            {
                // Stop any in-flight roll-down (e.g. HP rose again mid-animation) and jump
                // straight to the new value.
                self.BeginAnimation(DisplayedHPProperty, null);
                self.SetValue(DisplayedHPProperty, newValue);
            }
        }

        private static void OnDisplayedHPChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((HpRollText)d).Text = string.Format(RadarLocalization.Get("Str_EnemyListHP"), (int)e.NewValue);
        }
    }
}
