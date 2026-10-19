namespace SeaRaider.Core;

/// <summary>Named <see cref="HttpClient"/> registrations (see <c>ServiceCollectionExtensions</c>).</summary>
public static class HttpClientNames
{
    /// <summary>Short timeouts, HTML/XML accept headers — for page and feed fetches.</summary>
    public const string Scraper = "searaider-scraper";

    /// <summary>No overall timeout (multi-GB files); per-read stall detection happens in the downloader.</summary>
    public const string Downloads = "searaider-downloads";

    /// <summary>Public JSON APIs (Steam store, IGDB): short timeout, no verification window.</summary>
    public const string Metadata = "searaider-metadata";
}
