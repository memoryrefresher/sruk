using SeaRaider.Core;

namespace SeaRaider.App.Services;

/// <summary>
/// Knows whether this start is the very first one on this PC: the library database doesn't exist yet. Must be asked
/// (<see cref="Detect"/>) before anything opens the database, i.e. at the top of <c>Main</c>.
/// </summary>
internal static class FirstRunDetector
{
    /// <summary>True when SeaRaider started with no library database on disk (a brand-new install).</summary>
    public static bool IsFreshInstall { get; private set; }

    public static void Detect()
    {
        try
        {
            IsFreshInstall = !File.Exists(AppPaths.DatabasePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            IsFreshInstall = false; // can't tell: never nag an existing install with the welcome questions
        }
    }
}
