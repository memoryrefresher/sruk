using System.Text.RegularExpressions;

namespace SeaRaider.Core.Scraping;

/// <summary>Normalises video URLs found on pages and derives thumbnails for the UI.</summary>
public static partial class MediaUrlHelper
{
    [GeneratedRegex(@"(?:youtube(?:-nocookie)?\.com/(?:embed/|watch\?(?:.*&)?v=|v/|shorts/)|youtu\.be/)(?<id>[A-Za-z0-9_-]{11})",
        RegexOptions.IgnoreCase)]
    private static partial Regex YouTubeRegex();

    [GeneratedRegex(@"vimeo\.com/(?:video/)?(?<id>\d{5,})", RegexOptions.IgnoreCase)]
    private static partial Regex VimeoRegex();

    private static readonly string[] DirectVideoExtensions = [".mp4", ".webm", ".m4v", ".mov", ".m3u8"];

    /// <summary>
    /// Returns a canonical, user-openable video URL (YouTube watch page, Vimeo page, or the direct file),
    /// or null if <paramref name="raw"/> isn't a recognised video.
    /// </summary>
    public static string? NormalizeVideoUrl(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (raw.StartsWith("//", StringComparison.Ordinal)) raw = "https:" + raw;

        if (TryGetYouTubeId(raw) is { } yt) return $"https://www.youtube.com/watch?v={yt}";

        var vimeo = VimeoRegex().Match(raw);
        if (vimeo.Success) return $"https://vimeo.com/{vimeo.Groups["id"].Value}";

        if (Uri.TryCreate(raw, UriKind.Absolute, out var uri) &&
            DirectVideoExtensions.Any(ext => uri.AbsolutePath.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
        {
            return uri.AbsoluteUri;
        }

        return null;
    }

    public static string? TryGetYouTubeId(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        var m = YouTubeRegex().Match(url);
        return m.Success ? m.Groups["id"].Value : null;
    }

    /// <summary>High-quality thumbnail for a YouTube trailer, or null for other providers.</summary>
    public static string? GetThumbnailUrl(string videoUrl) =>
        TryGetYouTubeId(videoUrl) is { } id ? $"https://img.youtube.com/vi/{id}/hqdefault.jpg" : null;

    public static bool IsImageUrl(string url)
    {
        var path = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.AbsolutePath : url;
        return path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase);
    }
}
