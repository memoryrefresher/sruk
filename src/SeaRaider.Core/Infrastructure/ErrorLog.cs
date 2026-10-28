namespace SeaRaider.Core;

/// <summary>
/// Unexpected errors with their full details (where in SeaRaider it happened), in
/// %LOCALAPPDATA%\SeaRaider\logs\errors.log. The banner shows the short message; this file has the rest, so a crash like
/// "Object reference not set to an instance of an object" can be traced to the line that caused it. Kept under 1 MB
/// (the older half is moved to errors.old.log).
/// </summary>
public static class ErrorLog
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly object Gate = new();

    public static string Path => System.IO.Path.Combine(AppPaths.DataDirectory, "logs", "errors.log");

    /// <summary>Writes the error; never throws.</summary>
    public static void Write(Exception error, string? context = null)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                if (File.Exists(Path) && new FileInfo(Path).Length > MaxBytes)
                    File.Move(Path, System.IO.Path.ChangeExtension(Path, ".old.log"), overwrite: true);
                File.AppendAllText(Path,
                    $"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss} {context}{Environment.NewLine}{error}{Environment.NewLine}{Environment.NewLine}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // nowhere to write: the banner still shows the message
        }
    }
}
