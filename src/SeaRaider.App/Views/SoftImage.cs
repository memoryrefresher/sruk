using System.Runtime.CompilerServices;
using AsyncImageLoader;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace SeaRaider.App.Views;

/// <summary>
/// A picture shown out of focus, as a background. Instead of a live <see cref="BlurEffect"/> (re-computed every frame
/// the area is drawn, e.g. while scrolling), the picture is shrunk once to a tiny thumbnail and stretched back up with
/// smooth filtering: it looks the same and costs nothing per frame.
/// </summary>
public sealed class SoftImage : Image
{
    public static readonly StyledProperty<string?> UrlProperty = AvaloniaProperty.Register<SoftImage, string?>(nameof(Url));

    private int _version;

    public SoftImage()
    {
        Stretch = Stretch.UniformToFill;
        IsHitTestVisible = false;
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.HighQuality);
    }

    public string? Url
    {
        get => GetValue(UrlProperty);
        set => SetValue(UrlProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == UrlProperty) _ = LoadAsync(change.GetNewValue<string?>());
    }

    private async Task LoadAsync(string? url)
    {
        var version = ++_version;
        if (string.IsNullOrEmpty(url))
        {
            Source = null;
            return;
        }
        try
        {
            var bitmap = await ImageLoader.AsyncImageLoader.ProvideImageAsync(url);
            if (version == _version) Source = bitmap is null ? null : Softener.Soften(bitmap);
        }
        catch (Exception)
        {
            if (version == _version) Source = null; // a background is a nicety
        }
    }
}

/// <summary>Tiny, cached thumbnails of pictures, for out-of-focus backgrounds.</summary>
internal static class Softener
{
    private const int Width = 48;
    private static readonly ConditionalWeakTable<Bitmap, Bitmap> Cache = new();

    public static Bitmap Soften(Bitmap source)
    {
        if (Cache.TryGetValue(source, out var soft)) return soft;
        var size = source.PixelSize;
        if (size.Width <= Width) return source;
        var height = Math.Max(1, (int)Math.Round(size.Height * (double)Width / size.Width));
        soft = source.CreateScaledBitmap(new PixelSize(Width, height), BitmapInterpolationMode.MediumQuality);
        Cache.AddOrUpdate(source, soft);
        return soft;
    }
}
