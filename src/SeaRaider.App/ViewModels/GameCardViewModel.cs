using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SeaRaider.App.Services;
using SeaRaider.Core.Catalog;
using SeaRaider.Core.Models;

namespace SeaRaider.App.ViewModels;

/// <summary>
/// One tile in the storefront grid: one game, merged across every source that has it (<see cref="GameGroup"/>).
/// Shows the best copy's art and summary, which sites carry it, and acts on the copy that's already on disk or
/// downloading (else the best copy).
/// </summary>
public sealed partial class GameCardViewModel : ViewModelBase, IChatAttachmentSource
{
    /// <summary>What goes in a chat message (dragged into the chat, or "Send to a friend…"): the friend's store opens it.</summary>
    public ChatAttachment? ToChatAttachment() =>
        string.IsNullOrWhiteSpace(Entry.PageUrl)
            ? null
            : ChatAttachment.ForGame(new Core.Social.SharedGame(Entry.Title, Entry.PageUrl, CoverImageUrl, SourceName(Entry.SourceId), Entry.Size));

    [RelayCommand]
    private void SendToFriend() => ChatAttachments.RequestAttach(this);

    /// <summary>When copies disagree, the card reflects the most "advanced" one: playing beats installed beats downloading …</summary>
    private static readonly GameUiState[] StatePriority =
    [
        GameUiState.Running, GameUiState.Installed, GameUiState.Installing, GameUiState.ReadyToInstall, GameUiState.Extracting,
        GameUiState.Downloading, GameUiState.Queued, GameUiState.Paused, GameUiState.Failed, GameUiState.Available,
    ];

    private readonly GameStatusService _status;
    private readonly GameActionService _actions;
    private readonly NavigationService _navigation;
    private readonly Action<GameCardViewModel>? _hide;
    private readonly ArtworkService? _artwork;
    private string? _fallbackCover;
    private string? _heroUrl;
    private bool _artworkRequested;
    private IReadOnlyDictionary<Guid, string> _sourceNames;

    /// <param name="hide">Removes the card from the store (its × button); null where cards can't be hidden.</param>
    public GameCardViewModel(GameGroup group, IReadOnlyDictionary<Guid, string> sourceNames, GameStatusService status,
        GameActionService actions, NavigationService navigation, Action<GameCardViewModel>? hide = null,
        ArtworkService? artwork = null)
    {
        _hide = hide;
        _artwork = artwork;
#pragma warning disable MVVMTK0034 // initialise the backing fields directly (no change notification needed yet)
        _group = group;
        _entry = group.Primary;
#pragma warning restore MVVMTK0034
        _sourceNames = sourceNames;
        _status = status;
        _actions = actions;
        _navigation = navigation;
        ActiveId = group.Primary.Id;
        RefreshStatus();
        if (string.IsNullOrEmpty(group.Primary.CoverImageUrl)) LoadArtwork();
    }

    /// <summary>Every copy of this game; replaced when results or the catalog change.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SourcesText), nameof(HasMultipleSources), nameof(SourceCountText), nameof(DateText))]
    private GameGroup _group;

    /// <summary>The copy whose art and summary the card shows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(TitleUpper), nameof(ShortDescription), nameof(CoverImageUrl), nameof(HasCover), nameof(HeroImageUrl), nameof(Monogram),
        nameof(PrimaryTag), nameof(HasPrimaryTag), nameof(SizeText), nameof(HasSize), nameof(VersionText), nameof(HasVersion),
        nameof(DlcText), nameof(HasDlc), nameof(DateText))]
    private CatalogEntry _entry;

    /// <summary>Copy the main button acts on: the one on disk / downloading, else the card's face.</summary>
    public string ActiveId { get; private set; }

    public string Key => Group.Key;
    public IEnumerable<string> VariantIds => Group.Variants.Select(v => v.Id);

    /// <summary>Just the game's name: version, DLCs and repack notes are tags of their own.</summary>
    public string Title => DisplayText.Title(Entry.Title);

    /// <summary>The title in capitals, for "The Next Generation" skin's display type.</summary>
    public string TitleUpper => Title.ToUpperInvariant();

    public bool CanHide => _hide is not null;
    public string ShortDescription => DisplayText.Description(Entry.ShortDescription);
    /// <summary>The page's cover, else a picture found elsewhere (Steam header, or a screenshot from the game page).</summary>
    public string? CoverImageUrl => string.IsNullOrEmpty(Entry.CoverImageUrl) ? _fallbackCover : Entry.CoverImageUrl;
    public bool HasCover => !string.IsNullOrEmpty(CoverImageUrl);

    /// <summary>Large art for the featured banner and the backdrop (see <see cref="LoadHero"/>); the cover until it's found.</summary>
    public string? HeroImageUrl => _heroUrl ?? CoverImageUrl;

    /// <summary>Looks up high-resolution art (featured card): a repack site's cover is too small for a full-width banner.</summary>
    public void LoadHero() => LoadArtwork();

    private async void LoadArtwork()
    {
        if (_artwork is null || _artworkRequested) return;
        _artworkRequested = true;
        try
        {
            if (await _artwork.GetAsync(Entry.Title, Entry.Id) is not { } art) return;
            if (string.IsNullOrEmpty(Entry.CoverImageUrl) && art.HeaderUrl is not null)
            {
                _fallbackCover = art.HeaderUrl;
                OnPropertyChanged(nameof(CoverImageUrl));
                OnPropertyChanged(nameof(HasCover));
            }
            if (art.HeroUrl is not null) _heroUrl = art.HeroUrl;
            OnPropertyChanged(nameof(HeroImageUrl));
        }
        catch (Exception)
        {
            // pictures are a nicety; the monogram stays
        }
    }

    /// <summary>Two-letter monogram shown when a game has no cover art.</summary>
    public string Monogram => string.Concat(Entry.Title.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpperInvariant(w[0])));

    public string? PrimaryTag => Entry.Genres.FirstOrDefault();
    public bool HasPrimaryTag => !string.IsNullOrWhiteSpace(PrimaryTag);
    public string? SizeText => Entry.Size;
    public bool HasSize => !string.IsNullOrWhiteSpace(Entry.Size);
    public string? VersionText => Entry.Version is { Length: > 0 } v ? (char.IsDigit(v[0]) ? "v" + v : v) : null;
    public bool HasVersion => VersionText is not null;
    public string? DlcText => Entry.DlcInfo;
    public bool HasDlc => !string.IsNullOrWhiteSpace(Entry.DlcInfo);

    /// <summary>"FitGirl Repacks · SteamRIP".</summary>
    public string SourcesText => string.Join(" · ", Group.Variants.Select(v => SourceName(v.SourceId)).Distinct());
    public bool HasMultipleSources => Group.SourceCount > 1;
    public string SourceCountText => $"{Group.SourceCount} sources";

    /// <summary>"Uploaded 3 Oct 2026" (newest copy), or the release date when no upload date is known.</summary>
    public string DateText => Group.NewestUploadUtc is { } up
        ? "Uploaded " + up.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture)
        : Entry.ReleaseDate is { Length: > 0 } released ? "Released " + released : string.Empty;

    [ObservableProperty] private string _actionLabel = string.Empty;

    /// <summary>"Play" state: brass button and "In your library" chip.</summary>
    [ObservableProperty] private bool _isPlayable;

    /// <summary>Downloaded as a setup program that hasn't been run yet: "Install" button.</summary>
    [ObservableProperty] private bool _isSetupReady;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _hasProgress;

    /// <summary>Fetching details / starting the download after a click.</summary>
    [ObservableProperty] private bool _isWorking;

    /// <summary>Takes a new set of copies (search results changed, details loaded).</summary>
    public void Update(GameGroup group, IReadOnlyDictionary<Guid, string> sourceNames)
    {
        _sourceNames = sourceNames;
        if (!ReferenceEquals(Group, group)) Group = group;
        if (!ReferenceEquals(Entry, group.Primary))
        {
            Entry = group.Primary;
            if (string.IsNullOrEmpty(Entry.CoverImageUrl)) LoadArtwork();
        }
        RefreshStatus();
    }

    /// <summary>Re-reads state from <see cref="GameStatusService"/>; called when one of the copies changes.</summary>
    public void RefreshStatus()
    {
        var (id, status) = Group.Variants
            .Select(v => (Id: v.Id, Status: _status.GetStatus(v.Id)))
            .OrderBy(x => Array.IndexOf(StatePriority, x.Status.State))
            .ThenBy(x => x.Id == Entry.Id ? 0 : 1)
            .First();

        ActiveId = id;
        ActionLabel = status.Label;
        IsPlayable = status.IsPlayable;
        IsSetupReady = status.State is GameUiState.ReadyToInstall or GameUiState.Installing;
        IsBusy = status.IsBusy;
        HasProgress = status.Percent is not null && (status.IsBusy || status.State == GameUiState.Paused);
        Progress = status.Percent ?? 0;
    }

    private string SourceName(Guid sourceId) => _sourceNames.TryGetValue(sourceId, out var name) ? name : "Unknown source";

    [RelayCommand]
    private void Open() => _navigation.ShowGame(ActiveId);

    /// <summary>The × on the card: "not a game / not interested", hides it from the store.</summary>
    [RelayCommand]
    private void Hide() => _hide?.Invoke(this);

    [RelayCommand]
    private Task PrimaryActionAsync() => RunSafeAsync(async () =>
    {
        IsWorking = true;
        try
        {
            await _actions.ExecutePrimaryAsync(ActiveId);
        }
        finally
        {
            IsWorking = false;
        }
    });
}
