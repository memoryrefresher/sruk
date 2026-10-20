using SeaRaider.Core.Models;
using SeaRaider.Core.Scraping;
using SeaRaider.Core.WebChallenge;

namespace SeaRaider.Core.Tests;

public class MetadataAndSearchTests
{
    // ---- title cleaning -------------------------------------------------------------------------

    [Theory]
    [InlineData("Portal Dungeon: Goblin Escape - v1.1", "Portal Dungeon: Goblin Escape", "1.1", null)]
    [InlineData("Cyberpunk 2077: Ultimate Edition – v2.12 + 4 DLCs + Bonus Content", "Cyberpunk 2077: Ultimate Edition", "2.12", "4 DLCs")]
    [InlineData("Elden Ring: Deluxe Edition (v1.10.1 + DLC, MULTi14) [FitGirl Repack]", "Elden Ring: Deluxe Edition", "1.10.1", "DLC included")]
    [InlineData("Hades II – Build 18923104 – Free Download", "Hades II", "Build 18923104", null)]
    [InlineData("V Rising (v1.0.5) incl. DLC", "V Rising", "1.0.5", "DLC included")]
    [InlineData("Stardew Valley (1.6.8) [GOG]", "Stardew Valley", "1.6.8", null)]
    [InlineData("Rune II: Decapitation Edition", "Rune II: Decapitation Edition", null, null)]
    [InlineData("Half-Life 2: Episode Two", "Half-Life 2: Episode Two", null, null)]
    [InlineData("F.E.A.R. Platinum", "F.E.A.R. Platinum", null, null)]
    public void Cleans_titles(string raw, string title, string? version, string? dlc)
    {
        var parts = MetadataParser.CleanTitle(raw, "FitGirl Repacks");
        Assert.Equal(title, parts.Title);
        Assert.Equal(version, parts.Version);
        Assert.Equal(dlc, parts.DlcInfo);
    }

    [Fact]
    public void Title_language_hint_reads_multi_count() =>
        Assert.Equal("14 languages", MetadataParser.CleanTitle("Elden Ring (v1.10.1, MULTi14)").LanguageHint);

    // ---- labelled fields ------------------------------------------------------------------------

    [Fact]
    public void Splits_several_labels_on_one_line()
    {
        var fields = MetadataParser.ExtractLabeledFields(["Genre: RPG Release Date: 12th March 2024 Version: v1.0.5"]);
        Assert.Equal(
            new[] { ("genre", "RPG"), ("release date", "12th March 2024"), ("version", "v1.0.5") },
            fields);
    }

    [Fact]
    public void Prefers_the_longer_label()
    {
        var fields = MetadataParser.ExtractLabeledFields(["Repack Size: from 5.21 GB [Selective Download]", "Genres/Tags: Action, 3D"]);
        Assert.Equal("repack size", fields[0].Label);
        Assert.Equal("genres/tags", fields[1].Label);
    }

    // ---- value normalisers ----------------------------------------------------------------------

    [Theory]
    [InlineData("from 5.21 GB [Selective Download]", "from 5.21 GB")]
    [InlineData("3,4 GiB", "3.4 GB")]
    [InlineData("12.5 GB", "12.5 GB")]
    [InlineData("~800 MB", "from 800 MB")]
    [InlineData("unknown", null)]
    public void Normalises_sizes(string text, string? expected) => Assert.Equal(expected, MetadataParser.NormalizeSize(text));

    [Theory]
    [InlineData("Yes", "DLC included")]
    [InlineData("All", "All DLCs")]
    [InlineData("None", null)]
    [InlineData("No", null)]
    public void Normalises_dlc(string text, string? expected) => Assert.Equal(expected, MetadataParser.NormalizeDlc(text));

    [Theory]
    [InlineData("12th March 2024", 2024, 3, 12)]
    [InlineData("Mar 12, 2024", 2024, 3, 12)]
    [InlineData("March 5, 2024 (Steam)", 2024, 3, 5)]
    [InlineData("2024-03-12", 2024, 3, 12)]
    [InlineData("12.03.2024", 2024, 3, 12)]
    public void Parses_dates(string text, int y, int m, int d)
    {
        var parsed = MetadataParser.ParseDate(text);
        Assert.NotNull(parsed);
        Assert.Equal(new DateTime(y, m, d), parsed!.Value.Date);
    }

    [Theory]
    [InlineData("")]
    [InlineData("TBA")]
    public void Unparseable_dates_are_null(string text) => Assert.Null(MetadataParser.ParseDate(text));

    [Fact]
    public void Parses_language_codes_and_multi() =>
        Assert.Equal(new[] { "English", "10 languages" }, MetadataParser.ParseLanguages("ENG/MULTI10"));

    [Fact]
    public void Parses_language_names() =>
        Assert.Equal(new[] { "English", "French", "German" }, MetadataParser.ParseLanguages("English, French and German"));

    // ---- challenge detection --------------------------------------------------------------------

    [Fact]
    public void Cloudflare_header_is_a_bot_check() =>
        Assert.Equal(ChallengeKind.BotCheck, ChallengeDetector.Detect(403, "challenge", false, null, false));

    [Fact]
    public void Just_a_moment_page_is_a_bot_check() =>
        Assert.Equal(ChallengeKind.BotCheck,
            ChallengeDetector.Detect(503, null, true, "<html><title>Just a moment...</title></html>", false));

    [Fact]
    public void Recaptcha_on_file_link_is_a_captcha() =>
        Assert.Equal(ChallengeKind.Captcha,
            ChallengeDetector.Detect(200, null, true, "<div class=\"g-recaptcha\"></div>", expectFile: true));

    [Fact]
    public void Html_instead_of_file_is_a_landing_page() =>
        Assert.Equal(ChallengeKind.LandingPage,
            ChallengeDetector.Detect(200, null, true, "<html><a href='/get'>Download</a></html>", expectFile: true));

    [Fact]
    public void Normal_pages_are_left_alone()
    {
        Assert.Equal(ChallengeKind.None, ChallengeDetector.Detect(200, null, true, "<div class=\"g-recaptcha\"></div>", false));
        Assert.Equal(ChallengeKind.None, ChallengeDetector.Detect(404, null, true, "<title>Just a moment</title>", false));
        Assert.Equal(ChallengeKind.None, ChallengeDetector.Detect(200, null, false, null, true));
    }

    // ---- sorting and matching -------------------------------------------------------------------

    private static CatalogEntry Entry(string title, DateTime? uploaded = null, DateTime? released = null) =>
        new() { Id = title, Title = title, UploadedAtUtc = uploaded, ReleaseDateUtc = released };

    private static readonly CatalogEntry[] Sample =
    [
        Entry("Alpha", uploaded: new DateTime(2024, 1, 1), released: new DateTime(2020, 1, 1)),
        Entry("Bravo", uploaded: null, released: new DateTime(2023, 1, 1)),
        Entry("Charlie", uploaded: new DateTime(2025, 6, 1), released: null),
        Entry("Delta", uploaded: new DateTime(2022, 3, 1), released: new DateTime(2018, 1, 1)),
    ];

    [Theory]
    [InlineData(CatalogSort.UploadNewest, "Charlie,Alpha,Delta,Bravo")]
    [InlineData(CatalogSort.UploadOldest, "Delta,Alpha,Charlie,Bravo")]
    [InlineData(CatalogSort.ReleaseNewest, "Bravo,Alpha,Delta,Charlie")]
    [InlineData(CatalogSort.ReleaseOldest, "Delta,Alpha,Bravo,Charlie")]
    [InlineData(CatalogSort.TitleAscending, "Alpha,Bravo,Charlie,Delta")]
    public void Sorts_with_undated_entries_last(CatalogSort sort, string expected) =>
        Assert.Equal(expected, string.Join(",", Sample.Sort(sort).Select(e => e.Title)));

    [Fact]
    public void Every_search_word_must_match_title_genre_or_developer()
    {
        var entry = new CatalogEntry { Title = "Hollow Depths", Genres = ["Metroidvania"], Developer = "Tidewater" };
        Assert.True(entry.Matches(CatalogSorting.SplitWords("hollow metroid")));
        Assert.True(entry.Matches(CatalogSorting.SplitWords("tidewater")));
        Assert.False(entry.Matches(CatalogSorting.SplitWords("hollow racing")));
    }
}
