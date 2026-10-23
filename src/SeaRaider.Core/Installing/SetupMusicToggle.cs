using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using SeaRaider.Core.Installing.Native;

namespace SeaRaider.Core.Installing;

/// <summary>
/// Switches a repack setup's music off with the setup's own speaker button (bottom left of its wizard), like a person
/// would. Muting in the volume mixer alone doesn't always hold: some setups play from a process outside their tree, or
/// raise their volume again.
///
/// <para>The speaker is usually drawn by the setup script, not a standard control, so it can't be found by name. Instead:
/// the mixer's level meter says whether the setup is making sound; while it is, the most likely spots are clicked one at a
/// time (small controls in the wizard's bottom-left corner, then the corner itself, level with the Cancel button), and
/// after each click the meter shows whether the music stopped. Once it has, or after a few tries, it's left alone — so
/// switching the music back on by hand sticks. Runs in the setup helper (same rights as the setup).</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class SetupMusicToggle(Func<int, bool>? alsoSetupProcess, Action<string> log)
{
    private const float Audible = 0.01f;
    private const float Silent = 0.002f;
    private const int MaxAttempts = 5;

    /// <summary>The click is posted; the meter needs a moment to show the setup's next (silent) audio.</summary>
    private static readonly TimeSpan IgnoreAfterClick = TimeSpan.FromMilliseconds(180);

    /// <summary>Silence for this long after a click = it worked (music never pauses this briefly on its own… usually).</summary>
    private static readonly TimeSpan SettleTime = TimeSpan.FromMilliseconds(450);

    /// <summary>After that, keep listening: a quiet bar in the music that happened to follow a wrong click comes back here.</summary>
    private static readonly TimeSpan ConfirmTime = TimeSpan.FromSeconds(2.5);

    /// <summary>The wizard's first page needs a moment to finish drawing before its controls can be found.</summary>
    private static readonly TimeSpan WizardShownFor = TimeSpan.FromMilliseconds(250);

    private const uint WmMouseMove = 0x0200;
    private const uint WmLButtonDown = 0x0201;
    private const uint WmLButtonUp = 0x0202;
    private const uint BmClick = 0x00F5;
    private const uint CwpSkipInvisible = 0x0001;
    private const uint CwpSkipTransparent = 0x0004;

    private enum Phase { Listening, Clicked, Confirming, Done }

    private readonly Queue<float> _recentPeaks = new();
    private readonly List<(TimeSpan At, float Peak)> _sinceClickPeaks = [];
    private readonly Stopwatch _sinceClick = new();
    private readonly Stopwatch _wizardVisible = new();
    private List<Target>? _targets;
    private IntPtr _wizard;
    private string? _signature;
    private int _attempt;
    private Phase _phase = Phase.Listening;

    [GeneratedRegex(@"^\s*&?(cancel|abbrechen|annuler|cancelar|annulla|отмена)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex CancelRegex();

    [GeneratedRegex(@"music|sound|audio|mute|♪|♫|🔊|🔇", RegexOptions.IgnoreCase)]
    private static partial Regex SoundLabelRegex();

    /// <summary>A place to click: a control (its centre) or a point in a window's client area.</summary>
    /// <param name="WizardX">The same spot in the wizard's client area (what's remembered for next time).</param>
    private sealed record Target(IntPtr Window, int X, int Y, bool IsButton, string Description, int WizardX, int WizardY);

    /// <summary>One look; the setup helper calls this about 12 times a second with the setup's process tree.</summary>
    public void Tick(IReadOnlySet<int> processIds)
    {
        if (_phase == Phase.Done) return;

        var peak = AudioSessionMuter.PeakOf(processIds, alsoSetupProcess);
        _recentPeaks.Enqueue(peak);
        while (_recentPeaks.Count > 4) _recentPeaks.Dequeue(); // ~⅓ s
        var loudest = _recentPeaks.Max();

        switch (_phase)
        {
            case Phase.Clicked:
                _sinceClickPeaks.Add((_sinceClick.Elapsed, peak));
                if (_sinceClick.Elapsed < IgnoreAfterClick + SettleTime) return;
                if (_sinceClickPeaks.Where(p => p.At >= IgnoreAfterClick).All(p => p.Peak < Silent))
                {
                    _phase = Phase.Confirming; // silent: looks like that was the button
                    _sinceClick.Restart();
                    return;
                }
                _phase = Phase.Listening; // still playing: not the button, try the next spot right away
                break;

            case Phase.Confirming:
                if (peak >= Audible)
                {
                    _phase = Phase.Listening; // the music came back: that was only a quiet moment
                    break;
                }
                if (_sinceClick.Elapsed < ConfirmTime) return;
                _phase = Phase.Done;
                var winner = _targets![_attempt - 1];
                log($"Switched the setup's music off with its own button ({winner.Description}, try {_attempt})");
                if (_signature is not null) MusicButtonMemory.Remember(_signature, winner.WizardX, winner.WizardY, ClientHeight(_wizard));
                return;
        }

        if (loudest < Audible) return; // nothing playing (yet), or it's already off

        if (!FindWizard(processIds)) return;
        if (_wizardVisible.Elapsed < WizardShownFor) return; // let the first page finish drawing

        _targets ??= FindTargets(_wizard, _signature);
        if (_attempt >= Math.Min(MaxAttempts, _targets.Count))
        {
            _phase = Phase.Done;
            log(_targets.Count == 0
                ? "Couldn't find the setup's music button"
                : "Couldn't switch the setup's music off with its button (kept muted in the volume mixer)");
            return;
        }

        Click(_targets[_attempt++]);
        _sinceClickPeaks.Clear();
        _sinceClick.Restart();
        _phase = Phase.Clicked;
    }

    private static int ClientHeight(IntPtr window) => SetupProcessNative.GetClientRect(window, out var client) ? client.Height : 0;

    private bool FindWizard(IReadOnlySet<int> processIds)
    {
        if (_wizard != IntPtr.Zero && SetupProcessNative.IsWindowVisible(_wizard)) return true;

        _wizard = IntPtr.Zero;
        _targets = null;
        _signature = null;
        _wizardVisible.Reset();
        IntPtr best = IntPtr.Zero;
        var bestScore = 0L;
        foreach (var window in WindowSurfacer.FindWindows(processIds))
        {
            if (!window.Visible || !SetupProcessNative.GetClientRect(window.Handle, out var client)) continue;
            if (client.Width < 300 || client.Height < 200) continue; // message boxes, splash strips
            long score = client.Width * (long)client.Height;
            if (FindCancel(window.Handle) != IntPtr.Zero) score *= 4; // the wizard has Cancel
            if (window.ClassName.Contains("Wizard", StringComparison.OrdinalIgnoreCase)) score *= 2;
            if (score > bestScore)
            {
                bestScore = score;
                best = window.Handle;
            }
        }
        if (best == IntPtr.Zero) return false;
        _wizard = best;
        SetupProcessNative.GetClientRect(best, out var size);
        _signature = $"{SetupProcessNative.ClassName(best)}|{size.Width}x{size.Height}";
        _wizardVisible.Start();
        return true;
    }

    private static IntPtr FindCancel(IntPtr wizard)
    {
        var found = IntPtr.Zero;
        SetupProcessNative.EnumChildWindows(wizard, (child, _) =>
        {
            if (!SetupProcessNative.IsWindowVisible(child) || !CancelRegex().IsMatch(SetupOptionTweaker.Text(child))) return true;
            found = child;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>The spots to try, most likely first.</summary>
    private static List<Target> FindTargets(IntPtr wizard, string? signature)
    {
        var targets = new List<Target>();
        if (!SetupProcessNative.GetClientRect(wizard, out var client)) return targets;

        // 0. Where the button was last time in a setup with this wizard: first try, usually the only one.
        if (signature is not null && MusicButtonMemory.Recall(signature) is { } spot)
            targets.Add(PointTarget(wizard, spot.X, client.Height - spot.FromBottom, "the spot that worked last time"));

        // The wizard's bottom strip: from the top of the Cancel button (or the bottom fifth) down.
        var cancel = FindCancel(wizard);
        var cancelRect = cancel != IntPtr.Zero ? ClientRectOf(wizard, cancel) : (SetupProcessNative.Rect?)null;
        var stripTop = cancelRect is { } cancelBox ? cancelBox.Top - cancelBox.Height : client.Height * 4 / 5;
        var leftZone = client.Width * 2 / 5;

        // 1. Small controls in the bottom-left corner (an image button, a "♪" button, a check box labelled Music).
        var controls = new List<(SetupProcessNative.Rect Rect, IntPtr Handle, string Text)>();
        SetupProcessNative.EnumChildWindows(wizard, (child, _) =>
        {
            if (!SetupProcessNative.IsWindowVisible(child)) return true;
            var r = ClientRectOf(wizard, child);
            var text = SetupOptionTweaker.Text(child);
            var small = r.Width is >= 10 and <= 96 && r.Height is >= 10 and <= 96;
            var labelled = text.Length > 0 && SoundLabelRegex().IsMatch(text);
            if (r.Left < leftZone && r.Top >= stripTop - 8 && (small && text.Length <= 2 || labelled)) controls.Add((r, child, text));
            return true;
        }, IntPtr.Zero);
        foreach (var (rect, handle, text) in controls.OrderByDescending(x => x.Text.Length > 0 && SoundLabelRegex().IsMatch(x.Text)).ThenBy(x => x.Rect.Left))
        {
            // Real buttons take BM_CLICK (not group boxes, style 7); anything else gets a mouse click.
            var isButton = (SetupProcessNative.GetWindowLong(handle, -16) & 0xF) != 7 &&
                           SetupProcessNative.ClassName(handle).Contains("Button", StringComparison.OrdinalIgnoreCase);
            targets.Add(new Target(handle, rect.Width / 2, rect.Height / 2, isButton,
                text.Length > 0 ? $"\"{text}\"" : $"control at {rect.Left},{rect.Top}",
                rect.Left + rect.Width / 2, rect.Top + rect.Height / 2));
        }

        // 2. Drawn icons (no window of their own): the bottom-left corner, level with Cancel, as far in as Cancel is from
        //    the right edge — where repack setups put their speaker.
        if (cancelRect is { } button)
        {
            var margin = Math.Clamp(client.Width - button.Right, 4, 40);
            var y = button.Top + button.Height / 2;
            targets.Add(PointTarget(wizard, margin + button.Height / 2, y));
            targets.Add(PointTarget(wizard, margin + button.Height, y));
        }
        targets.Add(PointTarget(wizard, 22, client.Height - 22));
        return targets;
    }

    /// <summary>A point in the wizard, sent to the innermost window there (the icon may sit on a panel).</summary>
    private static Target PointTarget(IntPtr wizard, int x, int y, string? description = null)
    {
        var window = wizard;
        var point = new SetupProcessNative.Point { X = x, Y = y };
        for (var depth = 0; depth < 6; depth++)
        {
            var child = SetupProcessNative.ChildWindowFromPointEx(window, point, CwpSkipInvisible | CwpSkipTransparent);
            if (child == IntPtr.Zero || child == window) break;
            var screen = point;
            SetupProcessNative.ClientToScreen(window, ref screen);
            SetupProcessNative.ScreenToClient(child, ref screen);
            window = child;
            point = screen;
        }
        return new Target(window, point.X, point.Y, false, description ?? $"bottom-left corner at {x},{y}", x, y);
    }

    private static SetupProcessNative.Rect ClientRectOf(IntPtr wizard, IntPtr child)
    {
        SetupProcessNative.GetWindowRect(child, out var r);
        var topLeft = new SetupProcessNative.Point { X = r.Left, Y = r.Top };
        var bottomRight = new SetupProcessNative.Point { X = r.Right, Y = r.Bottom };
        SetupProcessNative.ScreenToClient(wizard, ref topLeft);
        SetupProcessNative.ScreenToClient(wizard, ref bottomRight);
        return new SetupProcessNative.Rect { Left = topLeft.X, Top = topLeft.Y, Right = bottomRight.X, Bottom = bottomRight.Y };
    }

    private static void Click(Target target)
    {
        if (target.IsButton)
        {
            SetupOptionTweaker.Send(target.Window, BmClick, IntPtr.Zero, IntPtr.Zero);
            return;
        }
        // Hover first (image buttons light up on hover and some only click when "hot"), then press and release.
        var position = new IntPtr((target.Y << 16) | (target.X & 0xFFFF));
        SetupProcessNative.PostMessage(target.Window, WmMouseMove, IntPtr.Zero, position);
        SetupProcessNative.PostMessage(target.Window, WmLButtonDown, new IntPtr(1), position); // MK_LBUTTON
        SetupProcessNative.PostMessage(target.Window, WmLButtonUp, IntPtr.Zero, position);
    }
}

/// <summary>
/// Where a setup's music button turned out to be, per kind of wizard (window class and size), so the next setup built the
/// same way (the same repacker's script) is muted with the first click. %LOCALAPPDATA%\SeaRaider\music-buttons.json.
/// </summary>
internal static class MusicButtonMemory
{
    internal sealed record Spot(int X, int FromBottom);

    private static readonly object Gate = new();
    private static string FilePath => Path.Combine(AppPaths.DataDirectory, "music-buttons.json");

    /// <summary>Exact wizard (class and size) first, else the same class at any size (bottom-left anchored).</summary>
    public static Spot? Recall(string signature)
    {
        var all = Load();
        if (all.TryGetValue(signature, out var exact)) return exact;
        return all.TryGetValue(ClassOnly(signature), out var similar) ? similar : null;
    }

    public static void Remember(string signature, int x, int y, int clientHeight)
    {
        if (clientHeight <= 0) return;
        lock (Gate)
        {
            var all = Load();
            var spot = new Spot(x, clientHeight - y);
            all[signature] = spot;
            all[ClassOnly(signature)] = spot;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, System.Text.Json.JsonSerializer.Serialize(all));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // only a shortcut for next time
            }
        }
    }

    private static string ClassOnly(string signature) => signature.Split('|')[0] + "|*";

    private static Dictionary<string, Spot> Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, Spot>>(File.ReadAllText(FilePath)) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return [];
        }
    }
}
