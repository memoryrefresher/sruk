using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SeaRaider.App.Services;
using SeaRaider.Core.Catalog;
using SeaRaider.Core;
using SeaRaider.Core.Abstractions;
using SeaRaider.Core.Downloads;
using SeaRaider.Core.Models;

namespace SeaRaider.App.ViewModels;

/// <summary>
/// The Download Manager page, laid out like a BitTorrent client's queue: unfinished jobs in start order
/// (numbered, with Move Up / Down / Top / Bottom, Force start, Pause / Resume and per-job speed limits), then
/// finished jobs. Limits and slot counts live in Settings › Downloads.
/// </summary>
public sealed partial class DownloadsViewModel : PageViewModel
{
    /// <summary>Settings → "Show advanced options": each row's technical details start open.</summary>
    private bool ShowDetails => _repository.GetSettings().ShowAdvancedOptions;

    private readonly IDownloadAndExtractManager _downloads;
    private readonly GameActionService _actions;
    private readonly GameStatusService _statusService;
    private readonly ShellService _shell;
    private readonly NavigationService _navigation;
    private readonly ILibraryRepository _repository;

    public DownloadsViewModel(IDownloadAndExtractManager downloads, GameStatusService status, GameActionService actions,
        ShellService shell, NavigationService navigation, ILibraryRepository repository)
    {
        _downloads = downloads;
        _actions = actions;
        _statusService = status;
        _shell = shell;
        _navigation = navigation;
        _repository = repository;

        foreach (var job in downloads.Jobs) Place(new DownloadJobViewModel(job, downloads, actions, status, shell, ShowDetails));
        UpdateSummary();

        status.JobUpdated += OnJobUpdated;
        status.GameChanged += id =>
        {
            foreach (var row in Queue.Concat(Finished).Where(j => j.GameId == id)) row.RefreshInstallState();
        };
        status.JobRemoved += id =>
        {
            if (Queue.FirstOrDefault(j => j.Id == id) is { } queued) Queue.Remove(queued);
            if (Finished.FirstOrDefault(j => j.Id == id) is { } finished) Finished.Remove(finished);
            UpdateSummary();
        };
        repository.SettingsChanged += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(UpdateSummary);
    }

    /// <summary>Unfinished jobs in queue order.</summary>
    public ObservableCollection<DownloadJobViewModel> Queue { get; } = [];

    /// <summary>Completed, cancelled jobs, newest first.</summary>
    public ObservableCollection<DownloadJobViewModel> Finished { get; } = [];

    [ObservableProperty] private int _activeCount;
    [ObservableProperty] private int _queuedCount;
    [ObservableProperty] private string _totalSpeedText = string.Empty;
    [ObservableProperty] private string _limitsText = string.Empty;
    [ObservableProperty] private bool _hasFinished;

    public bool IsEmpty => Queue.Count == 0 && Finished.Count == 0;
    public bool HasQueue => Queue.Count > 0;

    public override void OnNavigatedTo() => UpdateSummary();

    private void OnJobUpdated(DownloadJobSnapshot snapshot)
    {
        var row = Queue.FirstOrDefault(j => j.Id == snapshot.Id) ?? Finished.FirstOrDefault(j => j.Id == snapshot.Id);
        if (row is null)
        {
            Place(new DownloadJobViewModel(snapshot, _downloads, _actions, _statusService, _shell, ShowDetails));
        }
        else
        {
            var wasQueued = Queue.Contains(row);
            var oldPosition = row.QueuePosition;
            row.Update(snapshot);
            if (wasQueued != row.IsInQueue || oldPosition != row.QueuePosition)
            {
                Queue.Remove(row);
                Finished.Remove(row);
                Place(row);
            }
        }
        UpdateSummary();
    }

    /// <summary>Inserts a row in the right list at the right place.</summary>
    private void Place(DownloadJobViewModel row)
    {
        if (row.IsInQueue)
        {
            var index = 0;
            while (index < Queue.Count && Queue[index].QueuePosition <= row.QueuePosition) index++;
            Queue.Insert(index, row);
        }
        else
        {
            Finished.Insert(0, row);
        }
    }

    private void UpdateSummary()
    {
        for (var i = 0; i < Queue.Count; i++)
        {
            Queue[i].QueueNumber = i + 1;
            Queue[i].IsFirst = i == 0;
            Queue[i].IsLast = i == Queue.Count - 1;
        }

        ActiveCount = Queue.Count(j => j.Status == DownloadJobStatus.Downloading);
        QueuedCount = Queue.Count(j => j.Status == DownloadJobStatus.Queued);
        TotalSpeedText = Formatting.Speed(Queue.Sum(j => j.BytesPerSecond));
        HasFinished = Finished.Count > 0;

        var s = _repository.GetSettings();
        var down = s.GlobalDownloadLimitKBps > 0 ? $"limit {Formatting.Speed(s.GlobalDownloadLimitKBps * 1024.0)}" : "no speed limit";
        LimitsText = $"{s.MaxConcurrentJobs} at a time ({s.MaxActiveTorrents} torrent{(s.MaxActiveTorrents == 1 ? "" : "s")}), {down}";

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasQueue));
    }

    [RelayCommand]
    private Task ClearFinishedAsync() => RunSafeAsync(async () =>
    {
        foreach (var job in Finished.Where(j => j.CanRemove).ToList())
            await _downloads.RemoveAsync(job.Id);
    });

    [RelayCommand]
    private Task PauseAllAsync() => RunSafeAsync(async () =>
    {
        foreach (var job in Queue.Where(j => j.CanPause).ToList()) await _downloads.PauseAsync(job.Id);
    });

    [RelayCommand]
    private Task ResumeAllAsync() => RunSafeAsync(async () =>
    {
        foreach (var job in Queue.Where(j => j.Status == DownloadJobStatus.Paused).ToList()) await _downloads.ResumeAsync(job.Id);
    });

    [RelayCommand]
    private void OpenDownloadSettings()
    {
        _navigation.RequestedSettingsTab = SettingsTab.Downloads;
        _navigation.ShowPage(AppPage.Settings);
    }
}

/// <summary>One row in the Download Manager.</summary>
public sealed partial class DownloadJobViewModel : ViewModelBase
{
    private readonly IDownloadAndExtractManager _downloads;
    private readonly GameActionService _actions;
    private readonly GameStatusService _statusService;
    private readonly ShellService _shell;

    /// <param name="showDetails">Settings → "Show advanced options": the Details section starts open.</param>
    public DownloadJobViewModel(DownloadJobSnapshot snapshot, IDownloadAndExtractManager downloads, GameActionService actions,
        GameStatusService status, ShellService shell, bool showDetails = false)
    {
        ShowDetails = showDetails;
        DetailsOpen = showDetails;
        _downloads = downloads;
        _actions = actions;
        _statusService = status;
        _shell = shell;
        IsTorrent = snapshot.Transport == DownloadTransport.Torrent;
        SourceUrl = snapshot.PrimaryUrl;
        Id = snapshot.Id;
        GameId = snapshot.GameId;
        Title = DisplayText.Title(snapshot.GameTitle);
        CoverImageUrl = snapshot.CoverImageUrl;
        Transport = snapshot.Transport == DownloadTransport.Torrent ? "Torrent" : "Direct download";
        Update(snapshot);
        EditDownloadLimit = snapshot.DownloadLimitKBps;
        EditUploadLimit = snapshot.UploadLimitKBps;
    }

    public Guid Id { get; }
    public string GameId { get; }
    public string Title { get; }
    public string? CoverImageUrl { get; }
    public string Transport { get; }
    public bool IsTorrent { get; }

    /// <summary>Magnet / .torrent / file URL, for "Copy link" (e.g. to test a torrent in another client).</summary>
    public string? SourceUrl { get; }
    public bool CanCopyLink => !string.IsNullOrEmpty(SourceUrl);
    public string CopyLinkLabel => IsTorrent && SourceUrl?.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) == true
        ? "Copy magnet link"
        : "Copy link";

    [ObservableProperty] private string? _copyFeedback;
    public double BytesPerSecond { get; private set; }

    [ObservableProperty] private DownloadJobStatus _status;
    [ObservableProperty] private string _statusText = string.Empty;

    // ---- the friendly view: three steps and one sentence ------------------------------------------
    /// <summary>Settings → "Show advanced options": details open and the queue arrows visible.</summary>
    public bool ShowDetails { get; }

    /// <summary>Parts, hosts, speeds … (folded away by default).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailsToggleText))]
    private bool _detailsOpen;

    public string DetailsToggleText => DetailsOpen ? "Hide details" : "Details";

    public bool ShowReorder => ShowDetails && CanReorder;

    /// <summary>"37%" while downloading or unpacking, else nothing.</summary>
    [ObservableProperty] private string _percentText = string.Empty;

    public bool ShowDownloadBar => Status is DownloadJobStatus.Queued or DownloadJobStatus.Downloading or DownloadJobStatus.Paused
        or DownloadJobStatus.Failed;

    /// <summary>"Downloading… 42% · about 12m left", "Unpacking the game files… 60%", "Ready to play!".</summary>
    [ObservableProperty] private string _headline = string.Empty;
    [ObservableProperty] private bool _step1Current;
    [ObservableProperty] private bool _step1Done;
    [ObservableProperty] private bool _step2Current;
    [ObservableProperty] private bool _step2Done;
    [ObservableProperty] private bool _step3Current;
    [ObservableProperty] private bool _step3Done;
    [ObservableProperty] private string _step3Label = "Ready to play";
    [ObservableProperty] private string _detailText = string.Empty;
    [ObservableProperty] private double _downloadPercent;
    [ObservableProperty] private bool _isDownloadIndeterminate;
    [ObservableProperty] private double _extractionPercent;
    [ObservableProperty] private bool _showExtraction;
    [ObservableProperty] private string _sizeText = string.Empty;
    [ObservableProperty] private string _speedText = string.Empty;
    [ObservableProperty] private string _etaText = string.Empty;
    [ObservableProperty] private string? _peersText;
    /// <summary>Why the job failed (named so it doesn't clash with ViewModelBase.ErrorMessage/HasError).</summary>
    [ObservableProperty] private string? _jobError;

    /// <summary>The download produced a setup program that hasn't been run yet.</summary>
    [ObservableProperty] private bool _canRunInstaller;

    // ---- queue -----------------------------------------------------------------------------------

    public long QueuePosition { get; private set; }

    /// <summary>1-based place in the visible queue ("#2").</summary>
    [ObservableProperty] private int _queueNumber;
    [ObservableProperty] private bool _isFirst;
    [ObservableProperty] private bool _isLast;
    [ObservableProperty] private bool _isForced;
    [ObservableProperty] private bool _isSeeding;
    [ObservableProperty] private string? _seedText;
    [ObservableProperty] private string? _limitText;

    /// <summary>Editor values for the "Speed limit" flyout (KB/s, 0 = none).</summary>
    [ObservableProperty] private decimal _editDownloadLimit;
    [ObservableProperty] private decimal _editUploadLimit;

    public bool IsInQueue => Status is not (DownloadJobStatus.Completed or DownloadJobStatus.Cancelled);
    public bool IsActive => Status is DownloadJobStatus.Queued or DownloadJobStatus.Downloading
        or DownloadJobStatus.Extracting or DownloadJobStatus.Finalizing;
    public bool IsFinished => Status is DownloadJobStatus.Completed or DownloadJobStatus.Cancelled or DownloadJobStatus.Failed;
    public bool CanPause => Status is DownloadJobStatus.Queued or DownloadJobStatus.Downloading;
    public bool CanResume => Status is DownloadJobStatus.Paused or DownloadJobStatus.Failed;
    public bool CanForceStart => Status is DownloadJobStatus.Queued or DownloadJobStatus.Paused or DownloadJobStatus.Failed && !IsForced;
    public bool CanReorder => IsInQueue;
    public bool CanMoveUp => IsInQueue && !IsFirst;
    public bool CanMoveDown => IsInQueue && !IsLast;
    public bool CanCancel => Status is DownloadJobStatus.Queued or DownloadJobStatus.Downloading
        or DownloadJobStatus.Paused or DownloadJobStatus.Extracting;
    public bool CanRemove => IsFinished && !IsSeeding;
    public bool CanSetLimits => IsInQueue || IsSeeding;
    public bool HasJobError => !string.IsNullOrEmpty(JobError);
    public bool HasPeers => !string.IsNullOrEmpty(PeersText);
    public bool HasLimit => !string.IsNullOrEmpty(LimitText);
    public bool IsCompleted => Status == DownloadJobStatus.Completed;
    public bool IsWaiting => Status == DownloadJobStatus.Queued;
    public string ResumeLabel => Status == DownloadJobStatus.Failed ? "Retry" : "Resume";
    public string QueueLabel => $"#{QueueNumber}";

    partial void OnQueueNumberChanged(int value) => OnPropertyChanged(nameof(QueueLabel));
    partial void OnIsFirstChanged(bool value) => OnPropertyChanged(nameof(CanMoveUp));
    partial void OnIsLastChanged(bool value) => OnPropertyChanged(nameof(CanMoveDown));

    public void Update(DownloadJobSnapshot s)
    {
        Status = s.Status;
        QueuePosition = s.QueuePosition;
        IsForced = s.ForceStart;
        IsSeeding = s.IsSeeding;
        BytesPerSecond = s.Status == DownloadJobStatus.Downloading ? s.BytesPerSecond : 0;

        StatusText = s.Status switch
        {
            DownloadJobStatus.Queued => s.ForceStart ? "Starting" : "Queued",
            DownloadJobStatus.Downloading => "Downloading",
            DownloadJobStatus.Paused => "Paused",
            DownloadJobStatus.Extracting => "Extracting",
            DownloadJobStatus.Finalizing => "Finishing",
            DownloadJobStatus.Completed => s.IsSeeding ? "Seeding" : s.ProducedInstaller ? "Ready to install" : "Installed",
            DownloadJobStatus.Failed => "Failed",
            DownloadJobStatus.Cancelled => "Cancelled",
            _ => s.Status.ToString(),
        };
        DetailText = s.Status == DownloadJobStatus.Queued && !s.ForceStart && string.IsNullOrEmpty(s.StatusDetail)
            ? "Waiting for a free download slot"
            : s.StatusDetail;
        JobError = s.Error;
        _lastSnapshot = s;
        UpdateSteps(s);

        DownloadPercent = s.Status is DownloadJobStatus.Extracting or DownloadJobStatus.Finalizing or DownloadJobStatus.Completed
            ? 100
            : s.DownloadPercent ?? 0;
        IsDownloadIndeterminate = s.Status == DownloadJobStatus.Downloading && s.DownloadPercent is null;
        ShowExtraction = s.Status is DownloadJobStatus.Extracting or DownloadJobStatus.Finalizing;
        ExtractionPercent = s.ExtractionPercent;
        PercentText = s.Status switch
        {
            DownloadJobStatus.Downloading when s.DownloadPercent is { } p => $"{p:0}%",
            DownloadJobStatus.Extracting => $"{s.ExtractionPercent:0}%",
            _ => string.Empty,
        };

        SizeText = s.TotalBytes > 0
            ? $"{Formatting.Bytes(s.BytesDownloaded)} of {Formatting.Bytes(s.TotalBytes)}"
            : Formatting.Bytes(s.BytesDownloaded);
        SpeedText = s.Status == DownloadJobStatus.Downloading ? Formatting.Speed(s.BytesPerSecond) : string.Empty;
        EtaText = s.Status == DownloadJobStatus.Downloading && s.Eta is not null ? $"{Formatting.Duration(s.Eta)} left" : string.Empty;
        PeersText = s.Transport == DownloadTransport.Torrent && s.Status == DownloadJobStatus.Downloading
            ? $"Connected to {s.Seeds + s.Peers} ({s.Seeds} seeds), {s.PeersFound} found"
            : null;
        SeedText = s.IsSeeding ? $"Uploading {Formatting.Speed(s.UploadBytesPerSecond)} · ratio {s.SeedRatio:0.00}" : null;

        var limits = new List<string>();
        if (s.DownloadLimitKBps > 0) limits.Add($"↓ {Formatting.Speed(s.DownloadLimitKBps * 1024.0)}");
        if (s.UploadLimitKBps > 0 && IsTorrent) limits.Add($"↑ {Formatting.Speed(s.UploadLimitKBps * 1024.0)}");
        LimitText = limits.Count > 0 ? "Limited to " + string.Join(", ", limits) : null;

        foreach (var name in new[] { nameof(IsInQueue), nameof(IsActive), nameof(IsFinished), nameof(CanPause), nameof(CanResume),
                     nameof(CanForceStart), nameof(CanReorder), nameof(CanMoveUp), nameof(CanMoveDown), nameof(CanCancel),
                     nameof(CanRemove), nameof(CanSetLimits), nameof(HasJobError), nameof(HasPeers), nameof(HasLimit),
                     nameof(IsCompleted), nameof(IsWaiting), nameof(ResumeLabel), nameof(ShowReorder), nameof(ShowDownloadBar) })
            OnPropertyChanged(name);
        RefreshInstallState();
    }

    /// <summary>Re-checks whether the produced setup is still waiting (it may have been run from elsewhere).</summary>
    public void RefreshInstallState()
    {
        CanRunInstaller = Status == DownloadJobStatus.Completed && _statusService.GetStatus(GameId).State == GameUiState.ReadyToInstall;
        if (_lastSnapshot is not null) UpdateSteps(_lastSnapshot);
    }

    private DownloadJobSnapshot? _lastSnapshot;

    /// <summary>A little thank-you for the disk space SeaRaider kept free.</summary>
    private static string SpaceNote(DownloadJobSnapshot s) => s.SpaceSavedBytes <= 0
        ? string.Empty
        : s.StreamedWhileDownloading
            ? $" Bonus: we saved you {Formatting.Bytes(s.SpaceSavedBytes)} of disk space by unpacking as we went."
            : $" We tidied up {Formatting.Bytes(s.SpaceSavedBytes)} of download files for you.";

    /// <summary>Download → Unpack → Install / Play, and what's happening in one plain sentence.</summary>
    private void UpdateSteps(DownloadJobSnapshot s)
    {
        var game = _statusService.GetStatus(GameId).State;
        var downloading = s.Status is DownloadJobStatus.Queued or DownloadJobStatus.Downloading or DownloadJobStatus.Paused
            or DownloadJobStatus.Failed or DownloadJobStatus.Cancelled;
        var unpacking = s.Status is DownloadJobStatus.Extracting or DownloadJobStatus.Finalizing;
        var done = s.Status == DownloadJobStatus.Completed;
        var needsSetup = done && s.ProducedInstaller && game is GameUiState.ReadyToInstall or GameUiState.Installing;

        Step1Current = downloading;
        Step1Done = unpacking || done;
        Step2Current = unpacking;
        Step2Done = done;
        Step3Current = needsSetup;
        Step3Done = done && !needsSetup;
        Step3Label = s.ProducedInstaller ? "Install" : "Ready to play";

        Headline = s.Status switch
        {
            DownloadJobStatus.Queued when s.ForceStart => "Starting…",
            DownloadJobStatus.Queued => "Waiting its turn. It starts by itself as soon as a download slot is free.",
            DownloadJobStatus.Downloading => s.DownloadPercent is { } percent
                ? $"Downloading… {percent:0}%" + (s.Eta is { } eta ? $" · about {Formatting.Duration(eta)} left" : string.Empty)
                : "Downloading…",
            DownloadJobStatus.Paused => "Paused. Press Resume to continue where it stopped.",
            DownloadJobStatus.Extracting => $"Unpacking the game files… {s.ExtractionPercent:0}%",
            DownloadJobStatus.Finalizing => "Almost done: moving the game into your Library…",
            DownloadJobStatus.Completed when game == GameUiState.Installing => "Installing… the setup runs by itself." + SpaceNote(s),
            DownloadJobStatus.Completed when needsSetup => "Downloaded. Press Install to set the game up." + SpaceNote(s),
            DownloadJobStatus.Completed when s.IsSeeding => "Ready to play! Sharing the download with others for a little while.",
            DownloadJobStatus.Completed => "Ready to play! You'll find it in your Library." + SpaceNote(s),
            DownloadJobStatus.Failed => "This download stopped. Press Retry to try again; the reason is below.",
            DownloadJobStatus.Cancelled => "Cancelled.",
            _ => s.Status.ToString(),
        };
    }

    [RelayCommand]
    private Task RunInstallerAsync() => RunSafeAsync(() => _actions.RunInstallerAsync(GameId));

    [RelayCommand]
    private Task CopyLinkAsync() => RunSafeAsync(async () =>
    {
        if (SourceUrl is null) return;
        await _shell.CopyToClipboardAsync(SourceUrl);
        CopyFeedback = "Copied";
    });

    [RelayCommand]
    private Task PauseAsync() => RunSafeAsync(() => _downloads.PauseAsync(Id));

    [RelayCommand]
    private Task ResumeAsync() => RunSafeAsync(() => _downloads.ResumeAsync(Id));

    [RelayCommand]
    private Task ForceStartAsync() => RunSafeAsync(() => _downloads.ForceStartAsync(Id));

    [RelayCommand]
    private Task MoveUpAsync() => RunSafeAsync(() => _downloads.MoveAsync(Id, QueueMove.Up));

    [RelayCommand]
    private Task MoveDownAsync() => RunSafeAsync(() => _downloads.MoveAsync(Id, QueueMove.Down));

    [RelayCommand]
    private Task MoveToTopAsync() => RunSafeAsync(() => _downloads.MoveAsync(Id, QueueMove.Top));

    [RelayCommand]
    private Task MoveToBottomAsync() => RunSafeAsync(() => _downloads.MoveAsync(Id, QueueMove.Bottom));

    [RelayCommand]
    private Task ApplyLimitsAsync() =>
        RunSafeAsync(() => _downloads.SetJobLimitsAsync(Id, (int)Math.Max(0, EditDownloadLimit), (int)Math.Max(0, EditUploadLimit)));

    [RelayCommand]
    private Task ClearLimitsAsync() => RunSafeAsync(async () =>
    {
        EditDownloadLimit = 0;
        EditUploadLimit = 0;
        await _downloads.SetJobLimitsAsync(Id, 0, 0);
    });

    [RelayCommand]
    private Task StopSeedingAsync() => RunSafeAsync(() => _downloads.StopSeedingAsync(Id));

    [RelayCommand]
    private Task CancelAsync() => RunSafeAsync(() => _downloads.CancelAsync(Id));

    [RelayCommand]
    private Task RemoveAsync() => RunSafeAsync(() => _downloads.RemoveAsync(Id));
}
