using SeaRaider.Core.Downloads;
using SeaRaider.Core.Models;
using SeaRaider.Core.Scraping;

namespace SeaRaider.Core.Tests;

public class LinkClassifierTests
{
    [Theory]
    [InlineData("game.part01.rar", "game", 1)]
    [InlineData("game.part12.rar", "game", 12)]
    [InlineData("game.r00", "game", 2)]
    [InlineData("game.r05", "game", 7)]
    [InlineData("game.z01", "game", 1)]
    [InlineData("game.7z.001", "game.7z", 1)]
    [InlineData("Game.ZIP.003", "game.zip", 3)]
    public void Recognises_split_volume_names(string fileName, string expectedKey, int expectedPart)
    {
        Assert.True(LinkClassifier.TryParseVolume(fileName, out var key, out var part));
        Assert.Equal(expectedKey, key);
        Assert.Equal(expectedPart, part);
    }

    [Theory]
    [InlineData("game.zip")]
    [InlineData("game.rar")]
    [InlineData("setup.exe")]
    [InlineData("readme.txt")]
    public void Plain_files_are_not_volumes(string fileName) =>
        Assert.False(LinkClassifier.TryParseVolume(fileName, out _, out _));

    [Fact]
    public void Classifies_magnets_torrents_and_archives()
    {
        var magnet = LinkClassifier.Classify(new Uri("magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&dn=My+Game"), null);
        Assert.Equal(DownloadLinkKind.Magnet, magnet!.Kind);
        Assert.Equal("My Game", magnet.FileName);

        Assert.Equal(DownloadLinkKind.TorrentFile, LinkClassifier.Classify(new Uri("https://x.test/a/game.torrent"), "Torrent")!.Kind);
        Assert.Equal(DownloadLinkKind.DirectHttp, LinkClassifier.Classify(new Uri("https://x.test/files/game.7z"), null)!.Kind);
        Assert.Null(LinkClassifier.Classify(new Uri("https://x.test/about"), "About"));
        Assert.Null(LinkClassifier.Classify(new Uri("https://x.test/cover.jpg"), null, forceDownload: true));
        Assert.NotNull(LinkClassifier.Classify(new Uri("https://host.test/d/abc123"), "Download", forceDownload: true));
    }

    [Fact]
    public void Lone_rar_is_promoted_when_old_style_volumes_exist()
    {
        var links = new[] { "game.rar", "game.r00", "game.r01" }
            .Select(n => LinkClassifier.Classify(new Uri($"https://x.test/{n}"), null)!)
            .ToList();

        var normalized = LinkClassifier.NormalizeGroups(links);

        Assert.All(normalized, l => Assert.Equal(DownloadLinkKind.MultipartArchivePart, l.Kind));
        Assert.Equal(new[] { 1, 2, 3 }, normalized.OrderBy(l => l.PartNumber).Select(l => l.PartNumber));
    }

    [Fact]
    public void Planner_rejects_incomplete_sets_and_collapses_mirrors()
    {
        static DownloadLink Part(string host, int n) => new()
        {
            Url = $"https://{host}/game.part{n}.rar", Kind = DownloadLinkKind.MultipartArchivePart, GroupKey = "game", PartNumber = n,
        };

        Assert.Null(DownloadPlanner.CompleteSet([Part("a.test", 1), Part("a.test", 2), Part("a.test", 4)]));

        var set = DownloadPlanner.CompleteSet([Part("a.test", 1), Part("b.test", 1), Part("a.test", 2), Part("b.test", 2)]);
        Assert.NotNull(set);
        Assert.Equal(2, set!.Count);
    }

    [Fact]
    public void Planner_prefers_http_unless_torrents_are_preferred()
    {
        var game = new GameMetadata
        {
            Title = "Test",
            DownloadLinks =
            [
                new DownloadLink { Url = "https://x.test/game.zip", Kind = DownloadLinkKind.DirectHttp, FileName = "game.zip" },
                new DownloadLink { Url = "magnet:?xt=urn:btih:abc", Kind = DownloadLinkKind.Magnet },
            ],
        };

        Assert.Equal(DownloadTransport.Http, DownloadPlanner.Plan(game, null, preferTorrents: false).Transport);
        Assert.Equal(DownloadTransport.Torrent, DownloadPlanner.Plan(game, null, preferTorrents: true).Transport);
    }
}
