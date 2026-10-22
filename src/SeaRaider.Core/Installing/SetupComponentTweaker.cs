using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using SeaRaider.Core.Installing.Native;

namespace SeaRaider.Core.Installing;

/// <summary>
/// Unticks the runtime installers on a setup's "Select Components" page ("Download and Install C++ Redist 2010 SP1 x64",
/// "Update DirectX", "Install .NET Framework 3.5" …). SeaRaider handles those itself (Settings › Setup programs › Runtimes:
/// it closes their windows and installs them quietly, or skips them), so the setup's own copies would only start, get
/// stopped, and leave the setup waiting or complaining.
///
/// <para>The page is Inno Setup's component list (a single "TNewCheckListBox" control drawing its own tick boxes, not
/// separate check boxes, so <see cref="SetupOptionTweaker"/> can't see them). Each item's name and whether it's ticked
/// are read through Windows' accessibility interface (the one screen readers use, which Inno Setup provides); an item is
/// only touched when it's both a runtime and ticked, and only once, so ticking it again by hand sticks. A list whose
/// ticks can't be read is left alone.</para>
/// </summary>
public static partial class SetupComponentTweaker
{
    // "C++ Redist", "Visual C++ 2010", "VCRedist", "DirectX", ".NET Framework 3.5", "XNA Framework", "PhysX", "OpenAL" …
    [GeneratedRegex(@"c\+\+|\bvc\s*\+*\s*redist|\bvcredist|visual\s*c\b|\bredist(?:ributable)?s?\b|\bdirect\s*x\b|\bdx\s*(?:9|10|11|12)\b|\.net\s*(?:framework|runtime|desktop)?|\bnet\s*framework\b|\bxna\b|\bphysx\b|\bopenal\b|\bwindows\s*media\s*feature|\bupdate\s*runtimes?\b|\binstall\s*runtimes?\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex RuntimeComponentRegex();

    /// <summary>Is this component a runtime installer SeaRaider looks after itself?</summary>
    public static bool IsRuntimeComponent(string? label) =>
        !string.IsNullOrWhiteSpace(label) && label.Length <= 200 && RuntimeComponentRegex().IsMatch(label.Replace("&", string.Empty, StringComparison.Ordinal));

    private const uint ObjIdClient = 0xFFFFFFFC;
    private const int StateChecked = 0x10;
    private const int StateMixed = 0x20;
    private const uint LbSetCurSel = 0x0186;
    private const uint WmChar = 0x0102;
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const int VkSpace = 0x20;
    private static readonly Guid IidAccessible = new("618736E0-3C3D-11CF-810C-00AA00389B71");

    /// <summary>The part of IAccessible used here (in its vtable order; the rest isn't called).</summary>
    [ComImport, Guid("618736E0-3C3D-11CF-810C-00AA00389B71"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IAccessible
    {
        [return: MarshalAs(UnmanagedType.IDispatch)] object get_accParent();
        int get_accChildCount();
        [return: MarshalAs(UnmanagedType.IDispatch)] object get_accChild(object child);
        [return: MarshalAs(UnmanagedType.BStr)] string get_accName(object child);
        [return: MarshalAs(UnmanagedType.BStr)] string get_accValue(object child);
        [return: MarshalAs(UnmanagedType.BStr)] string get_accDescription(object child);
        object get_accRole(object child);
        object get_accState(object child);
    }

    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromWindow(IntPtr hwnd, uint objectId, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out object accessible);

    /// <summary>
    /// One look at the setup's windows: unticks ticked runtime components on a component list that's on screen.
    /// </summary>
    /// <param name="processIds">The setup and everything it started.</param>
    /// <param name="handled">List items already dealt with (kept by the caller between calls).</param>
    /// <param name="log">Where to note what was changed.</param>
    [SupportedOSPlatform("windows")]
    public static void UncheckRuntimes(IReadOnlySet<int> processIds, HashSet<(IntPtr List, int Item)> handled, Action<string> log)
    {
        foreach (var list in ComponentLists(processIds))
        {
            if (Accessible(list) is not { } acc) continue;
            int count;
            try { count = acc.get_accChildCount(); }
            catch (COMException) { continue; }

            for (var item = 1; item <= Math.Min(count, 500); item++)
            {
                if (handled.Contains((list, item))) continue;
                string name;
                int state;
                try
                {
                    name = acc.get_accName(item) ?? string.Empty;
                    state = Convert.ToInt32(acc.get_accState(item));
                }
                catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
                {
                    continue;
                }
                if (!IsRuntimeComponent(name)) continue;

                handled.Add((list, item)); // once: what the person does with it afterwards stays
                if ((state & (StateChecked | StateMixed)) == 0) continue; // already off

                Toggle(list, item - 1);
                var after = ReadState(acc, item);
                log(after is { } s && (s & (StateChecked | StateMixed)) == 0
                    ? $"Unticked component \"{name.Trim()}\" (SeaRaider handles runtimes itself)"
                    : $"Couldn't untick component \"{name.Trim()}\"");
            }
        }
    }

    /// <summary>The setup's visible component lists (Inno Setup's TNewCheckListBox, or another check list box).</summary>
    [SupportedOSPlatform("windows")]
    private static List<IntPtr> ComponentLists(IReadOnlySet<int> processIds)
    {
        var lists = new List<IntPtr>();
        SetupProcessNative.EnumWindows((window, _) =>
        {
            SetupProcessNative.GetWindowThreadProcessId(window, out var pid);
            if (!processIds.Contains((int)pid) || !SetupProcessNative.IsWindowVisible(window)) return true;
            SetupProcessNative.EnumChildWindows(window, (child, _) =>
            {
                if (SetupProcessNative.IsWindowVisible(child) && ClassOf(child).Contains("CheckListBox", StringComparison.OrdinalIgnoreCase))
                    lists.Add(child);
                return true;
            }, IntPtr.Zero);
            return true;
        }, IntPtr.Zero);
        return lists;
    }

    /// <summary>Selects the item and presses Space on it: the list toggles its tick exactly as for the person.</summary>
    [SupportedOSPlatform("windows")]
    private static void Toggle(IntPtr list, int index)
    {
        SetupOptionTweaker.Send(list, LbSetCurSel, new IntPtr(index), IntPtr.Zero);
        SetupOptionTweaker.Send(list, WmChar, new IntPtr(VkSpace), new IntPtr(0x00390001));
        if (Accessible(list) is { } acc && ReadState(acc, index + 1) is { } state && (state & (StateChecked | StateMixed)) == 0) return;
        // Some lists toggle on the key itself rather than the character.
        SetupOptionTweaker.Send(list, WmKeyDown, new IntPtr(VkSpace), new IntPtr(0x00390001));
        SetupOptionTweaker.Send(list, WmKeyUp, new IntPtr(VkSpace), unchecked(new IntPtr((int)0xC0390001)));
    }

    private static int? ReadState(IAccessible acc, int item)
    {
        try { return Convert.ToInt32(acc.get_accState(item)); }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException) { return null; }
    }

    [SupportedOSPlatform("windows")]
    private static IAccessible? Accessible(IntPtr hwnd)
    {
        var iid = IidAccessible;
        try
        {
            return AccessibleObjectFromWindow(hwnd, ObjIdClient, ref iid, out var obj) == 0 ? obj as IAccessible : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static string ClassOf(IntPtr hwnd)
    {
        var name = new StringBuilder(128);
        return SetupProcessNative.GetClassName(hwnd, name, name.Capacity) > 0 ? name.ToString() : string.Empty;
    }
}
