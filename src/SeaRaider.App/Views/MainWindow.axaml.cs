using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using SeaRaider.App.Services;
using SeaRaider.App.ViewModels;

namespace SeaRaider.App.Views;

public partial class MainWindow : Window
{
    private TrailerPlayerService? _trailer;
    private MainWindowViewModel? _shell;

    public MainWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
        // Belt and braces: Windows keeps the opt-out for the process, but asking again costs nothing.
        Deactivated += (_, _) => BackgroundThrottling.KeepFullSpeed();
    }

    /// <summary>
    /// The window opens maximised on its dark background; a moment later its content fades in. The short wait lets the first
    /// frame (and the store's first layout) finish, so the fade doesn't stutter. With Animations off it simply appears.
    /// </summary>
    private async void OnOpened(object? sender, EventArgs e)
    {
        Opened -= OnOpened;
        if (_shell is { AnimationsEnabled: false })
        {
            RootFade.Transitions = null;
            RootFade.Opacity = 1;
            return;
        }
        await Task.Delay(150);
        RootFade.Opacity = 1;
    }

    /// <summary>
    /// The client area is extended into the title bar, so the brand block and the empty strip
    /// above the page act as the drag handle (double-click toggles maximise, like a native title bar).
    /// </summary>
    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.Source is Button || (e.Source as Control)?.FindAncestorOfType<Button>() is not null) return;

        if (e.ClickCount == 2)
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else
            BeginMoveDrag(e);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_trailer is not null) _trailer.PropertyChanged -= OnTrailerChanged;
        if (_shell is not null) _shell.PropertyChanged -= OnShellChanged;
        if (_shell is not null) _shell.EventChat.LinesAppended -= ScrollChat;
        _shell = DataContext as MainWindowViewModel;
        if (_shell is not null) _shell.EventChat.LinesAppended += ScrollChat;
        _trailer = _shell?.Trailer;
        if (_trailer is not null) _trailer.PropertyChanged += OnTrailerChanged;
        if (_shell is not null) _shell.PropertyChanged += OnShellChanged;
        ShowTrailer();
        ShowBetSlip();
    }

    private void OnTrailerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TrailerPlayerService.Current)) ShowTrailer();
        else if (e.PropertyName is nameof(TrailerPlayerService.IsBetSlipOpen) or nameof(TrailerPlayerService.BetSlipUrl)) ShowBetSlip();
    }

    private string? _betSlipShown;

    /// <summary>The bet slip beside the player: made when it opens (or its address changes), closed with it.</summary>
    private void ShowBetSlip()
    {
        var url = _trailer is { IsBetSlipOpen: true } t ? t.BetSlipUrl : null;
        if (url == _betSlipShown) return; // same page: keep it (and whatever the user did on it)
        _betSlipShown = url;
        BetSlipSlot.Child = url is null ? null : new BetSlipHost(url);
    }

    /// <summary>Keeps the live chat at its newest line.</summary>
    private void ScrollChat() =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() => EventChatScroller.ScrollToEnd(), Avalonia.Threading.DispatcherPriority.Background);

    /// <summary>A fresh player for each trailer; removing it closes its browser (and stops the sound) at once.</summary>
    private void ShowTrailer()
    {
        var session = _trailer?.Current;
        TrailerSlot.Child = _trailer is not null && session is not null ? new TrailerPlayerHost(_trailer, session) : null;
    }

    /// <summary>
    /// F11 switches true full screen (no title bar, no taskbar) on and off. Esc closes whatever is popped out: the trailer
    /// first, then a coming-soon page, and otherwise leaves full screen.
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;
        if (e.Key == Key.F11)
        {
            WindowState = WindowState == WindowState.FullScreen ? WindowState.Maximized : WindowState.FullScreen;
            e.Handled = true;
            return;
        }
        if (e.Key != Key.Escape || _shell is null) return;
        if (_shell.Trailer.IsOpen && !_shell.Trailer.IsMinimized) _shell.Trailer.MinimizeCommand.Execute(null); // keeps playing in a corner
        else if (_shell.Upcoming.IsPopoutOpen) _shell.Upcoming.ClosePopoutCommand.Execute(null);
        else if (WindowState == WindowState.FullScreen) WindowState = WindowState.Maximized;
        else return;
        e.Handled = true;
    }

    /// <summary>A click on the dimmed area around a coming-soon page closes it.</summary>
    private void OnUpcomingBackdropPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, UpcomingOverlay)) return;
        if (e.GetPosition(this).Y < 44)
        {
            BeginMoveDrag(e);
            return;
        }
        _shell?.Upcoming.ClosePopoutCommand.Execute(null);
    }

    /// <summary>A wishlisted game arrived while SeaRaider is in the background: flash it in the taskbar.</summary>
    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainWindowViewModel.HasWishlistNotice) || _shell?.HasWishlistNotice != true || IsActive) return;
        if (!OperatingSystem.IsWindows() || TryGetPlatformHandle()?.Handle is not { } hwnd || hwnd == IntPtr.Zero) return;
        var info = new FlashWindowInfo
        {
            cbSize = (uint)Marshal.SizeOf<FlashWindowInfo>(),
            hwnd = hwnd,
            dwFlags = 0x3 | 0xC, // FLASHW_ALL | FLASHW_TIMERNOFG: until the window comes to the front
        };
        FlashWindowEx(ref info);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashWindowInfo
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FlashWindowInfo info);

    /// <summary>A click on the dimmed area around the video shrinks the player into a corner (it keeps playing).</summary>
    private void OnTrailerBackdropPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, TrailerOverlay)) return;
        if (e.GetPosition(this).Y < 44)
        {
            BeginMoveDrag(e); // the strip under the caption buttons still moves the window
            return;
        }
        if (_trailer is { IsMinimized: false }) _trailer.MinimizeCommand.Execute(null);
    }
}
