using Avalonia;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using SeaRaider.Core.Abstractions;
using SeaRaider.Core.Models;

namespace SeaRaider.App.Services;

/// <summary>
/// Settings → General → Appearance &amp; performance, applied live:
/// <list type="bullet">
///   <item><b>Animations</b>: Styles/Motion.axaml (the springy presses, lifts, page and pop-out entrances) is loaded or
///         removed. Without it, the theme's plain short fades remain.</item>
///   <item><b>Blur and glow</b>: off adds Styles/Lite.axaml (no shadows or glows) last, and <see cref="BlurEnabled"/>
///         tells the window not to blur itself behind pop-outs.</item>
/// </list>
/// </summary>
public sealed class EffectsService
{
    private readonly ILibraryRepository _repository;
    private readonly StyleInclude _motion = Include("Motion.axaml");
    private readonly StyleInclude _lite = Include("Lite.axaml");
    private readonly StyleInclude _halloweenMotion = Include("HalloweenMotion.axaml");
    private readonly SkinService _skins;

    public EffectsService(ILibraryRepository repository, SkinService skins)
    {
        _repository = repository;
        _skins = skins;
        repository.SettingsChanged += (_, settings) => Dispatcher.UIThread.Post(() => Apply(settings));
        // A skin switch adds its styles at the end: put the flat look back after it.
        skins.Changed += (_, _) => Dispatcher.UIThread.Post(() => Apply(_repository.GetSettings()));
    }

    public bool AnimationsEnabled { get; private set; } = true;
    public bool BlurEnabled { get; private set; } = true;

    /// <summary>Raised (UI thread) when either switch changes.</summary>
    public event EventHandler? Changed;

    public void ApplySaved() => Apply(_repository.GetSettings());

    private void Apply(AppSettings settings)
    {
        if (Application.Current is not { } app) return;
        var changed = AnimationsEnabled != settings.Animations || BlurEnabled != settings.BlurAndGlow;

        // Only touch the style list when something actually changes: every change restyles the whole window.
        if (settings.Animations != app.Styles.Contains(_motion))
        {
            if (settings.Animations) app.Styles.Add(_motion);
            else app.Styles.Remove(_motion);
        }

        // Hallow's Eve moves (moon, bats, mist, embers) only with its skin and with Animations on.
        var wantsHalloweenMotion = settings.Animations && _skins.Current == AppSkin.Halloween;
        if (wantsHalloweenMotion != app.Styles.Contains(_halloweenMotion))
        {
            if (wantsHalloweenMotion) app.Styles.Add(_halloweenMotion);
            else app.Styles.Remove(_halloweenMotion);
        }

        var isLast = app.Styles.Count > 0 && ReferenceEquals(app.Styles[app.Styles.Count - 1], _lite);
        if (!settings.BlurAndGlow && !isLast)
        {
            app.Styles.Remove(_lite);
            app.Styles.Add(_lite); // last, so it wins over the skins
        }
        else if (settings.BlurAndGlow)
        {
            app.Styles.Remove(_lite);
        }

        AnimationsEnabled = settings.Animations;
        BlurEnabled = settings.BlurAndGlow;
        if (changed) Changed?.Invoke(this, EventArgs.Empty);
    }

    private static StyleInclude Include(string file) =>
        new(new Uri("avares://SeaRaider/")) { Source = new Uri($"avares://SeaRaider/Styles/{file}") };
}
