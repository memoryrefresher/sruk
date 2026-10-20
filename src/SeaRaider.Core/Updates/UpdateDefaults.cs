using System.Reflection;

namespace SeaRaider.Core.Updates;

/// <summary>
/// Where a SeaRaider build looks for updates, and the key their releases must be signed with. Both are baked into the app
/// at build time (Directory.Build.props: <c>SeaRaiderUpdateFeed</c>, <c>SeaRaiderUpdatePublicKey</c>), so everyone
/// running your build gets your updates and only yours. The <c>SEARAIDER_UPDATE_FEED</c> environment variable points one
/// PC at a test feed (the signature is still checked against the built-in key).
/// </summary>
public static class UpdateDefaults
{
    public const string FeedMetadataKey = "SeaRaiderUpdateFeed";
    public const string PublicKeyMetadataKey = "SeaRaiderUpdatePublicKey";
    public const string FeedEnvironmentVariable = "SEARAIDER_UPDATE_FEED";

    private static readonly Lazy<string?> BuiltInFeed = new(() => Read(FeedMetadataKey));
    private static readonly Lazy<string?> BuiltInKey = new(() => Read(PublicKeyMetadataKey));

    /// <summary>The address of the release file (latest.json), or null when this build has none.</summary>
    public static string? FeedUrl =>
        Environment.GetEnvironmentVariable(FeedEnvironmentVariable) is { Length: > 0 } test ? test.Trim() : BuiltInFeed.Value;

    /// <summary>The public key releases are checked against (base64 SubjectPublicKeyInfo), or null.</summary>
    public static string? PublicKey => BuiltInKey.Value;

    /// <summary>This build's version (Directory.Build.props &lt;Version&gt;), as major.minor.build.</summary>
    public static Version CurrentVersion { get; } = Normalize(
        (Assembly.GetEntryAssembly() ?? typeof(UpdateDefaults).Assembly).GetName().Version ?? new Version(0, 0, 0));

    public static Version Normalize(Version v) => new(Math.Max(0, v.Major), Math.Max(0, v.Minor), Math.Max(0, v.Build));

    private static string? Read(string key)
    {
        foreach (var assembly in new[] { Assembly.GetEntryAssembly(), typeof(UpdateDefaults).Assembly }.OfType<Assembly>())
        {
            var value = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value;
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }
        return null;
    }
}
