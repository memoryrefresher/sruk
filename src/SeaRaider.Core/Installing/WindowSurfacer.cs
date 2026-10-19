using SeaRaider.Core.Installing.Native;

namespace SeaRaider.Core.Installing;

/// <summary>A top-level window that belongs to a setup process.</summary>
public sealed record SetupWindow(IntPtr Handle, int ProcessId, string Title, string ClassName, bool Visible);

/// <summary>What <see cref="WindowSurfacer.Surface"/> managed to do.</summary>
/// <param name="Shown">Windows brought to the front (or revealed).</param>
/// <param name="Title">Title of the main window that was surfaced, if any.</param>
public sealed record SurfaceResult(int Shown, string? Title)
{
    public bool Any => Shown > 0;
}

/// <summary>
/// Brings a stuck setup's windows to the front. Background processes may not take the foreground,
/// so a wizard (or a message box) started from SeaRaider often opens behind other windows, or minimized,
/// while its music plays. Windows that setups create but leave hidden under /VERYSILENT are revealed
/// only for known wizard and dialog classes, so internal helper windows stay hidden.
/// </summary>
public static class WindowSurfacer
{
    /// <summary>Window classes worth revealing when hidden: dialogs, Inno/NSIS/MSI wizards.</summary>
    private static readonly HashSet<string> RevealableClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "#32770",                 // standard dialogs and message boxes (also the NSIS main window)
        "TWizardForm", "TSetupForm", "TSelectLanguageForm", "TNewDiskForm", "TMainForm", // Inno Setup (+ repack forms)
        "MsiDialogCloseClass", "MsiDialogNoCloseClass", // Windows Installer UI
    };

    public static List<SetupWindow> FindWindows(IReadOnlySet<int> processIds)
    {
        var windows = new List<SetupWindow>();
        if (!OperatingSystem.IsWindows() || processIds.Count == 0) return windows;

        SetupProcessNative.EnumWindows((hwnd, _) =>
        {
            SetupProcessNative.GetWindowThreadProcessId(hwnd, out var pid);
            if (processIds.Contains((int)pid))
            {
                windows.Add(new SetupWindow(hwnd, (int)pid, SetupProcessNative.WindowText(hwnd),
                    SetupProcessNative.ClassName(hwnd), SetupProcessNative.IsWindowVisible(hwnd)));
            }
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    /// <summary>
    /// Shows the setup's windows: visible ones are restored and raised (message boxes first, as they're what
    /// it's waiting on); when none are visible and <paramref name="revealHidden"/> is set, hidden wizard and dialog
    /// windows are shown.
    /// </summary>
    public static SurfaceResult Surface(IReadOnlySet<int> processIds, bool revealHidden)
    {
        var windows = FindWindows(processIds);
        var visible = windows.Where(w => w.Visible && w.Title.Length > 0).ToList();

        if (visible.Count == 0 && revealHidden)
        {
            foreach (var hidden in windows.Where(w => !w.Visible && RevealableClasses.Contains(w.ClassName)))
            {
                SetupProcessNative.ShowWindow(hidden.Handle, SetupProcessNative.SwShow);
                visible.Add(hidden with { Visible = true });
            }
        }
        if (visible.Count == 0) return new SurfaceResult(0, null);

        // Raise the main windows first and dialogs last, so the question ends up on top.
        var ordered = visible.OrderBy(w => w.ClassName == "#32770" ? 1 : 0).ToList();
        foreach (var window in ordered) BringToFront(window.Handle);

        var main = ordered.FirstOrDefault(w => w.ClassName != "#32770") ?? ordered[0];
        return new SurfaceResult(ordered.Count, main.Title);
    }

    /// <summary>Restores and raises a window, working around the foreground lock.</summary>
    public static void BringToFront(IntPtr hwnd)
    {
        if (!OperatingSystem.IsWindows()) return;

        if (SetupProcessNative.IsIconic(hwnd)) SetupProcessNative.ShowWindow(hwnd, SetupProcessNative.SwRestore);

        // Windows only lets the foreground process change the foreground window. Attaching to the current
        // foreground thread's input (and a synthetic Alt press) lifts that restriction for this call.
        var foreground = SetupProcessNative.GetForegroundWindow();
        var foregroundThread = SetupProcessNative.GetWindowThreadProcessId(foreground, out _);
        var thisThread = SetupProcessNative.GetCurrentThreadId();
        var attached = foregroundThread != 0 && foregroundThread != thisThread &&
                       SetupProcessNative.AttachThreadInput(thisThread, foregroundThread, true);
        try
        {
            SetupProcessNative.keybd_event(SetupProcessNative.VkMenu, 0, 0, UIntPtr.Zero);
            SetupProcessNative.keybd_event(SetupProcessNative.VkMenu, 0, SetupProcessNative.KeyeventfKeyup, UIntPtr.Zero);
            SetupProcessNative.BringWindowToTop(hwnd);
            SetupProcessNative.SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached) SetupProcessNative.AttachThreadInput(thisThread, foregroundThread, false);
        }

        // If the foreground still couldn't be taken (e.g. the setup runs as administrator), flash it in the taskbar.
        if (SetupProcessNative.GetForegroundWindow() != hwnd)
        {
            var info = new SetupProcessNative.FlashWindowInfo
            {
                cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<SetupProcessNative.FlashWindowInfo>(),
                hwnd = hwnd,
                dwFlags = SetupProcessNative.FlashwAll | SetupProcessNative.FlashwTimerNoFg,
                uCount = 0,
                dwTimeout = 0,
            };
            SetupProcessNative.FlashWindowEx(ref info);
        }
    }
}
