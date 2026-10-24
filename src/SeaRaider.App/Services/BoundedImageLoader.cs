using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using AsyncImageLoader;
using Avalonia.Media.Imaging;
using SkiaSharp;

namespace SeaRaider.App.Services;

/// <summary>
/// Pictures for the whole app (covers, box art, screenshots), kept on disk so the store works offline, and in memory only as many
/// as are in use. The loader this replaced kept every picture ever shown in memory at full size: thousands of ROM box-art PNGs
/// (1000–2000 px each, several MB once decoded) ran SeaRaider out of memory. Here:
/// <list type="bullet">
///   <item>pictures are decoded at most <see cref="MaxWidth"/> wide (sharp in a card or a page, a fraction of the memory);</item>
///   <item>only the <see cref="MaxInMemory"/> most recently used stay in memory; older ones are read from disk again if shown;</item>
///   <item>a picture asked for by many cards at once is fetched once.</item>
/// </list>
/// </summary>
public sealed class BoundedImageLoader(HttpClient client, string cacheFolder) : IAsyncImageLoader
{
    private const int MaxWidth = 1280;
    private const int MaxInMemory = 240;

    private readonly ConcurrentDictionary<string, Task<Bitmap?>> _loading = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LinkedListNode<(string Url, Bitmap Bitmap)>> _byUrl = new(StringComparer.Ordinal);
    private readonly LinkedList<(string Url, Bitmap Bitmap)> _recent = new();
    private readonly object _lock = new();

    public Task<Bitmap?> ProvideImageAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return Task.FromResult<Bitmap?>(null);
        lock (_lock)
        {
            if (_byUrl.TryGetValue(url, out var node))
            {
                _recent.Remove(node);
                _recent.AddFirst(node);
                return Task.FromResult<Bitmap?>(node.Value.Bitmap);
            }
        }
        return _loading.GetOrAdd(url, u => LoadAsync(u));
    }

    private async Task<Bitmap?> LoadAsync(string url)
    {
        try
        {
            var bytes = await ReadAsync(url).ConfigureAwait(false);
            if (bytes is null || bytes.Length == 0) return null;
            var bitmap = Decode(bytes);
            if (bitmap is null) return null;
            lock (_lock)
            {
                _byUrl[url] = _recent.AddFirst((url, bitmap));
                while (_recent.Count > MaxInMemory)
                {
                    var last = _recent.Last!;
                    _recent.RemoveLast();
                    _byUrl.Remove(last.Value.Url); // not disposed: a card may still show it; the GC frees it once none does
                }
            }
            return bitmap;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or ArgumentException or InvalidOperationException
                                       or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
        finally
        {
            _loading.TryRemove(url, out _);
        }
    }

    /// <summary>The picture's bytes: from the disk cache, a local file, or the web (then cached).</summary>
    private async Task<byte[]?> ReadAsync(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsFile)
            return File.Exists(uri.LocalPath) ? await File.ReadAllBytesAsync(uri.LocalPath).ConfigureAwait(false) : null;
        if (uri is null || uri.Scheme is not ("http" or "https")) return null;

        var path = Path.Combine(cacheFolder, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url))));
        if (File.Exists(path)) return await File.ReadAllBytesAsync(path).ConfigureAwait(false);

        using var response = await client.GetAsync(uri).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(cacheFolder);
            await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // another copy is writing it
        }
        return bytes;
    }

    /// <summary>Decodes at most <see cref="MaxWidth"/> wide (big pictures are scaled down as they're read).</summary>
    private static Bitmap? Decode(byte[] bytes)
    {
        int width;
        using (var codec = SKCodec.Create(new SKMemoryStream(bytes)))
        {
            if (codec is null) return null; // not a picture (an error page)
            width = codec.Info.Width;
        }
        using var stream = new MemoryStream(bytes);
        return width > MaxWidth ? Bitmap.DecodeToWidth(stream, MaxWidth, BitmapInterpolationMode.HighQuality) : new Bitmap(stream);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _byUrl.Clear();
            _recent.Clear();
        }
    }
}
