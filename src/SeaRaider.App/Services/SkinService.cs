using Avalonia;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using SeaRaider.Core.Abstractions;
using SeaRaider.Core.Models;

namespace SeaRaider.App.Services;

/// <summary>
/// Switches skins while the app runs. "Hallow's Eve" (Styles/Halloween.axaml) is a seasonal skin laid over The Next
/// Generation. "Legacy" is Styles/Theme.axaml, always loaded. "The Next Generation" is
/// Styles/NextGeneration.axaml, added on top of it: its resources replace Legacy's palette (views use DynamicResource
/// for brushes, so they re-colour at once) and its styles add the glass, glow and display font. Removing it
/// restores Legacy exactly.
/// </summary>
public sealed class SkinService
{
    private static readonly Uri NextGenerationUri = new("avares://SeaRaider/Styles/NextGeneration.axaml");
    private static readonly Uri HalloweenUri = new("avares://SeaRaider/Styles/Halloween.axaml");

    private readonly ILibraryRepository _repository;
    private StyleInclude? _nextGeneration;
    private StyleInclude? _halloween;

    public SkinService(ILibraryRepository repository)
    {
        _repository = repository;
        repository.SettingsChanged += (_, settings) => Dispatcher.UIThread.Post(() => Apply(settings.Skin));
    }

    /// <summary>The skin on screen now.</summary>
    public AppSkin Current { get; private set; } = AppSkin.Legacy;

    public event EventHandler<AppSkin>? Changed;

    /// <summary>Display names, in <see cref="AppSkin"/> order.</summary>
    public static IReadOnlyList<string> Names { get; } = ["The Next Generation", "Legacy", "Hallowsea"];

    public static string NameOf(AppSkin skin) => Names[(int)skin];

    /// <summary>Applies the saved skin (at start-up).</summary>
    public void ApplySaved()
    {
        var settings = _repository.GetSettings();
        // Hallow's Eve: everyone starts in it once (new copies and updates alike); a skin picked after that is kept.
        if (!settings.HalloweenSkinGiven)
        {
            settings.HalloweenSkinGiven = true;
            settings.Skin = AppSkin.Halloween;
            _repository.SaveSettings(settings);
        }
        Apply(settings.Skin);
    }

    public void Apply(AppSkin skin)
    {
        if (Application.Current is not { } app) return;
        if (!Enum.IsDefined(skin)) skin = AppSkin.NextGeneration;
        var wantsNextGeneration = skin is AppSkin.NextGeneration or AppSkin.Halloween; // Hallow's Eve is built on it
        var wantsHalloween = skin == AppSkin.Halloween;
        var nextGenerationOn = _nextGeneration is not null && app.Styles.Contains(_nextGeneration);
        var halloweenOn = _halloween is not null && app.Styles.Contains(_halloween);
        if (skin == Current && wantsNextGeneration == nextGenerationOn && wantsHalloween == halloweenOn) return;

        // Order matters: Hallow's Eve sits on top of The Next Generation.
        if (_halloween is not null) app.Styles.Remove(_halloween);
        if (wantsNextGeneration)
        {
            _nextGeneration ??= new StyleInclude(new Uri("avares://SeaRaider/")) { Source = NextGenerationUri };
            if (!app.Styles.Contains(_nextGeneration)) app.Styles.Add(_nextGeneration);
        }
        else if (_nextGeneration is not null)
        {
            app.Styles.Remove(_nextGeneration);
        }
        if (wantsHalloween)
        {
            _halloween ??= new StyleInclude(new Uri("avares://SeaRaider/")) { Source = HalloweenUri };
            app.Styles.Add(_halloween);
        }

        Current = skin;
        Changed?.Invoke(this, skin);
    }
}
