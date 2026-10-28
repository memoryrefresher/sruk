using Avalonia.Controls;
using Avalonia.Input;
using SeaRaider.App.ViewModels;

namespace SeaRaider.App.Views;

public partial class IptvView : UserControl
{
    public IptvView()
    {
        InitializeComponent();
        Focusable = true;
        // Like a remote: digits dial a channel number, Enter tunes in, Page Up / Down change channel, Esc clears.
        KeyDown += OnRemoteKey;
        PointerPressed += (_, e) =>
        {
            if (e.Source is not TextBox) Focus();
        };
    }

    private void OnRemoteKey(object? sender, KeyEventArgs e)
    {
        if (DataContext is not IptvViewModel vm || e.Source is TextBox) return;
        var digit = e.Key is >= Key.D0 and <= Key.D9 ? e.Key - Key.D0 : e.Key is >= Key.NumPad0 and <= Key.NumPad9 ? e.Key - Key.NumPad0 : -1;
        if (digit >= 0 && e.KeyModifiers == KeyModifiers.None) { vm.OnDigitTyped((char)('0' + digit)); e.Handled = true; return; }
        switch (e.Key)
        {
            case Key.Enter when vm.HasKeypadText: vm.GoToTypedNumberCommand.Execute(null); e.Handled = true; break;
            case Key.Escape when vm.HasKeypadText: vm.ClearKeypadCommand.Execute(null); e.Handled = true; break;
            case Key.PageUp: vm.ChannelUpCommand.Execute(null); e.Handled = true; break;
            case Key.PageDown: vm.ChannelDownCommand.Execute(null); e.Handled = true; break;
            case Key.Back when vm.HasKeypadText: vm.KeypadText = vm.KeypadText[..^1]; e.Handled = true; break;
        }
    }

    private void OnLinkKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is IptvViewModel vm) vm.ConnectCommand.Execute(null);
    }

    private void OnGoToKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is IptvViewModel vm) vm.GoToTypedNumberCommand.Execute(null);
    }
}
