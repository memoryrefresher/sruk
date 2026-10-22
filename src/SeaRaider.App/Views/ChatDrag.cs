using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using SeaRaider.App.Services;

namespace SeaRaider.App.Views;

/// <summary>
/// Makes a game or media box draggable into the friends chat: <c>views:ChatDrag.Source="{Binding}"</c> on the box (its view
/// model is an <see cref="IChatAttachmentSource"/>). Pressing and moving a few pixels starts the drag (a plain click still
/// clicks); the friends panel opens so the box can be dropped on a friend or into the open chat.
/// </summary>
public static class ChatDrag
{
    private const double Threshold = 8; // pixels moved before a press becomes a drag

    public static readonly AttachedProperty<IChatAttachmentSource?> SourceProperty =
        AvaloniaProperty.RegisterAttached<Control, IChatAttachmentSource?>("Source", typeof(ChatDrag));

    /// <summary>Where the press started, while the button is down (null otherwise).</summary>
    private static readonly AttachedProperty<Point?> PressedAtProperty =
        AvaloniaProperty.RegisterAttached<Control, Point?>("PressedAt", typeof(ChatDrag));

    private static readonly AttachedProperty<bool> HookedProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("Hooked", typeof(ChatDrag));

    static ChatDrag() => SourceProperty.Changed.AddClassHandler<Control>((control, _) => Hook(control));

    public static IChatAttachmentSource? GetSource(Control control) => control.GetValue(SourceProperty);
    public static void SetSource(Control control, IChatAttachmentSource? value) => control.SetValue(SourceProperty, value);

    private static void Hook(Control control)
    {
        if (control.GetValue(HookedProperty)) return;
        control.SetValue(HookedProperty, true);
        // Tunnel + handled too: buttons take the press for themselves.
        control.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        control.AddHandler(InputElement.PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        control.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private static void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control control && e.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
            control.SetValue(PressedAtProperty, e.GetPosition(control));
    }

    private static void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is Control control) control.SetValue(PressedAtProperty, null);
    }

    private static async void OnMoved(object? sender, PointerEventArgs e)
    {
        if (sender is not Control control || control.GetValue(PressedAtProperty) is not { } start) return;
        if (!e.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
        {
            control.SetValue(PressedAtProperty, null);
            return;
        }
        var now = e.GetPosition(control);
        if (Math.Abs(now.X - start.X) < Threshold && Math.Abs(now.Y - start.Y) < Threshold) return;

        control.SetValue(PressedAtProperty, null);
        if (GetSource(control)?.ToChatAttachment() is not { } attachment) return;

        var data = new DataObject();
        data.Set(ChatAttachments.DragFormat, attachment.ToJson());
        data.Set(DataFormats.Text, attachment.Title);
        e.Pointer.Capture(null); // the box's button lets go, so the drop doesn't also click it
        ChatAttachments.OnDragStarted();
        try
        {
            await DragDrop.DoDragDrop(e, data, DragDropEffects.Copy);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // another drag was already running
        }
    }
}
