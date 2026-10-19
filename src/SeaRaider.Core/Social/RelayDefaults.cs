using System.Reflection;

namespace SeaRaider.Core.Social;

/// <summary>
/// The friends relay a SeaRaider build connects to out of the box.
/// <list type="number">
///   <item><c>SEARAIDER_RELAY</c> environment variable (testing, or pointing a whole PC elsewhere);</item>
///   <item>the <c>SeaRaiderDefaultRelay</c> build property, baked into the app as assembly metadata
///         (<c>dotnet publish -p:SeaRaiderDefaultRelay=wss://relay.example.com/ws</c>, or set it in Directory.Build.props),
///         so everyone running that build is on the same relay without any setup;</item>
///   <item>nothing: friends stay off until an address is entered in Settings › Friends.</item>
/// </list>
/// A custom address in Settings always wins.
/// </summary>
public static class RelayDefaults
{
    public const string MetadataKey = "SeaRaiderDefaultRelay";
    public const string EnvironmentVariable = "SEARAIDER_RELAY";

    private static readonly Lazy<string?> BuiltIn = new(ReadBuiltIn);

    /// <summary>The build's default relay address, or null when the build has none.</summary>
    public static string? DefaultRelayUrl =>
        Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } fromEnvironment ? fromEnvironment.Trim() : BuiltIn.Value;

    /// <summary>The address to use: the user's own, else the build default.</summary>
    public static string? Effective(string? userRelay) =>
        !string.IsNullOrWhiteSpace(userRelay) ? userRelay.Trim() : DefaultRelayUrl;

    private static string? ReadBuiltIn()
    {
        foreach (var assembly in new[] { Assembly.GetEntryAssembly(), typeof(RelayDefaults).Assembly }.OfType<Assembly>())
        {
            var value = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == MetadataKey)?.Value;
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }
        return null;
    }
}
