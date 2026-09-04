using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
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

        // Str_EnemyListHP is a "<label>{0}" format string (e.g. "HP: {0}") - split it at the
        // placeholder so the label can render bold while the number itself stays regular weight,
        // instead of setting Text to the fully-formatted string as one uniformly-styled run.
        private static void OnDisplayedHPChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var self   = (HpRollText)d;
            var format = RadarLocalization.Get("Str_EnemyListHP");
            var value  = ((int)e.NewValue).ToString();

            self.Inlines.Clear();
            var placeholderIndex = format.IndexOf("{0}", StringComparison.Ordinal);
            if (placeholderIndex < 0)
            {
                self.Inlines.Add(new Run(string.Format(format, value)));
                return;
            }

            var label  = format.Substring(0, placeholderIndex);
            var suffix = format.Substring(placeholderIndex + "{0}".Length);
            self.Inlines.Add(new Run(label) { FontWeight = FontWeights.Bold });
            self.Inlines.Add(new Run(value));
            if (!string.IsNullOrEmpty(suffix))
                self.Inlines.Add(new Run(suffix));
        }
    }
}
