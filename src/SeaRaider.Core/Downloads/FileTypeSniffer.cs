namespace SeaRaider.Core.Downloads;

/// <summary>
/// The real type of a downloaded file from its first bytes, for downloads whose address and server gave no file name
/// ("/?__df=…" download scripts): archives, so they're unpacked, and the common cartridge ROMs, so the console is known.
/// </summary>
public static class FileTypeSniffer
{
    private static readonly byte[] NintendoLogoGba = [0x24, 0xFF, 0xAE, 0x51, 0x69, 0x9A, 0xA2, 0x21];
    private static readonly byte[] NintendoLogoGb = [0xCE, 0xED, 0x66, 0x66, 0xCC, 0x0D, 0x00, 0x0B];

    /// <summary>The extension (with the dot) for the file's content, or null when it isn't recognised.</summary>
    public static string? Sniff(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            var head = new byte[0x150];
            var read = file.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            return Sniff(head.AsSpan(0, read));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static string? Sniff(ReadOnlySpan<byte> head)
    {
        if (StartsWith(head, [0x50, 0x4B, 0x03, 0x04]) || StartsWith(head, [0x50, 0x4B, 0x05, 0x06])) return ".zip";
        if (StartsWith(head, [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C])) return ".7z";
        if (StartsWith(head, "Rar!\u001A\u0007"u8)) return ".rar";
        if (StartsWith(head, [0x1F, 0x8B])) return ".gz";
        if (StartsWith(head, "NES\u001A"u8)) return ".nes";
        if (head.Length >= 0xC0 && head.Slice(4, NintendoLogoGba.Length).SequenceEqual(NintendoLogoGba) && head[0xB2] == 0x96) return ".gba";
        if (head.Length >= 0x144 && head.Slice(0x104, NintendoLogoGb.Length).SequenceEqual(NintendoLogoGb))
            return (head[0x143] & 0x80) != 0 ? ".gbc" : ".gb";
        return null;
    }

    private static bool StartsWith(ReadOnlySpan<byte> head, ReadOnlySpan<byte> magic) => head.StartsWith(magic);
}
