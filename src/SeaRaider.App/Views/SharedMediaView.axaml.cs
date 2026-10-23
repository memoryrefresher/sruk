using Avalonia.Controls;
using Avalonia.Input;
using SeaRaider.App.ViewModels;

namespace SeaRaider.App.Views;

/// <summary>Media › Shared. Enter shares a link or posts a comment (each box knows which).</summary>
public partial class SharedMediaView : UserControl
{
    public SharedMediaView() => InitializeComponent();

    private void OnShareKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not SharedMediaViewModel feed) return;
        e.Handled = true;
        if (feed.ShareCommand.CanExecute(null)) feed.ShareCommand.Execute(null);
    }

    private void OnCommentKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox { DataContext: SharedPostViewModel post }) return;
        e.Handled = true;
        if (post.PostCommentCommand.CanExecute(null)) post.PostCommentCommand.Execute(null);
    }
}
