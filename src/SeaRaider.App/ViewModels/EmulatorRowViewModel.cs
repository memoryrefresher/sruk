using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SeaRaider.Core.Emulation;

namespace SeaRaider.App.ViewModels;

/// <summary>One line of Settings › Emulators: a system, the emulator that plays it, and optionally its own command line.</summary>
public sealed partial class EmulatorRowViewModel : ObservableObject
{
    private readonly Func<string, Task<string?>> _pick;

    public EmulatorRowViewModel(GameSystem system, string exePath, string arguments, Func<string, Task<string?>> pick)
    {
        SystemId = system.Id;
        SystemName = system.Name;
        _exePath = exePath;
        _arguments = arguments;
        _pick = pick;
    }

    public string SystemId { get; }

    public string SystemName { get; }

    /// <summary>The emulator program (an .exe); empty = no emulator chosen, the system's games can't be played yet.</summary>
    [ObservableProperty] private string _exePath;

    /// <summary>Command line to start a game with ({rom} is the game file); empty = the known switches for that emulator.</summary>
    [ObservableProperty] private string _arguments;

    [RelayCommand]
    private async Task BrowseAsync()
    {
        if (await _pick(SystemName) is { } path) ExePath = path;
    }
}
