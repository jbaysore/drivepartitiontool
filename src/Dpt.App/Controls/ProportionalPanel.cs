using System.Windows;
using System.Windows.Controls;

namespace Dpt.App.Controls;

/// <summary>Lays children out left-to-right with widths proportional to their <see cref="WeightProperty"/>; tiny items get a minimum width.</summary>
public sealed class ProportionalPanel : Panel
{
    public static readonly DependencyProperty WeightProperty = DependencyProperty.RegisterAttached(
        "Weight", typeof(double), typeof(ProportionalPanel), new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsParentArrange));

    public static readonly DependencyProperty MinItemWidthProperty = DependencyProperty.Register(
        nameof(MinItemWidth), typeof(double), typeof(ProportionalPanel), new FrameworkPropertyMetadata(4.0, FrameworkPropertyMetadataOptions.AffectsArrange));

    public static double GetWeight(DependencyObject d) => (double)d.GetValue(WeightProperty);
    public static void SetWeight(DependencyObject d, double v) => d.SetValue(WeightProperty, v);

    public double MinItemWidth
    {
        get => (double)GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    protected override Size MeasureOverride(Size available)
    {
        double height = 0;
        foreach (UIElement c in InternalChildren)
        {
            c.Measure(new Size(double.PositiveInfinity, available.Height));
            height = Math.Max(height, c.DesiredSize.Height);
        }

        return new Size(double.IsInfinity(available.Width) ? 0 : available.Width, height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var children = InternalChildren.Cast<UIElement>().ToList();
        if (children.Count == 0) return final;
        var weights = children.Select(c => Math.Max(0, GetWeight(c))).ToList();
        var total = weights.Sum();
        if (total <= 0) total = 1;
        var min = Math.Min(MinItemWidth, final.Width / children.Count);
        var widths = weights.Select(w => final.Width * w / total).ToList();
        var small = widths.Count(w => w < min);
        var deficit = widths.Where(w => w < min).Sum(w => min - w);
        var bigTotal = widths.Where(w => w >= min).Sum();
        for (var i = 0; i < widths.Count; i++)
            widths[i] = widths[i] < min ? min : bigTotal <= 0 ? widths[i] : widths[i] - deficit * widths[i] / bigTotal;
        double x = 0;
        for (var i = 0; i < children.Count; i++)
        {
            children[i].Arrange(new Rect(x, 0, Math.Max(0, widths[i]), final.Height));
            x += widths[i];
        }

        _ = small;
        return final;
    }
}
