using SeaRaider.Core;
using SeaRaider.Core.Downloads;
using SeaRaider.Core.Models;

namespace SeaRaider.Core.Tests;

public class SpaceEstimatorTests
{
    private const long Gb = 1024L * 1024 * 1024;

    private static (GameMetadata Game, List<DownloadLink> Links, List<DownloadFileSpec> Files) Rar(int parts, string size, string? installed)
    {
        var links = Enumerable.Range(1, parts).Select(i => new DownloadLink
        {
            Url = $"https://host.example/g.part{i}.rar",
            Kind = DownloadLinkKind.MultipartArchivePart,
            FileName = $"g.part{i}.rar",
            PartNumber = i,
            SizeText = size,
        }).ToList();
        return (new GameMetadata { OriginalSize = installed }, links, links.Select(DownloadFileSpec.FromLink).ToList());
    }

    private static AppSettings Settings(LowDiskMode mode = LowDiskMode.Never) => new()
    {
        LibraryRoot = Path.GetTempPath(),
        TempRoot = Path.GetTempPath(),
        LowDiskSpaceMode = mode,
    };

    [Fact]
    public void Unknown_size_gives_no_estimate()
    {
        var links = new List<DownloadLink> { new() { Url = "https://host.example/g.zip", FileName = "g.zip" } };
        var files = links.Select(DownloadFileSpec.FromLink).ToList();
        Assert.Null(SpaceEstimator.Estimate(new GameMetadata(), DownloadTransport.Http, links, files, Settings()));
    }

    [Fact]
    public void Archives_on_one_drive_peak_at_the_bigger_of_the_two_phases()
    {
        var (game, links, files) = Rar(4, "2 GB", "30 GB"); // 8 GB of archives, ~9.2 GB unpacked, 30 GB installed
        var estimate = SpaceEstimator.Estimate(game, DownloadTransport.Http, links, files, Settings())!;

        Assert.Equal(8 * Gb, estimate.DownloadBytes);
        Assert.True(estimate.InstalledKnown);
        Assert.False(estimate.Streaming);
        // setup phase (setup files + installed game) beats archives + unpacked
        Assert.Equal(estimate.ExtractedBytes + 30 * Gb, estimate.LibraryDriveNeeded);
    }

    [Fact]
    public void Low_disk_space_mode_drops_the_extract_phase_to_unpacked_plus_two_volumes()
    {
        var (game, links, files) = Rar(4, "2 GB", null);
        var estimate = SpaceEstimator.Estimate(game, DownloadTransport.Http, links, files, Settings(LowDiskMode.Always))!;

        Assert.True(estimate.Streaming);
        Assert.False(estimate.InstalledKnown);
        // installed unknown counts as the unpacked size, so the setup phase (2 x unpacked) is the peak
        Assert.Equal(2 * estimate.ExtractedBytes, estimate.LibraryDriveNeeded);
    }

    [Fact]
    public void Plain_setup_files_are_moved_not_unpacked()
    {
        var links = new List<DownloadLink>
        {
            new() { Url = "https://host.example/setup.exe", FileName = "setup.exe", SizeText = "10 GB" },
            new() { Url = "https://host.example/setup-1.bin", FileName = "setup-1.bin", SizeText = "10 GB" },
        };
        var files = links.Select(DownloadFileSpec.FromLink).ToList();
        var estimate = SpaceEstimator.Estimate(new GameMetadata { OriginalSize = "15 GB" }, DownloadTransport.Http, links, files, Settings())!;

        Assert.Equal(estimate.DownloadBytes, estimate.ExtractedBytes);
        Assert.Equal(20 * Gb + 15 * Gb, estimate.LibraryDriveNeeded);
    }

    [Fact]
    public void Warns_when_free_space_is_short()
    {
        var estimate = new SpaceEstimate(10 * Gb, 11 * Gb, 20 * Gb, true, false, true, 31 * Gb, 31 * Gb, 20 * Gb, 20 * Gb)
        {
            LibraryRoot = Path.GetTempPath(),
            TempRoot = Path.GetTempPath(),
        };
        Assert.True(estimate.IsShort);
        Assert.NotNull(estimate.Warning);

        var roomy = estimate with { LibraryFree = 100 * Gb, TempFree = 100 * Gb };
        Assert.False(roomy.IsShort);
        Assert.Null(roomy.Warning);
    }
}
