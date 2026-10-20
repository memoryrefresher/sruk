namespace SeaRaider.App.Services;

/// <summary>Top-level destinations in the left rail.</summary>
public enum AppPage
{
    Store,
    Library,
    Wishlist,
    Media,
    Downloads,
    Settings,
}

/// <summary>Tabs on the Settings page, in the order they appear (the values are the tab indexes).</summary>
public enum SettingsTab
{
    General,
    Sources,
    Downloads,
    Setup,
    Torrents,
    Updates,
    Friends,
}

/// <summary>
/// Decouples page view-models from the shell: pages ask to navigate, the
/// <c>MainWindowViewModel</c> listens and swaps the visible page.
/// </summary>
public sealed class NavigationService
{
    public event Action<AppPage>? PageRequested;
    public event Action<string>? GameRequested;
    public event Action? BackRequested;

    /// <summary>Show the store with this search typed in (the shell switches page, the store searches).</summary>
    public event Action<string>? SearchRequested;

    public void SearchStore(string text) => SearchRequested?.Invoke(text);

    public void ShowPage(AppPage page) => PageRequested?.Invoke(page);

    /// <summary>Opens Settings on one of its tabs.</summary>
    public void ShowSettings(SettingsTab tab)
    {
        RequestedSettingsTab = tab;
        ShowPage(AppPage.Settings);
    }

    /// <summary>Tab the Settings page should open on next time it's shown (read once).</summary>
    public SettingsTab? RequestedSettingsTab { get; set; }

    /// <summary>Opens the store page of one game.</summary>
    public void ShowGame(string gameId) => GameRequested?.Invoke(gameId);

    public void GoBack() => BackRequested?.Invoke();
}
