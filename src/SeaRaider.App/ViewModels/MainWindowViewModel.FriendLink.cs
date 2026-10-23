using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using SeaRaider.Core.Social;

namespace SeaRaider.App.ViewModels;

/// <summary>
/// "Add me on SeaRaider" from someone's Discord status: the button opens searaider://add/&lt;friend code&gt;?name=…, which
/// starts (or brings forward) SeaRaider, and this card asks whether to send them a friend request.
/// </summary>
public sealed partial class MainWindowViewModel
{
    [ObservableProperty] private bool _isFriendLinkOpen;
    [ObservableProperty] private string _friendLinkTitle = string.Empty;
    [ObservableProperty] private string _friendLinkCode = string.Empty;
    [ObservableProperty] private string? _friendLinkMessage;
    [ObservableProperty] private bool _canConfirmFriendLink;
    [ObservableProperty] private bool _isFriendLinkDone;

    /// <summary>Asks "Add &lt;name&gt; on SeaRaider?" for a friend code from a link.</summary>
    public void AskAddFriend(string code, string? name)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var friends = _services.GetRequiredService<IFriendsService>();
            var userId = FriendCode.Normalize(code);
            var shown = string.IsNullOrWhiteSpace(name) ? null : name.Trim()[..Math.Min(40, name.Trim().Length)];
            FriendLinkCode = userId ?? code;
            FriendLinkTitle = shown is null ? "Add this person on SeaRaider?" : $"Add {shown} on SeaRaider?";
            FriendLinkMessage = null;
            CanConfirmFriendLink = false;
            IsFriendLinkDone = false;

            var existing = userId is null ? null : friends.Friends.FirstOrDefault(f => f.UserId == userId);
            if (userId is null) FriendLinkMessage = "That link doesn't have a valid friend code.";
            else if (userId == friends.Profile.UserId) FriendLinkMessage = "That's your own friend code: your Discord friends see this button.";
            else if (existing?.State == FriendshipState.Friend) FriendLinkMessage = $"You and {existing.DisplayName} are already friends.";
            else if (existing?.State == FriendshipState.OutgoingRequest) FriendLinkMessage = "You've already sent them a friend request.";
            else if (!friends.IsEnabled) FriendLinkMessage = "Friends is switched off. Turn it on in Settings › Friends, then click their button again.";
            else
            {
                CanConfirmFriendLink = true;
                if (friends.ConnectionState != SocialConnectionState.Connected) FriendLinkMessage = "Still connecting to the friends relay: if Add says it isn't connected, wait a few seconds and try again.";
            }

            if (IsPlayerCovering) Trailer.MinimizeCommand.Execute(null); // so the card isn't hidden behind the video
            IsFriendLinkOpen = true;
        });
    }

    [RelayCommand]
    private async Task ConfirmFriendLinkAsync()
    {
        if (!CanConfirmFriendLink) return;
        CanConfirmFriendLink = false;
        try
        {
            await _services.GetRequiredService<IFriendsService>().SendFriendRequestAsync(FriendLinkCode, "Added you from Discord");
            FriendLinkMessage = "Friend request sent. They'll show up in your friends list when they accept.";
            IsFriendLinkDone = true;
        }
        catch (Exception ex)
        {
            FriendLinkMessage = ex.Message;
            CanConfirmFriendLink = true;
        }
    }

    [RelayCommand]
    private void CloseFriendLink() => IsFriendLinkOpen = false;
}
