using System.Collections.Concurrent;
using Avalonia.Media.Imaging;
using SkiaSharp;

namespace SeaRaider.App.Services;

/// <summary>A profile picture's frame around it (Friends panel › your picture › Decoration).</summary>
public sealed record AvatarDecoration(string Id, string Name);

/// <summary>
/// Profile pictures: a chosen image is cropped to its centre square and shrunk to 128 × 128 as a JPEG (a few KB, so it
/// travels to friends in one message), and pictures received as base64 are turned into images once and kept.
/// </summary>
public static class AvatarImages
{
    public const int Size = 128;

    /// <summary>The frames on offer, in the order of the menu. "none" is no frame.</summary>
    public static IReadOnlyList<AvatarDecoration> Decorations { get; } =
    [
        new("none", "None"),
        new("ring", "Glow ring"),
        new("waves", "Sea waves"),
        new("neon", "Neon"),
        new("gold", "Gold laurel"),
        new("crown", "Crown"),
        new("halloween", "Hallowsea 🎃"),
    ];

    private static readonly ConcurrentDictionary<string, Bitmap?> Cache = new(StringComparer.Ordinal);

    /// <summary>Reads an image file and makes the 128 × 128 JPEG (base64) to use as a profile picture.</summary>
    /// <exception cref="ArgumentException">Not an image SeaRaider can read.</exception>
    public static Task<string> MakeAvatarAsync(string path) => Task.Run(() =>
    {
        using var original = SKBitmap.Decode(path) ?? throw new ArgumentException("That file isn't a picture SeaRaider can read (PNG, JPEG, GIF, WebP or BMP).");
        var side = Math.Min(original.Width, original.Height);
        var source = new SKRect((original.Width - side) / 2f, (original.Height - side) / 2f,
            (original.Width + side) / 2f, (original.Height + side) / 2f);

        using var surface = SKSurface.Create(new SKImageInfo(Size, Size, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var paint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High })
        {
            surface.Canvas.Clear(SKColors.Black);
            surface.Canvas.DrawBitmap(original, source, new SKRect(0, 0, Size, Size), paint);
        }
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 86);
        return Convert.ToBase64String(data.ToArray());
    });

    /// <summary>The image for a base64 picture (kept, so a friend list redraw doesn't decode it again), or null.</summary>
    public static Bitmap? ToBitmap(string? base64)
    {
        if (string.IsNullOrEmpty(base64)) return null;
        if (Cache.Count > 400) Cache.Clear(); // old pictures of friends who changed theirs
        return Cache.GetOrAdd(base64, _ =>
        {
            try
            {
                using var stream = new MemoryStream(Convert.FromBase64String(base64));
                return new Bitmap(stream);
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException or NotSupportedException)
            {
                return null;
            }
        });
    }
}
