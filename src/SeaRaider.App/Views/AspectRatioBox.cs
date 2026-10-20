using Avalonia;
using Avalonia.Controls;

namespace SeaRaider.App.Views;

/// <summary>
/// Sizes its child to a fixed aspect ratio (width ÷ height), as large as the space allows, centred. With an unbounded
/// height (in a vertical StackPanel or scroller) the height simply follows the width.
/// <see cref="ExtraHeight"/> is added on top of the ratio, for a fixed-height header above a video, say.
/// </summary>
public sealed class AspectRatioBox : Decorator
{
    public static readonly StyledProperty<double> RatioProperty =
        AvaloniaProperty.Register<AspectRatioBox, double>(nameof(Ratio), 16.0 / 9.0);

    public static readonly StyledProperty<double> ExtraHeightProperty =
        AvaloniaProperty.Register<AspectRatioBox, double>(nameof(ExtraHeight));

    static AspectRatioBox() => AffectsMeasure<AspectRatioBox>(RatioProperty, ExtraHeightProperty);

    public double Ratio
    {
        get => GetValue(RatioProperty);
        set => SetValue(RatioProperty, value);
    }

    public double ExtraHeight
    {
        get => GetValue(ExtraHeightProperty);
        set => SetValue(ExtraHeightProperty, value);
    }

    private Size Fit(Size available)
    {
        var ratio = Ratio > 0 ? Ratio : 16.0 / 9.0;
        var extra = Math.Max(0, ExtraHeight);
        var width = available.Width;
        var height = available.Height;

        if (double.IsInfinity(width) && double.IsInfinity(height)) return default;
        if (double.IsInfinity(width)) return new Size(Math.Max(0, height - extra) * ratio, height);

        var fitted = width / ratio + extra;
        if (!double.IsInfinity(height) && fitted > height) return new Size(Math.Max(0, height - extra) * ratio, height);
        return new Size(width, fitted);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = Fit(availableSize);
        Child?.Measure(size);
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = Fit(finalSize);
        Child?.Arrange(new Rect((finalSize.Width - size.Width) / 2, (finalSize.Height - size.Height) / 2, size.Width, size.Height));
        return finalSize;
    }
}
