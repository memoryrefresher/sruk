using System.Text;
using SeaRaider.Core.Downloads;
using SeaRaider.Core.Installing;
using SeaRaider.Core.Models;

namespace SeaRaider.Core.Tests;

public class InstallerAndQueueTests
{
    // ---- setup engine detection -------------------------------------------------------------------

    private static byte[] Bytes(params string[] markers) =>
        [.. new byte[128], .. markers.SelectMany(m => Encoding.ASCII.GetBytes(m).Concat(new byte[32])), .. new byte[128]];

    private static SetupEngineInfo Detect(byte[] head, string ext = ".exe", params string[] siblings) =>
        SetupEngineDetector.Detect(head, Array.Empty<byte>(), ext, siblings);

    [Fact]
    public void Detects_inno_setup_and_its_version()
    {
        var info = Detect(Bytes("Inno Setup Setup Data (6.2.2)"));
        Assert.Equal(InstallerKind.InnoSetup, info.Kind);
        Assert.Equal("6.2.2", info.EngineVersion);
        Assert.False(info.IsRepack);
    }

    [Fact]
    public void Inno_with_isdone_and_fg_files_is_a_fitgirl_repack()
    {
        var info = Detect(Bytes("Inno Setup Setup Data (6.2.0)"), ".exe", "setup.exe", "isdone.dll", "fg-01.bin");
        Assert.Equal(InstallerKind.InnoSetup, info.Kind);
        Assert.True(info.IsRepack);
        Assert.Equal("FitGirl", info.RepackGroup);
    }

    [Fact]
    public void Utf16_version_info_counts() =>
        Assert.Equal(InstallerKind.Nsis, Detect([.. new byte[64], .. Encoding.Unicode.GetBytes("Nullsoft Install System"), .. new byte[64]]).Kind);

    [Fact]
    public void Burn_bundle_wins_over_the_engines_it_embeds() =>
        Assert.Equal(InstallerKind.WixBurn, Detect(Bytes(".wixburn", "Inno Setup Setup Data (6.0.0)")).Kind);

    [Fact]
    public void Msi_is_recognised_by_extension_or_ole_header()
    {
        Assert.Equal(InstallerKind.Msi, Detect(Bytes("anything"), ".msi").Kind);
        Assert.Equal(InstallerKind.Msi, Detect([0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0, 0]).Kind);
    }

    [Fact]
    public void Unknown_exe_next_to_repack_files_is_treated_as_inno_repack()
    {
        Assert.Equal(InstallerKind.Unknown, Detect(Bytes("nothing to see")).Kind);
        var info = Detect(Bytes("nothing to see"), ".exe", "setup.exe", "unarc.dll");
        Assert.Equal(InstallerKind.InnoSetup, info.Kind);
        Assert.True(info.IsRepack);
    }

    [Fact]
    public void Repacks_get_a_visible_progress_window_others_run_hidden()
    {
        var repack = new SetupEngineInfo(InstallerKind.InnoSetup, null, true, "FitGirl", "test");
        var plain = new SetupEngineInfo(InstallerKind.InnoSetup, null, false, null, "test");

        Assert.Equal(InstallMode.SilentWithProgress, SetupEngineDetector.ChooseMode(repack, silentRequested: true, showProgress: false));
        Assert.Equal(InstallMode.Silent, SetupEngineDetector.ChooseMode(plain, silentRequested: true, showProgress: false));
        Assert.Equal(InstallMode.SilentWithProgress, SetupEngineDetector.ChooseMode(plain, silentRequested: true, showProgress: true));
        Assert.Equal(InstallMode.Interactive, SetupEngineDetector.ChooseMode(plain, silentRequested: false, showProgress: false));
        Assert.Equal(InstallMode.Interactive, SetupEngineDetector.ChooseMode(SetupEngineInfo.Unknown, silentRequested: true, showProgress: false));
    }

    [Fact]
    public void Inno_switches_per_mode()
    {
        var (_, silent) = SetupEngineDetector.BuildCommand(@"C:\s\setup.exe", InstallerKind.InnoSetup, @"D:\Games\Hades", InstallMode.Silent, null);
        Assert.Contains("/VERYSILENT", silent);
        Assert.Contains("/SUPPRESSMSGBOXES", silent);
        Assert.Contains("/SP-", silent);
        Assert.Contains("/DIR=\"D:\\Games\\Hades\"", silent);

        var (_, progress) = SetupEngineDetector.BuildCommand(@"C:\s\setup.exe", InstallerKind.InnoSetup, @"D:\Games\Hades", InstallMode.SilentWithProgress, null);
        Assert.Contains("/SILENT", progress);
        Assert.DoesNotContain("/VERYSILENT", progress);
    }

    [Fact]
    public void Nsis_dir_is_last_and_unquoted_msi_goes_through_msiexec()
    {
        var (_, nsis) = SetupEngineDetector.BuildCommand(@"C:\s\setup.exe", InstallerKind.Nsis, @"D:\My Games\X", InstallMode.Silent, null);
        Assert.Equal(@"/S /D=D:\My Games\X", nsis);

        var (file, msi) = SetupEngineDetector.BuildCommand(@"C:\s\game.msi", InstallerKind.Msi, @"D:\G", InstallMode.Silent, null);
        Assert.Equal("msiexec.exe", file);
        Assert.StartsWith("/i \"C:\\s\\game.msi\" /qn", msi);
    }

    // ---- stall watchdog ---------------------------------------------------------------------------

    [Fact]
    public void Watchdog_flags_idle_setups_after_the_window_and_notices_when_they_resume()
    {
        var watchdog = new StallWatchdog(TimeSpan.FromSeconds(15));
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // 2 % CPU (background music), no disk: idle, but only once 15 s of history exist.
        for (var s = 0; s < 15; s++)
            Assert.Equal(StallTransition.None, watchdog.Add(start.AddSeconds(s), TimeSpan.FromMilliseconds(20), 0));
        Assert.Equal(StallTransition.BecameStalled, watchdog.Add(start.AddSeconds(15), TimeSpan.FromMilliseconds(20), 0));
        Assert.True(watchdog.IsStalled);

        Assert.Equal(StallTransition.Resumed, watchdog.Add(start.AddSeconds(16), TimeSpan.FromSeconds(1), 20_000_000));
        Assert.False(watchdog.IsStalled);
    }

    [Fact]
    public void Watchdog_never_flags_a_busy_setup()
    {
        var watchdog = new StallWatchdog(TimeSpan.FromSeconds(15));
        var start = DateTime.UtcNow;
        for (var s = 0; s < 40; s++)
            Assert.Equal(StallTransition.None, watchdog.Add(start.AddSeconds(s), TimeSpan.FromMilliseconds(400), 5_000_000));
    }

    // ---- download queue ---------------------------------------------------------------------------

    private static DownloadJob Job(string title, long position, DownloadJobStatus status = DownloadJobStatus.Queued,
        DownloadTransport transport = DownloadTransport.Http, bool force = false) =>
        new() { GameTitle = title, QueuePosition = position, Status = status, Transport = transport, ForceStart = force };

    [Fact]
    public void Starts_jobs_in_queue_order_up_to_the_limit()
    {
        var running = Job("A", 1, DownloadJobStatus.Downloading);
        var jobs = new[] { running, Job("D", 4), Job("B", 2), Job("C", 3) };

        var picks = DownloadQueueManager.SelectJobsToStart(jobs, id => id == running.Id, new QueueLimits(3, 2));

        Assert.Equal(new[] { "B", "C" }, picks.Select(j => j.GameTitle));
    }

    [Fact]
    public void A_torrent_over_its_limit_lets_an_http_job_behind_it_start()
    {
        var running = Job("A", 1, DownloadJobStatus.Downloading, DownloadTransport.Torrent);
        var jobs = new[] { running, Job("B", 2, transport: DownloadTransport.Torrent), Job("C", 3) };

        var picks = DownloadQueueManager.SelectJobsToStart(jobs, id => id == running.Id, new QueueLimits(3, 1));

        Assert.Equal(new[] { "C" }, picks.Select(j => j.GameTitle));
    }

    [Fact]
    public void Force_start_ignores_the_limits()
    {
        var running = Job("A", 1, DownloadJobStatus.Downloading);
        var jobs = new[] { running, Job("B", 2), Job("C", 3, force: true) };

        var picks = DownloadQueueManager.SelectJobsToStart(jobs, id => id == running.Id, new QueueLimits(1, 1));

        Assert.Equal(new[] { "C" }, picks.Select(j => j.GameTitle));
    }

    [Fact]
    public void Extracting_jobs_have_given_their_slot_back()
    {
        var extracting = Job("A", 1, DownloadJobStatus.Extracting);
        var jobs = new[] { extracting, Job("B", 2) };

        var picks = DownloadQueueManager.SelectJobsToStart(jobs, id => id == extracting.Id, new QueueLimits(1, 1));

        Assert.Equal(new[] { "B" }, picks.Select(j => j.GameTitle));
    }

    [Theory]
    [InlineData(QueueMove.Up, "A,C,B")]
    [InlineData(QueueMove.Down, "A,B,C")]
    [InlineData(QueueMove.Top, "C,A,B")]
    [InlineData(QueueMove.Bottom, "A,B,C")]
    public void Reorders_and_renumbers(QueueMove move, string expected)
    {
        var queue = new List<DownloadJob> { Job("A", 1), Job("B", 2), Job("C", 3) };
        DownloadQueueManager.Move(queue, queue[2].Id, move);

        Assert.Equal(expected, string.Join(",", queue.Select(j => j.GameTitle)));
        Assert.Equal(new long[] { 1, 2, 3 }, queue.Select(j => j.QueuePosition));
    }

    [Fact]
    public void Global_limit_is_split_between_http_and_torrents()
    {
        var settings = new AppSettings { GlobalDownloadLimitKBps = 1000, GlobalUploadLimitKBps = 200 };

        var share = DownloadQueueManager.ShareGlobalLimits(settings, activeHttp: 1, activeTorrents: 1);
        Assert.Equal(512_000, share.HttpDownloadBytesPerSecond);
        Assert.Equal(512_000, share.TorrentDownloadBytesPerSecond);
        Assert.Equal(204_800, share.TorrentUploadBytesPerSecond);

        Assert.Equal(0, DownloadQueueManager.ShareGlobalLimits(new AppSettings(), 2, 1).HttpDownloadBytesPerSecond);
    }

    // ---- bandwidth ----------------------------------------------------------------------------------

    private sealed class ManualTime : TimeProvider
    {
        public long Ticks;
        public override long GetTimestamp() => Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void Advance(TimeSpan by) => Ticks += by.Ticks;
    }

    [Fact]
    public void Limiter_allows_a_burst_then_paces_to_the_rate()
    {
        var time = new ManualTime();
        var limiter = new BandwidthLimiter(time);
        limiter.SetLimit(100_000);

        Assert.Equal(TimeSpan.Zero, limiter.Reserve(65_536));                         // the burst allowance
        Assert.Equal(1.0, limiter.Reserve(100_000).TotalSeconds, precision: 3);       // a second's worth over

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.Zero, limiter.Reserve(0));                              // paid back
    }

    [Fact]
    public void Unlimited_never_waits() => Assert.Equal(TimeSpan.Zero, new BandwidthLimiter().Reserve(100_000_000));

    [Fact]
    public async Task Slots_queue_waiters_and_grow_live()
    {
        var slots = new AdjustableSlots(1);
        var first = await slots.AcquireAsync(CancellationToken.None);
        var second = slots.AcquireAsync(CancellationToken.None);
        Assert.False(second.IsCompleted);

        slots.Capacity = 2;
        (await second.WaitAsync(TimeSpan.FromSeconds(1))).Dispose();
        first.Dispose();
        Assert.Equal(0, slots.InUse);
    }
}
