using SeaRaider.Core.Extraction;
using SeaRaider.Core.Models;

namespace SeaRaider.Core.Downloads;

/// <summary>
/// How much disk space a game needs from the first byte downloaded to the finished setup, shown before the download starts.
/// <code>
///  1. Download + extract   archives (A) + the unpacked setup (E)         [low disk space mode: E + two volumes]
///  2. Setup runs           the setup files (E) + the installed game (I)  [+ A when a torrent is kept for seeding]
/// </code>
/// The archives are deleted before setup starts (when "delete archives after install" is on) and the setup files after it
/// succeeds, so the peak is whichever phase is bigger. With the temp folder on another drive from the library, each drive
/// only needs its own share.
/// </summary>
public sealed record SpaceEstimate(
    long DownloadBytes,
    long ExtractedBytes,
    long InstalledBytes,
    bool InstalledKnown,
    bool Streaming,
    bool SameDrive,
    long LibraryDriveNeeded,
    long TempDriveNeeded,
    long? LibraryFree,
    long? TempFree)
{
    /// <summary>Kept free on top of the estimate (the same reserve the download pipeline uses).</summary>
    public const long Headroom = 2L * 1024 * 1024 * 1024;

    public bool LibraryShort => LibraryFree is { } free && free < LibraryDriveNeeded + Headroom;
    public bool TempShort => !SameDrive && TempFree is { } free && free < TempDriveNeeded + Headroom;
    public bool IsShort => LibraryShort || TempShort;

    /// <summary>One or two plain sentences for the download dialog.</summary>
    public string Summary
    {
        get
        {
            var parts = $"download {Formatting.Bytes(DownloadBytes)}, unpacked {Formatting.Bytes(ExtractedBytes)}, " +
                        (InstalledKnown ? $"installed {Formatting.Bytes(InstalledBytes)}" : $"installed size unknown (counted as {Formatting.Bytes(InstalledBytes)})");
            var mode = Streaming ? " Low disk space mode unpacks while downloading, so the archives never pile up." : string.Empty;
            return SameDrive
                ? $"Space needed at the busiest moment: about {Formatting.Bytes(LibraryDriveNeeded)} on {Drive(LibraryRoot)} ({parts}), plus about 2 GB to spare.{mode}" +
                  (LibraryFree is { } f ? $" Free now: {Formatting.Bytes(f)}." : string.Empty)
                : $"Space needed: about {Formatting.Bytes(TempDriveNeeded)} on {Drive(TempRoot)} for the download and about {Formatting.Bytes(LibraryDriveNeeded)} on {Drive(LibraryRoot)} for the game ({parts}), plus about 2 GB to spare on each.{mode}";
        }
    }

    /// <summary>Set when a drive is short; null when there's room (or free space is unknown).</summary>
    public string? Warning
    {
        get
        {
            if (!IsShort) return null;
            if (SameDrive || LibraryShort && !TempShort)
                return $"Not enough free space on {Drive(LibraryRoot)}: about {Formatting.Bytes(LibraryDriveNeeded + Headroom)} needed, {Formatting.Bytes(LibraryFree ?? 0)} free. " +
                       "The download may fail part-way. Free some space, or move the library in Settings.";
            return TempShort && !LibraryShort
                ? $"Not enough free space on {Drive(TempRoot)} for the download: about {Formatting.Bytes(TempDriveNeeded + Headroom)} needed, {Formatting.Bytes(TempFree ?? 0)} free."
                : $"Not enough free space: {Drive(TempRoot)} needs about {Formatting.Bytes(TempDriveNeeded + Headroom)} and {Drive(LibraryRoot)} about {Formatting.Bytes(LibraryDriveNeeded + Headroom)}.";
        }
    }

    internal string LibraryRoot { get; init; } = string.Empty;
    internal string TempRoot { get; init; } = string.Empty;

    private static string Drive(string path)
    {
        try { return Path.GetPathRoot(Path.GetFullPath(path)) ?? path; }
        catch (ArgumentException) { return path; }
    }
}

public static class SpaceEstimator
{
    /// <summary>Unpacked archives come out a little bigger than they went in (the same guess low disk space mode uses).</summary>
    private const double UnpackFactor = 1.15;

    /// <summary>Null when the download's size isn't known (nothing sensible to say).</summary>
    public static SpaceEstimate? Estimate(GameMetadata game, DownloadTransport transport, IReadOnlyList<DownloadLink> links,
        IReadOnlyList<DownloadFileSpec> files, AppSettings settings)
    {
        var download = DownloadSize(game, transport, links);
        if (download is not { } a || a <= 0) return null;

        // Setup files and their .bin parts are moved into place, not unpacked: no second copy.
        var passthrough = transport == DownloadTransport.Http && files.Count > 0 &&
                          files.All(f => !ArchiveSetResolver.IsArchiveFile(f.OriginalFileName));
        var extracted = passthrough ? a : (long)(a * UnpackFactor);

        var installedKnown = Formatting.ParseBytes(game.OriginalSize) is > 0;
        var installed = installedKnown ? Formatting.ParseBytes(game.OriginalSize)!.Value : extracted;

        var canStream = transport == DownloadTransport.Http && settings.LowDiskSpaceMode != LowDiskMode.Never &&
                        DownloadAndExtractManager.IsRarVolumeSet(files);
        var volume = a / Math.Max(1, files.Count);
        var seeding = transport == DownloadTransport.Torrent && settings.SeedAfterDownload;
        var keepArchives = !settings.DeleteArchivesAfterInstall || seeding;
        var sameDrive = SameRoot(settings.TempRoot, settings.LibraryRoot);
        var libraryFree = FreeSpace(settings.LibraryRoot);
        var tempFree = FreeSpace(settings.TempRoot);

        (long Library, long Temp) Needs(bool streaming)
        {
            if (sameDrive)
            {
                var extract = streaming ? extracted + 2 * volume : passthrough ? a : a + extracted;
                var setup = extracted + installed + (keepArchives ? a : 0);
                var both = Math.Max(extract, setup);
                return (both, both);
            }
            // Staging becomes the setup folder, then the game goes in beside it.
            return (extracted + installed, streaming ? 2 * volume : a);
        }

        bool Fits((long Library, long Temp) n) =>
            (libraryFree is not { } lf || lf >= n.Library + SpaceEstimate.Headroom) &&
            (sameDrive || tempFree is not { } tf || tf >= n.Temp + SpaceEstimate.Headroom);

        // "Automatic" only unpacks while downloading when the normal way doesn't fit; "Always" does for every RAR set.
        var streamNow = canStream && (settings.LowDiskSpaceMode == LowDiskMode.Always || !Fits(Needs(false)));
        var need = Needs(streamNow);

        return new SpaceEstimate(a, extracted, installed, installedKnown, streamNow, sameDrive, need.Library, need.Temp, libraryFree, tempFree)
        {
            LibraryRoot = settings.LibraryRoot,
            TempRoot = settings.TempRoot,
        };
    }

    private static long? DownloadSize(GameMetadata game, DownloadTransport transport, IReadOnlyList<DownloadLink> links)
    {
        // What's printed next to the chosen links, when every one has a size; else the page's repack size.
        var sizes = links.Select(l => Formatting.ParseBytes(l.SizeText)).ToList();
        if (transport == DownloadTransport.Http && sizes.Count > 0 && sizes.All(s => s is > 0)) return sizes.Sum(s => s!.Value);
        return Formatting.ParseBytes(game.RepackSize) ?? Formatting.ParseBytes(game.SizeText) ?? Formatting.ParseBytes(game.OriginalSize);
    }

    private static bool SameRoot(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static long? FreeSpace(string path)
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!);
            return drive.IsReady ? drive.AvailableFreeSpace : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
