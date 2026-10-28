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
        await Task.Delay(250);
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
        else if (e.PropertyName == nameof(TrailerPlayerService.SoundFrom)) foreach (var cell in _cells.Values) cell.UpdateSound();
        else if (e.PropertyName is nameof(TrailerPlayerService.IsBetSlipOpen) or nameof(TrailerPlayerService.BetSlipUrl)) ShowBetSlip();
        else if (e.PropertyName == nameof(TrailerPlayerService.IsFullScreen)) ApplyFullScreen();
    }

    private WindowState? _stateBeforeFullScreen;

    /// <summary>The video went full screen (its own button): so does the window, until the video comes back.</summary>
    private void ApplyFullScreen()
    {
        if (_trailer is { IsFullScreen: true })
        {
            if (WindowState == WindowState.FullScreen) return;
            _stateBeforeFullScreen = WindowState;
            WindowState = WindowState.FullScreen;
        }
        else if (_stateBeforeFullScreen is { } before)
        {
            _stateBeforeFullScreen = null;
            WindowState = before == WindowState.Minimized ? WindowState.Maximized : before;
        }
    }

    private BetSlipHost? _betSlip;

    /// <summary>
    /// The bet slip beside the player: made when it first opens and kept (the next video only changes its address), closed with
    /// the player.
    /// </summary>
    private void ShowBetSlip()
    {
        var url = _trailer is { IsBetSlipOpen: true } t ? t.BetSlipUrl : null;
        if (url is null)
        {
            _betSlip = null;
            BetSlipSlot.Child = null;
            return;
        }
        if (_betSlip is not null)
        {
            _betSlip.Navigate(url); // same page: nothing happens
            return;
        }
        BetSlipSlot.Child = _betSlip = new BetSlipHost(url);
    }

    private bool _chatScrollQueued;

    /// <summary>Keeps the live chat at its newest line: once per burst of lines (a room's history arrives all at once).</summary>
    private void ScrollChat()
    {
        if (_chatScrollQueued) return;
        _chatScrollQueued = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _chatScrollQueued = false;
            EventChatScroller.ScrollToEnd();
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    // ---- the player's streams: one, or up to four side by side (multi-view) ------------------------------------

    private readonly Dictionary<TrailerSession, StreamCell> _cells = new(ReferenceEqualityComparer.Instance);
    private Avalonia.Controls.Primitives.UniformGrid? _streamGrid;

    /// <summary>
    /// A fresh player for each trailer; removing it closes its browser (and stops the sound) at once. Streams watched alongside
    /// are added to (and taken from) the same grid without touching the others, so nothing that's playing reloads.
    /// </summary>
    private void ShowTrailer()
    {
        if (_trailer is not null) _trailer.Alongside.CollectionChanged -= OnAlongsideChanged;
        _cells.Clear();
        _streamGrid = null;
        var session = _trailer?.Current;
        if (_trailer is null || session is null)
        {
            TrailerSlot.Child = null;
            return;
        }
        _streamGrid = new Avalonia.Controls.Primitives.UniformGrid { Columns = 1 };
        TrailerSlot.Child = _streamGrid;
        AddCell(session);
        foreach (var extra in _trailer.Alongside) AddCell(extra);
        _trailer.Alongside.CollectionChanged += OnAlongsideChanged;
        LayoutCells();
    }

    private void OnAlongsideChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_trailer?.Current is not { } main || _streamGrid is null) return;
        var wanted = _trailer.Alongside.Prepend(main).ToList();
        foreach (var gone in _cells.Keys.Where(k => !wanted.Contains(k)).ToList())
        {
            _streamGrid.Children.Remove(_cells[gone]); // closes its browser
            _cells.Remove(gone);
        }
        foreach (var added in wanted.Where(w => !_cells.ContainsKey(w))) AddCell(added);
        LayoutCells();
    }

    private void AddCell(TrailerSession session)
    {
        var cell = new StreamCell(_trailer!, session);
        _cells[session] = cell;
        _streamGrid!.Children.Add(cell);
    }

    /// <summary>One stream fills the box; two side by side; three or four in a 2 × 2 grid. The bars over each show only in multi-view.</summary>
    private void LayoutCells()
    {
        if (_streamGrid is null) return;
        var count = _cells.Count;
        _streamGrid.Columns = count <= 1 ? 1 : 2;
        _streamGrid.Rows = count <= 2 ? 1 : 2;
        foreach (var cell in _cells.Values) cell.ShowBar(count > 1);
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
            if (_trailer is { IsFullScreen: true })
            {
                _trailer.SetFullScreen(false); // the video's full screen ends, and the window goes back with it
                e.Handled = true;
                return;
            }
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
        if (_trailer is { IsMinimized: false, IsFullScreen: false }) _trailer.MinimizeCommand.Execute(null);
    }
}

/// <summary>
/// One stream in the player: its browser, and (in multi-view) a slim bar above it with its title, 🔊 to listen to it and ×
/// to take it out. The bar sits outside the video, since nothing can be drawn over a browser window.
/// </summary>
internal sealed class StreamCell : DockPanel
{
    private readonly TrailerPlayerService _player;
    private readonly TrailerSession _session;
    private readonly Grid _bar;
    private readonly Button _sound;

    public StreamCell(TrailerPlayerService player, TrailerSession session)
    {
        _player = player;
        _session = session;
        Margin = new Avalonia.Thickness(2);

        var title = new TextBlock
        {
            Text = session.Title,
            FontSize = 12,
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
            Margin = new Avalonia.Thickness(10, 0),
        };
        _sound = new Button { Padding = new Avalonia.Thickness(8, 0), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        _sound.Classes.Add("link");
        _sound.Click += (_, _) => _player.ListenTo(_session);
        var close = new Button { Content = "✕", Padding = new Avalonia.Thickness(8, 0), Margin = new Avalonia.Thickness(0, 0, 4, 0),
                                 VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        close.Classes.Add("link");
        ToolTip.SetTip(close, "Take this stream out");
        close.Click += (_, _) => _player.RemoveAlongside(_session);

        _bar = new Grid { Height = 28, ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), IsVisible = false };
        _bar.Bind(Panel.BackgroundProperty, _bar.GetResourceObservable("PlayerBarBrush"));
        _bar.Children.Add(title);
        Grid.SetColumn(_sound, 1);
        Grid.SetColumn(close, 2);
        _bar.Children.Add(_sound);
        _bar.Children.Add(close);
        SetDock(_bar, Dock.Top);
        Children.Add(_bar);
        Children.Add(new Border { Background = Avalonia.Media.Brushes.Black, Child = new TrailerPlayerHost(player, session) });
        UpdateSound();
    }

    public void ShowBar(bool show) => _bar.IsVisible = show;

    public void UpdateSound()
    {
        var audible = _player.IsAudible(_session);
        _sound.Content = audible ? "🔊" : "🔇";
        ToolTip.SetTip(_sound, audible ? "You're hearing this one" : "Listen to this one");
    }
}
