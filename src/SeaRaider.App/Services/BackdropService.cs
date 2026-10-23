using AsyncImageLoader;
using Avalonia.Media.Imaging;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace SeaRaider.App.Services;

/// <summary>
/// The blurred backdrop of "The Next Generation" skin: the featured game's art, shrunk and blurred once with Skia.
/// A live blur effect over the whole window would be recomputed whenever anything above it changes (every scroll
/// frame); a pre-blurred, small bitmap stretched to the window looks the same and costs nothing to draw.
/// </summary>
public sealed class BackdropService(ILogger<BackdropService> logger)
{
    private const int Width = 480;
    private const float Sigma = 12f;

    private readonly Dictionary<string, Bitmap> _cache = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();

    /// <summary>The blurred backdrop for <paramref name="imageUrl"/>, or null when the art can't be loaded.</summary>
    public async Task<Bitmap?> GetAsync(string? imageUrl)
    {
        if (string.IsNullOrWhiteSpace(imageUrl)) return null;
        lock (_cache)
        {
            if (_cache.TryGetValue(imageUrl, out var cached)) return cached;
        }

        try
        {
            var source = await ImageLoader.AsyncImageLoader.ProvideImageAsync(imageUrl).ConfigureAwait(false);
            if (source is null) return null;
            var blurred = await Task.Run(() => Blur(source)).ConfigureAwait(false);

            lock (_cache)
            {
                _cache[imageUrl] = blurred;
                _order.Enqueue(imageUrl);
                while (_order.Count > 8) _cache.Remove(_order.Dequeue());
            }
            return blurred;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "No backdrop for {Url}", imageUrl);
            return null;
        }
    }

    private Bitmap Blur(Bitmap source)
    {
        try
        {
            using var encoded = new MemoryStream();
            source.Save(encoded);
            encoded.Position = 0;
            using var original = SKBitmap.Decode(encoded);
            if (original is null || original.Width == 0) return source;

            var height = Math.Max(1, (int)Math.Round(original.Height * (Width / (double)original.Width)));
            using var surface = SKSurface.Create(new SKImageInfo(Width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
            using var paint = new SKPaint
            {
                ImageFilter = SKImageFilter.CreateBlur(Sigma, Sigma, SKShaderTileMode.Clamp),
                IsAntialias = true,
            };
            surface.Canvas.DrawBitmap(original, new SKRect(0, 0, Width, height), paint);

            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var output = data.AsStream();
            return new Bitmap(output);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Couldn't blur the backdrop; using it as is");
            return source;
        }
    }
}
