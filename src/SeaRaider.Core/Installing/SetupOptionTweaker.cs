using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using SeaRaider.Core.Installing.Native;

namespace SeaRaider.Core.Installing;

/// <summary>
/// Turns off the "low RAM" option on the first page of repack setups ("Limit installer to 2 GB of RAM usage" and the
/// like). It makes unpacking much slower and is only needed on PCs with very little memory.
///
/// <para>Runs inside the setup helper (<see cref="SetupRunner"/>), which has the same administrator rights as the setup,
/// so it can read and click the setup's controls (Windows blocks that from a normal program). Each check box is
/// handled once: unticked if it starts ticked, then left alone, so ticking it again by hand sticks.</para>
/// </summary>
public static partial class SetupOptionTweaker
{
    // "Limit installer to 2 GB of RAM usage", "Low RAM mode", "Reduce memory usage", "Use less RAM (slower)" …
    [GeneratedRegex(@"\b(?:ram|memory)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RamWordRegex();

    [GeneratedRegex(@"\b(?:limit\w*|low(?:er)?|reduc\w*|sav(?:e|ing)|less|minimi[sz]\w*|\d+(?:[.,]\d+)?\s*[gm]i?b)\b", RegexOptions.IgnoreCase)]
    private static partial Regex LowRamQualifierRegex();

    /// <summary>Is this check box label a "use less memory" option?</summary>
    public static bool IsLowRamOption(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return false;
        var text = label.Replace("&", string.Empty, StringComparison.Ordinal); // keyboard-accelerator markers
        return text.Length <= 160 && RamWordRegex().IsMatch(text) && LowRamQualifierRegex().IsMatch(text);
    }

    private const uint WmGetText = 0x000D;
    private const uint WmCommand = 0x0111;
    private const uint BmGetCheck = 0x00F0;
    private const uint BmClick = 0x00F5;
    private const int BstChecked = 1;
    private const int BnClicked = 0;
    private const int GwlStyle = -16;
    private const uint SmtoAbortIfHung = 0x0002;
    private const uint TimeoutMs = 500;

    /// <summary>
    /// One look at the setup's windows. Unticks low-RAM check boxes that are ticked and haven't been handled yet.
    /// </summary>
    /// <param name="processIds">The setup and everything it started.</param>
    /// <param name="handled">Check boxes already dealt with (kept by the caller between calls).</param>
    /// <param name="log">Where to note what was changed.</param>
    [SupportedOSPlatform("windows")]
    public static void UncheckLowRam(IReadOnlySet<int> processIds, HashSet<IntPtr> handled, Action<string> log)
    {
        var topLevel = new List<IntPtr>();
        SetupProcessNative.EnumWindows((hwnd, _) =>
        {
            SetupProcessNative.GetWindowThreadProcessId(hwnd, out var pid);
            if (processIds.Contains((int)pid)) topLevel.Add(hwnd);
            return true;
        }, IntPtr.Zero);

        foreach (var window in topLevel)
        {
            var shown = SetupProcessNative.IsWindowVisible(window);
            var boxes = new List<IntPtr>();
            SetupProcessNative.EnumChildWindows(window, (child, _) =>
            {
                if (!handled.Contains(child) && IsCheckBox(child)) boxes.Add(child);
                return true;
            }, IntPtr.Zero);

            foreach (var box in boxes)
            {
                var label = Text(box);
                if (!IsLowRamOption(label)) continue;

                if (!IsChecked(box))
                {
                    // Already off. Once the page is on screen the person has seen it: from then on it's theirs.
                    if (shown && SetupProcessNative.IsWindowVisible(box)) handled.Add(box);
                    continue;
                }

                handled.Add(box);
                Untick(box);
                log(IsChecked(box)
                    ? $"Couldn't untick \"{label.Trim()}\""
                    : $"Unticked \"{label.Trim()}\"");
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static void Untick(IntPtr box)
    {
        // A real click: the setup sees it exactly as if the person had clicked (its own handlers run).
        Send(box, BmClick, IntPtr.Zero, IntPtr.Zero);
        if (!IsChecked(box)) return;

        // Hidden pages (silent mode) can ignore a click; Delphi/Inno check boxes also toggle on the parent's BN_CLICKED.
        var parent = SetupProcessNative.GetParent(box);
        if (parent == IntPtr.Zero) return;
        var id = SetupProcessNative.GetDlgCtrlID(box);
        Send(parent, WmCommand, new IntPtr((BnClicked << 16) | (id & 0xFFFF)), box);
    }

    [SupportedOSPlatform("windows")]
    private static bool IsCheckBox(IntPtr hwnd)
    {
        // BS_CHECKBOX, BS_AUTOCHECKBOX, BS_3STATE, BS_AUTO3STATE (Delphi's TCheckBox uses BS_3STATE).
        var type = SetupProcessNative.GetWindowLong(hwnd, GwlStyle) & 0xF;
        return type is 2 or 3 or 5 or 6;
    }

    [SupportedOSPlatform("windows")]
    private static bool IsChecked(IntPtr box) => (Send(box, BmGetCheck, IntPtr.Zero, IntPtr.Zero).ToInt64() & 0x3) == BstChecked;

    [SupportedOSPlatform("windows")]
    internal static string Text(IntPtr hwnd)
    {
        // GetWindowText can't read another program's controls; WM_GETTEXT can.
        var buffer = new StringBuilder(512);
        return SetupProcessNative.SendMessageTimeoutText(hwnd, WmGetText, new IntPtr(buffer.Capacity), buffer, SmtoAbortIfHung, TimeoutMs, out _) != IntPtr.Zero
            ? buffer.ToString()
            : string.Empty;
    }

    [SupportedOSPlatform("windows")]
    internal static IntPtr Send(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam) =>
        SetupProcessNative.SendMessageTimeout(hwnd, message, wParam, lParam, SmtoAbortIfHung, TimeoutMs, out var result) != IntPtr.Zero
            ? result
            : IntPtr.Zero;
}
