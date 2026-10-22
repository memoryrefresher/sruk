using Avalonia;
using Avalonia.Controls;
using SeaRaider.App.ViewModels;

namespace SeaRaider.App.Views;

public partial class StorefrontView : UserControl
{
    /// <summary>Start loading the next page this far (in pixels) before the end of the list.</summary>
    private const double LoadAheadDistance = 900;

    /// <summary>Where to scroll once the list is tall enough; null when there's nothing to restore.</summary>
    private double? _restoreOffset;
    private int _restoreAttempts;
    private const int MaxRestoreAttempts = 60;

    public StorefrontView()
    {
        InitializeComponent();
        Scroller.ScrollChanged += OnScrollChanged;
        DataContextChanged += (_, _) => BeginRestore();
        AttachedToVisualTree += (_, _) => BeginRestore();
    }

    /// <summary>Coming back from a game page: remember to scroll to where the store was left.</summary>
    private void BeginRestore()
    {
        if (DataContext is StorefrontViewModel { ScrollOffset: > 0 } vm) _restoreOffset = vm.ScrollOffset;
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (DataContext is not StorefrontViewModel vm) return;

        if (_restoreOffset is { } target)
        {
            // The grid is laid out in steps: keep trying until it's tall enough to reach the spot (or give up after a while,
            // e.g. when the results changed and the list is shorter now).
            var reachable = Scroller.Extent.Height - Scroller.Viewport.Height;
            if (reachable >= target - 1 || ++_restoreAttempts > MaxRestoreAttempts)
            {
                _restoreOffset = null;
                _restoreAttempts = 0;
                Scroller.Offset = new Vector(Scroller.Offset.X, Math.Max(0, Math.Min(target, reachable)));
            }
            return; // don't remember the temporary top-of-list position while restoring
        }

        // Remember where the person scrolled to. Only real scrolling counts: when the page is torn down the list collapses
        // and the offset snaps to 0, which must not overwrite the saved spot.
        if (e.OffsetDelta.Y != 0 && e.ExtentDelta.Y == 0 && e.ViewportDelta.Y == 0 && Scroller.Extent.Height > 0)
            vm.ScrollOffset = Scroller.Offset.Y;

        // Infinite scroll: near the bottom, ask the view-model for the next page.
        if (!vm.HasMore || vm.LoadMoreCommand.IsRunning) return;
        var remaining = Scroller.Extent.Height - (Scroller.Offset.Y + Scroller.Viewport.Height);
        if (remaining < LoadAheadDistance && vm.LoadMoreCommand.CanExecute(null))
            vm.LoadMoreCommand.Execute(null);
    }
}
