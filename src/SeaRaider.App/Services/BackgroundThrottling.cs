using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;

namespace SeaRaider.App.Services;

/// <summary>
/// Keeps downloads and extraction at full speed when SeaRaider isn't the window in front (after Alt+Tab, when it's behind a
/// game, or minimised). Two things slow a background app down, and both are switched off here:
/// <list type="bullet">
///   <item><b>Windows efficiency mode (EcoQoS)</b>: Windows 10/11 puts a process whose window isn't in front on slower, lower-power
///         CPU cores and clocks, and coarsens its timers. <see cref="KeepFullSpeed"/> opts SeaRaider out.</item>
///   <item><b>The built-in browser's own throttling</b>: Chromium slows hidden and covered windows. SeaRaider's download browser is
///         parked off-screen or hidden while a file comes down, so <see cref="WebView2Arguments"/> switches that off.</item>
/// </list>
/// </summary>
internal static class BackgroundThrottling
{
    private const int ProcessPowerThrottling = 4; // PROCESS_INFORMATION_CLASS: ProcessPowerThrottling
    private const uint CurrentVersion = 1; // PROCESS_POWER_THROTTLING_CURRENT_VERSION
    private const uint ExecutionSpeed = 0x1; // PROCESS_POWER_THROTTLING_EXECUTION_SPEED
    private const uint IgnoreTimerResolution = 0x4; // PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION (Windows 11)

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessPowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(IntPtr process, int informationClass, ref ProcessPowerThrottlingState information, int size);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    /// <summary>
    /// Opts this process out of power throttling: a bit set in ControlMask with the same bit clear in StateMask means "never
    /// throttle this". Safe to call again and again; does nothing (and never throws) where Windows doesn't know the setting.
    /// </summary>
    public static void KeepFullSpeed()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            // Timer resolution first (Windows 11 only: older builds reject the unknown bit), then plain CPU speed on its own.
            if (Apply(ExecutionSpeed | IgnoreTimerResolution)) return;
            Apply(ExecutionSpeed);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            // an old Windows without this setting: nothing to switch off
        }
    }

    private static bool Apply(uint controlMask)
    {
        var state = new ProcessPowerThrottlingState { Version = CurrentVersion, ControlMask = controlMask, StateMask = 0 };
        return SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state, Marshal.SizeOf<ProcessPowerThrottlingState>());
    }

    /// <summary>
    /// Browser switches that stop Chromium slowing a window that is hidden, covered or in the background. Every window that shares
    /// SeaRaider's download-browser profile must start with exactly these, or WebView2 refuses the second one.
    /// </summary>
    public const string WebView2Arguments =
        "--disable-background-timer-throttling --disable-renderer-backgrounding " +
        "--disable-backgrounding-occluded-windows --disable-features=CalculateNativeWinOcclusion";

    /// <summary>The options for the download, verification and page-fetch browsers (one shared profile).</summary>
    public static CoreWebView2EnvironmentOptions CreateWebView2Options() => new(WebView2Arguments);
}
