using System.Buffers.Binary;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;

namespace SeaRaider.Core.Catalog;

/// <summary>
/// Gets the full-size picture behind a thumbnail URL, and measures pictures without downloading them whole.
/// Repack sites show small previews (riotpixels' "….jpg.240p.jpg", WordPress' "-300x169.jpg", Steam's
/// "ss_….600x338.jpg", imgur's "abc1234m.jpg"); the full picture is one predictable URL away.
/// </summary>
public static partial class ImageQuality
{
    [GeneratedRegex(@"(\.(?:jpe?g|png|webp))\.\d{3,4}p\.(?:jpe?g|png|webp)$", RegexOptions.IgnoreCase)]
    private static partial Regex RiotPixelsThumbRegex();

    [GeneratedRegex(@"(/ss_[0-9a-f]{20,})\.\d{2,4}x\d{2,4}\.jpg$", RegexOptions.IgnoreCase)]
    private static partial Regex SteamSizedRegex();

    [GeneratedRegex(@"-\d{2,4}x\d{2,4}(\.(?:jpe?g|png|webp))$", RegexOptions.IgnoreCase)]
    private static partial Regex WordPressSizedRegex();

    [GeneratedRegex(@"^(/[A-Za-z0-9]{7})[sbtmlh](\.(?:jpe?g|png|gif|webp))$")]
    private static partial Regex ImgurThumbRegex();

    [GeneratedRegex(@"(?:^|&)(?:w|h|width|height|resize|fit|quality|strip)=", RegexOptions.IgnoreCase)]
    private static partial Regex ResizeQueryRegex();

    /// <summary>The full-size URL when the rule is certain (riotpixels, Steam, WordPress sizes); else the URL itself.</summary>
    public static string Upgrade(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;
        var path = uri.AbsolutePath;
        string? better = null;

        if (RiotPixelsThumbRegex().IsMatch(path)) better = RiotPixelsThumbRegex().Replace(path, "$1");
        else if (uri.Host.Contains("steamstatic", StringComparison.OrdinalIgnoreCase) && SteamSizedRegex().IsMatch(path))
            better = SteamSizedRegex().Replace(path, "$1.1920x1080.jpg");
        else if (!uri.Host.Contains("steamstatic", StringComparison.OrdinalIgnoreCase) && WordPressSizedRegex().IsMatch(path))
            better = WordPressSizedRegex().Replace(path, "$1");

        var query = uri.Query.TrimStart('?');
        var dropQuery = query.Length > 0 && ResizeQueryRegex().IsMatch(query);
        if (better is null && !dropQuery) return url;

        var builder = new UriBuilder(uri) { Path = better ?? path };
        if (dropQuery) builder.Query = string.Empty;
        return builder.Uri.AbsoluteUri;
    }

    /// <summary>
    /// Full-size guesses for <paramref name="url"/>, best first, ending with the URL itself. Includes rules that are
    /// usually right but not always (imgur, imageban), so callers measure them (<see cref="ProbeAsync"/>) before use.
    /// </summary>
    public static IReadOnlyList<string> Candidates(string url)
    {
        var list = new List<string>();
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            if (uri.Host.EndsWith("imgur.com", StringComparison.OrdinalIgnoreCase) && ImgurThumbRegex().IsMatch(uri.AbsolutePath))
                list.Add(new UriBuilder(uri) { Path = ImgurThumbRegex().Replace(uri.AbsolutePath, "$1$2") }.Uri.AbsoluteUri);
            if (uri.Host.Contains("imageban", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath.Contains("/thumbs/"))
                list.Add(new UriBuilder(uri) { Path = uri.AbsolutePath.Replace("/thumbs/", "/out/") }.Uri.AbsoluteUri);
        }
        var upgraded = Upgrade(url);
        if (!list.Contains(upgraded)) list.Add(upgraded);
        if (!list.Contains(url)) list.Add(url);
        return list;
    }

    /// <summary>Pixel size of the picture at <paramref name="url"/> (reads its first 256 KB), or null.</summary>
    public static async Task<(int Width, int Height)?> ProbeAsync(HttpClient client, string url, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new RangeHeaderValue(0, 262_143);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            if (response.Content.Headers.ContentType?.MediaType is { } type && !type.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                return null;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var buffer = new byte[262_144];
            var total = 0;
            int read;
            while (total < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false)) > 0)
                total += read;
            return ReadSize(buffer.AsSpan(0, total));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UriFormatException)
        {
            return null;
        }
    }

    /// <summary>Width and height from a PNG, JPEG or WebP header.</summary>
    public static (int Width, int Height)? ReadSize(ReadOnlySpan<byte> data)
    {
        // PNG: signature, then the IHDR chunk.
        if (data.Length >= 24 && data[0] == 0x89 && data[1] == 'P' && data[2] == 'N' && data[3] == 'G')
            return ((int)BinaryPrimitives.ReadUInt32BigEndian(data[16..]), (int)BinaryPrimitives.ReadUInt32BigEndian(data[20..]));

        // JPEG: walk the segments to the frame header (SOF0–SOF15, except DHT/JPG/DAC).
        if (data.Length >= 4 && data[0] == 0xFF && data[1] == 0xD8)
        {
            var i = 2;
            while (i + 9 < data.Length)
            {
                if (data[i] != 0xFF) { i++; continue; }
                var marker = data[i + 1];
                if (marker == 0xFF) { i++; continue; }
                if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                    return (BinaryPrimitives.ReadUInt16BigEndian(data[(i + 7)..]), BinaryPrimitives.ReadUInt16BigEndian(data[(i + 5)..]));
                if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7)) { i += 2; continue; }
                i += 2 + BinaryPrimitives.ReadUInt16BigEndian(data[(i + 2)..]);
            }
            return null;
        }

        // WebP: RIFF container with a VP8 / VP8L / VP8X chunk.
        if (data.Length >= 30 && data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F' &&
            data[8] == 'W' && data[9] == 'E' && data[10] == 'B' && data[11] == 'P')
        {
            var chunk = System.Text.Encoding.ASCII.GetString(data[12..16]);
            switch (chunk)
            {
                case "VP8 ":
                    return (BinaryPrimitives.ReadUInt16LittleEndian(data[26..]) & 0x3FFF, BinaryPrimitives.ReadUInt16LittleEndian(data[28..]) & 0x3FFF);
                case "VP8L":
                    var bits = BinaryPrimitives.ReadUInt32LittleEndian(data[21..]);
                    return ((int)(bits & 0x3FFF) + 1, (int)((bits >> 14) & 0x3FFF) + 1);
                case "VP8X":
                    return ((data[24] | data[25] << 8 | data[26] << 16) + 1, (data[27] | data[28] << 8 | data[29] << 16) + 1);
            }
        }
        return null;
    }
}
