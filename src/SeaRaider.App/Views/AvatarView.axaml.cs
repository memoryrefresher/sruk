using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace SeaRaider.App.Views;

/// <summary>A profile picture with its decoration (see AvatarView.axaml); falls back to the name's first letter.</summary>
public partial class AvatarView : UserControl
{
    public static readonly StyledProperty<Bitmap?> PictureProperty = AvaloniaProperty.Register<AvatarView, Bitmap?>(nameof(Picture));
    public static readonly StyledProperty<string?> InitialProperty = AvaloniaProperty.Register<AvatarView, string?>(nameof(Initial));
    public static readonly StyledProperty<string?> DecorationProperty = AvaloniaProperty.Register<AvatarView, string?>(nameof(Decoration));

    public AvatarView()
    {
        InitializeComponent();
        Update();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    public Bitmap? Picture
    {
        get => GetValue(PictureProperty);
        set => SetValue(PictureProperty, value);
    }

    public string? Initial
    {
        get => GetValue(InitialProperty);
        set => SetValue(InitialProperty, value);
    }

    /// <summary>"ring", "waves", "neon", "gold", "crown", "halloween", or none.</summary>
    public string? Decoration
    {
        get => GetValue(DecorationProperty);
        set => SetValue(DecorationProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PictureProperty || change.Property == InitialProperty || change.Property == DecorationProperty) Update();
    }

    private void Update()
    {
        if (this.FindControl<TextBlock>("InitialText") is not { } initial || this.FindControl<Ellipse>("PictureCircle") is not { } circle) return;
        initial.Text = string.IsNullOrWhiteSpace(Initial) ? "?" : Initial;
        circle.IsVisible = Picture is not null;
        circle.Fill = Picture is null ? null : new ImageBrush(Picture) { Stretch = Stretch.UniformToFill };

        var decoration = Decoration ?? "none";
        foreach (var (name, id) in new[]
                 {
                     ("DecoRing", "ring"), ("DecoWaves", "waves"), ("DecoNeon", "neon"), ("DecoGold", "gold"),
                     ("DecoCrown", "crown"), ("DecoHalloween", "halloween"),
                 })
        {
            if (this.FindControl<Canvas>(name) is { } layer) layer.IsVisible = decoration == id;
        }
    }
}
