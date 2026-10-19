using Avalonia;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using SeaRaider.Core.Abstractions;
using SeaRaider.Core.Models;

namespace SeaRaider.App.Services;

/// <summary>
/// Switches skins while the app runs. "Legacy" is Styles/Theme.axaml, always loaded. "The Next Generation" is
/// Styles/NextGeneration.axaml, added on top of it: its resources replace Legacy's palette (views use DynamicResource
/// for brushes, so they re-colour at once) and its styles add the glass, glow and display font. Removing it
/// restores Legacy exactly.
/// </summary>
public sealed class SkinService
{
    private static readonly Uri NextGenerationUri = new("avares://SeaRaider/Styles/NextGeneration.axaml");

    private readonly ILibraryRepository _repository;
    private StyleInclude? _nextGeneration;

    public SkinService(ILibraryRepository repository)
    {
        _repository = repository;
        repository.SettingsChanged += (_, settings) => Dispatcher.UIThread.Post(() => Apply(settings.Skin));
    }

    /// <summary>The skin on screen now.</summary>
    public AppSkin Current { get; private set; } = AppSkin.Legacy;

    public event EventHandler<AppSkin>? Changed;

    /// <summary>Display names, in <see cref="AppSkin"/> order.</summary>
    public static IReadOnlyList<string> Names { get; } = ["The Next Generation", "Legacy"];

    public static string NameOf(AppSkin skin) => Names[(int)skin];

    /// <summary>Applies the saved skin (at start-up).</summary>
    public void ApplySaved() => Apply(_repository.GetSettings().Skin);

    public void Apply(AppSkin skin)
    {
        if (Application.Current is not { } app) return;
        if (skin == Current && (skin == AppSkin.Legacy || _nextGeneration is not null)) return;

        if (skin == AppSkin.NextGeneration)
        {
            _nextGeneration ??= new StyleInclude(new Uri("avares://SeaRaider/")) { Source = NextGenerationUri };
            if (!app.Styles.Contains(_nextGeneration)) app.Styles.Add(_nextGeneration);
        }
        else if (_nextGeneration is not null)
        {
            app.Styles.Remove(_nextGeneration);
        }

        Current = skin;
        Changed?.Invoke(this, skin);
    }
}
