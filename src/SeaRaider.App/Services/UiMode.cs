using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using SeaRaider.Core.Abstractions;
using SeaRaider.Core.Models;

namespace SeaRaider.App.Services;

/// <summary>
/// Simple or advanced (Settings › Show advanced options), for every page at once. Simple (the default) is the
/// console-dashboard experience: big tiles and one button per thing to do; every filter, sort, technical detail and
/// tuning option is in advanced, nothing is removed. Pages bind to it with
/// <c>{Binding IsAdvanced, Source={x:Static services:UiMode.Current}}</c>.
/// </summary>
public sealed partial class UiMode : ObservableObject
{
    public static UiMode Current { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSimple))]
    private bool _isAdvanced;

    public bool IsSimple => !IsAdvanced;

    /// <summary>Follows the setting from now on (called once at start-up).</summary>
    public void Attach(ILibraryRepository repository)
    {
        IsAdvanced = repository.GetSettings().ShowAdvancedOptions;
        repository.SettingsChanged += (_, settings) => Dispatcher.UIThread.Post(() => Apply(settings));
    }

    private void Apply(AppSettings settings) => IsAdvanced = settings.ShowAdvancedOptions;
}
