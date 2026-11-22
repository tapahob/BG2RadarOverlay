using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace WPFFrontend
{
    public sealed class CenteredWrapPanel : Panel
    {
        public static readonly DependencyProperty CenterRowsProperty = DependencyProperty.Register(
            nameof(CenterRows), typeof(bool), typeof(CenteredWrapPanel),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsArrange));

        public bool CenterRows
        {
            get => (bool)GetValue(CenterRowsProperty);
            set => SetValue(CenterRowsProperty, value);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            foreach (UIElement child in InternalChildren)
                child.Measure(availableSize);

            double width = 0;
            double height = 0;
            foreach (var row in GetRows(availableSize.Width))
            {
                width = Math.Max(width, row.Width);
                height += row.Height;
            }
            return new Size(width, height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            double y = 0;
            foreach (var row in GetRows(finalSize.Width))
            {
                double x = CenterRows ? Math.Max(0, (finalSize.Width - row.Width) / 2) : 0;
                for (int i = row.Start; i < row.End; i++)
                {
                    UIElement child = InternalChildren[i];
                    double width = child.DesiredSize.Width;
                    child.Arrange(new Rect(x, y, width, row.Height));
                    x += width;
                }
                y += row.Height;
            }
            return finalSize;
        }

        private IEnumerable<(int Start, int End, double Width, double Height)> GetRows(double availableWidth)
        {
            int start = 0;
            double rowWidth = 0;
            double rowHeight = 0;
            for (int i = 0; i < InternalChildren.Count; i++)
            {
                Size size = InternalChildren[i].DesiredSize;
                if (i > start && rowWidth + size.Width > availableWidth)
                {
                    yield return (start, i, rowWidth, rowHeight);
                    start = i;
                    rowWidth = 0;
                    rowHeight = 0;
                }
                rowWidth += size.Width;
                rowHeight = Math.Max(rowHeight, size.Height);
            }

            if (start < InternalChildren.Count)
                yield return (start, InternalChildren.Count, rowWidth, rowHeight);
        }
    }
}
