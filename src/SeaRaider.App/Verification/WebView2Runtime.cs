using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using SeaRaider.Core;

namespace SeaRaider.App.Verification;

/// <summary>
/// Finds, checks and (if allowed) installs the Microsoft Edge WebView2 Runtime for the verification window.
///
/// <para><b>Why "The system cannot find the file specified (0x80070002)".</b> WebView2 reports ERROR_FILE_NOT_FOUND when
/// the runtime isn't installed — and also when Windows still lists a runtime whose files are gone (an interrupted
/// update or a half-removed install), or when the browser process can't start from the app's profile folder. The
/// first case surfaces as <see cref="WebView2RuntimeNotFoundException"/>; the others as a raw COMException while
/// creating the browser control, which is the error SeaRaider showed.</para>
///
/// <para>Recovery, in order (see <see cref="CaptchaResolverWindow"/>):</para>
/// <list type="number">
///   <item>Prefer a <b>fixed-version runtime</b> shipped next to SeaRaider (<c>WebView2Runtime\</c>) or unpacked to
///         <c>%LOCALAPPDATA%\SeaRaider\webview2-runtime\</c>, when present.</item>
///   <item>Retry with a <b>fresh profile folder</b> (a damaged profile gives the same error).</item>
///   <item><b>Install / repair the Evergreen runtime</b> with Microsoft's bootstrapper (<c>/silent /install</c>, per-user
///         without admin rights), then retry.</item>
///   <item>Fall back to the <b>system browser</b> (<see cref="BrowserHandoffWindow"/>).</item>
/// </list>
/// Every failure is written to <c>%LOCALAPPDATA%\SeaRaider\logs\webview2.log</c> with the runtime version and paths.
/// </summary>
internal static class WebView2Runtime
{
    /// <summary>Microsoft's Evergreen bootstrapper (a ~2 MB downloader that installs or repairs the runtime).</summary>
    private const string BootstrapperUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

    public static string LogPath => Path.Combine(AppPaths.DataDirectory, "logs", "webview2.log");

    /// <summary>Installed Evergreen runtime version, or null when none is registered.</summary>
    public static string? InstalledVersion(string? browserFolder = null)
    {
        try
        {
            return CoreWebView2Environment.GetAvailableBrowserVersionString(browserFolder);
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or COMException or DllNotFoundException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>A fixed-version runtime folder (one containing msedgewebview2.exe), if SeaRaider ships or was given one.</summary>
    public static string? FindFixedVersionFolder()
    {
        foreach (var root in new[]
                 {
                     Path.Combine(AppContext.BaseDirectory, "WebView2Runtime"),
                     Path.Combine(AppPaths.DataDirectory, "webview2-runtime"),
                 })
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                var exe = Directory.EnumerateFiles(root, "msedgewebview2.exe",
                    new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true }).FirstOrDefault();
                if (exe is not null) return Path.GetDirectoryName(exe);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // try the next one
            }
        }
        return null;
    }

    /// <summary>
    /// Errors that mean "the runtime (or its files) can't be found / started", as opposed to page or network errors.
    /// </summary>
    public static bool IsRuntimeProblem(Exception ex) => ex switch
    {
        WebView2RuntimeNotFoundException => true,
        FileNotFoundException or DirectoryNotFoundException or DllNotFoundException => true,
        COMException com => (uint)com.HResult is 0x80070002 or 0x80070003 or 0x80070005 or 0x8007139F or 0x80004004,
        _ => (uint)ex.HResult is 0x80070002 or 0x80070003,
    };

    /// <summary>A new, empty profile folder next to the usual one (the old one is kept for inspection).</summary>
    public static string FreshUserDataFolder(string current)
    {
        var fresh = $"{current}-{DateTime.Now:yyyyMMddHHmmss}";
        Directory.CreateDirectory(fresh);
        return fresh;
    }

    /// <summary>
    /// Downloads Microsoft's bootstrapper and installs / repairs the Evergreen runtime silently. Without admin rights it
    /// installs for the current user. Returns true when a runtime is available afterwards.
    /// </summary>
    public static async Task<bool> InstallEvergreenAsync(Action<string> status, CancellationToken cancellationToken)
    {
        var installer = Path.Combine(Path.GetTempPath(), "MicrosoftEdgeWebview2Setup.exe");
        try
        {
            status("Downloading the Microsoft Edge WebView2 Runtime installer…");
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("SeaRaider/0.10");
                var bytes = await http.GetByteArrayAsync(BootstrapperUrl, cancellationToken).ConfigureAwait(false);
                await File.WriteAllBytesAsync(installer, bytes, cancellationToken).ConfigureAwait(false);
            }

            status("Installing the WebView2 Runtime (about a minute)…");
            using var process = Process.Start(new ProcessStartInfo(installer, "/silent /install")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            }) ?? throw new InvalidOperationException("The WebView2 installer didn't start.");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(6));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            Log($"Bootstrapper exited with {process.ExitCode}");
            return InstalledVersion() is not null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException
                                       or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            Log($"Runtime install failed: {ex}");
            return false;
        }
        finally
        {
            try { File.Delete(installer); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}" +
                $"    runtime={InstalledVersion() ?? "none"} fixed={FindFixedVersionFolder() ?? "none"} " +
                $"os={Environment.OSVersion} x64={Environment.Is64BitProcess} elevated={IsElevated()}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // logging must never break verification
        }
    }

    private static bool IsElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
