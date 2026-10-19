using Avalonia.Controls;
using Avalonia.Threading;
using SeaRaider.App.ViewModels;

namespace SeaRaider.App.Views;

public partial class FriendsListOverlay : UserControl
{
    private FriendsViewModel? _subscribed;

    public FriendsListOverlay()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_subscribed is not null) _subscribed.MessagesAppended -= ScrollToNewest;
            _subscribed = DataContext as FriendsViewModel;
            if (_subscribed is not null) _subscribed.MessagesAppended += ScrollToNewest;
        };
    }

    /// <summary>Keeps the conversation pinned to the newest message.</summary>
    private void ScrollToNewest() =>
        Dispatcher.UIThread.Post(() => MessageScroller.ScrollToEnd(), DispatcherPriority.Background);
}
