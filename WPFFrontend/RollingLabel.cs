using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace WPFFrontend
{
    /// <summary>
    /// A TextBlock that displays a plain "{Format applied to Value}" string and rolls the
    /// displayed number from the old value to the new one over one second (counting up or down
    /// as needed) instead of jumping straight to it - by analogy with MainWindow's enemy list HP
    /// (see HpRollText), generalized to any single-number EnemyControl stat label (AC, THAC0,
    /// saves, resistances, etc.) instead of just "HP:{0}", and animating both directions since a
    /// buff landing/wearing off should read the same way either way.
    /// </summary>
    public class RollingLabel : TextBlock
    {
        public static readonly DependencyProperty ValueProperty =
            DependencyProperty.Register(nameof(Value), typeof(int), typeof(RollingLabel),
                new PropertyMetadata(0, OnValueChanged));

        public int Value
        {
            get => (int)GetValue(ValueProperty);
            set => SetValue(ValueProperty, value);
        }

        // The format string Value is substituted into via string.Format - e.g. "STR: {0}" or,
        // for Health, "Health: {0}/30" (the "/30" max-HP suffix baked in as literal text, since
        // only current HP animates).
        public static readonly DependencyProperty FormatProperty =
            DependencyProperty.Register(nameof(Format), typeof(string), typeof(RollingLabel),
                new PropertyMetadata("{0}", OnFormatChanged));

        public string Format
        {
            get => (string)GetValue(FormatProperty);
            set => SetValue(FormatProperty, value);
        }

        // Drives what's actually shown; animated independently of Value itself so a roll-down
        // can interpolate through intermediate values instead of jumping straight to the new one.
        private static readonly DependencyProperty DisplayedValueProperty =
            DependencyProperty.Register("DisplayedValue", typeof(int), typeof(RollingLabel),
                new PropertyMetadata(0, OnDisplayedValueChanged));

        private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var self = (RollingLabel)d;
            var oldValue = (int)e.OldValue;
            var newValue = (int)e.NewValue;

            if (newValue == oldValue)
                return;

            // Unlike HpRollText (which only rolls down and jumps straight to an increase, since
            // healing shouldn't count up slowly), stat labels here roll both ways - a buff
            // landing counts up, one wearing off counts down.
            var animation = new Int32Animation(oldValue, newValue, TimeSpan.FromSeconds(1))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            self.BeginAnimation(DisplayedValueProperty, animation, HandoffBehavior.SnapshotAndReplace);
        }

        // The format string can change independently of Value (e.g. Health's "/max" suffix, or
        // a locale switch) - re-render with whatever DisplayedValue currently holds.
        private static void OnFormatChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var self = (RollingLabel)d;
            self.Text = string.Format(self.Format, (int)self.GetValue(DisplayedValueProperty));
        }

        private static void OnDisplayedValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var self = (RollingLabel)d;
            self.Text = string.Format(self.Format, (int)e.NewValue);
        }
    }
}
