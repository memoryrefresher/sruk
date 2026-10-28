using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using SeaRaider.App.Services;
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

        // Game and media boxes dropped on the panel: on a friend, their chat opens with it; anywhere else, it waits above the
        // message box of the open chat (or for a friend to be picked).
        AddHandler(DragDrop.DragOverEvent, OnDragOver, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DropEvent, OnDrop, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>Keeps the conversation pinned to the newest message.</summary>
    private void ScrollToNewest() =>
        Dispatcher.UIThread.Post(() => MessageScroller.ScrollToEnd(), DispatcherPriority.Background);

    private static void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(ChatAttachments.DragFormat) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not FriendsViewModel friends || !e.Data.Contains(ChatAttachments.DragFormat)) return;
        if (ChatAttachment.FromJson(e.Data.Get(ChatAttachments.DragFormat) as string) is not { } attachment) return;
        e.Handled = true;
        friends.Attach(attachment, FriendUnder(e.Source));
    }

    /// <summary>The friend row the drop landed on, if any.</summary>
    private static FriendItemViewModel? FriendUnder(object? source)
    {
        for (var element = source as StyledElement; element is not null; element = element.Parent)
        {
            if (element.DataContext is FriendItemViewModel friend) return friend;
            if (element is FriendsListOverlay) break;
        }
        return null;
    }
}
