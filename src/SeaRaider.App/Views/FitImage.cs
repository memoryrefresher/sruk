using AsyncImageLoader;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace SeaRaider.App.Views;

/// <summary>
/// A picture that always fills its box edge to edge, with no bars and no awkward crops:
/// <list type="bullet">
///   <item>When the picture has (nearly) the box's shape — a Steam header in a card, a 16:9 screenshot in the media
///         viewer — it simply fills the box.</item>
///   <item>When the shapes are far apart (a tall box-art cover in a wide card) cropping would cut most of it away, so
///         the whole picture is shown, over a blurred, zoomed copy of itself that fills the rest of the box.</item>
/// </list>
/// Loads <see cref="Url"/> with AsyncImageLoader, like the plain images elsewhere.
/// </summary>
public sealed class FitImage : Panel
{
    /// <summary>How far the shapes may differ before switching to "whole picture" (ln of the ratio; 0.2 ≈ 20 %).</summary>
    private const double MaxCropLog = 0.2;

    public static readonly StyledProperty<string?> UrlProperty =
        AvaloniaProperty.Register<FitImage, string?>(nameof(Url));

    /// <summary>Always fill and crop, whatever the shape (YouTube's 4:3 thumbnails carry a 16:9 picture between bars).</summary>
    public static readonly StyledProperty<bool> AlwaysFillProperty =
        AvaloniaProperty.Register<FitImage, bool>(nameof(AlwaysFill));

    public bool AlwaysFill
    {
        get => GetValue(AlwaysFillProperty);
        set => SetValue(AlwaysFillProperty, value);
    }

    // The out-of-focus fill is a tiny thumbnail stretched up (see Softener), not a live blur: no cost per frame.
    private readonly Image _fill = new()
    {
        Stretch = Stretch.UniformToFill,
        Opacity = 0.55,
        IsVisible = false,
        IsHitTestVisible = false,
    };

    private readonly Image _picture = new() { Stretch = Stretch.UniformToFill, IsHitTestVisible = false };

    public FitImage()
    {
        ClipToBounds = true;
        RenderOptions.SetBitmapInterpolationMode(_fill, BitmapInterpolationMode.HighQuality);
        Children.Add(_fill);
        Children.Add(_picture);
        _picture.PropertyChanged += (_, e) =>
        {
            if (e.Property == Image.SourceProperty) UpdateFit();
        };
    }

    public string? Url
    {
        get => GetValue(UrlProperty);
        set => SetValue(UrlProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == UrlProperty) ImageLoader.SetSource(_picture, change.GetNewValue<string?>());
        else if (change.Property == BoundsProperty || change.Property == AlwaysFillProperty) UpdateFit();
    }

    private void UpdateFit()
    {
        var source = _picture.Source;
        var picture = source?.Size ?? default;
        var box = Bounds.Size;
        var whole = !AlwaysFill && source is not null && picture.Width > 0 && picture.Height > 0 && box.Width > 0 && box.Height > 0
                    && Math.Abs(Math.Log(picture.Width / picture.Height / (box.Width / box.Height))) > MaxCropLog;

        _picture.Stretch = whole ? Stretch.Uniform : Stretch.UniformToFill;
        _fill.Source = whole ? source is Bitmap bitmap ? Softener.Soften(bitmap) : source : null;
        _fill.IsVisible = whole;
    }
}
