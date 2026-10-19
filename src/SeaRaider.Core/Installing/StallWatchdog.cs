namespace SeaRaider.Core.Installing;

/// <summary>Change reported by <see cref="StallWatchdog.Add"/>.</summary>
public enum StallTransition
{
    None,

    /// <summary>The setup just went idle for the whole window.</summary>
    BecameStalled,

    /// <summary>The setup was stalled and is busy again.</summary>
    Resumed,
}

/// <summary>
/// Decides whether a setup is working or waiting, from CPU time and disk I/O of its process tree.
/// A setup that's extracting or copying burns CPU and writes to disk constantly; a wizard waiting on a
/// question (or playing its music) barely does either. "Stalled" means the average over the whole window
/// stays under both thresholds. Pure logic, so it's unit-testable.
/// </summary>
public sealed class StallWatchdog
{
    private readonly Queue<(DateTime At, double CpuSeconds, long IoBytes)> _samples = new();
    private readonly TimeSpan _window;
    private readonly double _cpuFraction;
    private readonly long _ioBytesPerSecond;
    private DateTime? _startedAt;
    private DateTime? _stalledSince;

    /// <param name="window">How long the setup must be idle (e.g. 15 seconds).</param>
    /// <param name="cpuFraction">Average share of one core below which it counts as idle (music players use 1–3 %).</param>
    /// <param name="ioBytesPerSecond">Average disk I/O below which it counts as idle.</param>
    public StallWatchdog(TimeSpan window, double cpuFraction = 0.05, long ioBytesPerSecond = 512 * 1024)
    {
        _window = window;
        _cpuFraction = cpuFraction;
        _ioBytesPerSecond = ioBytesPerSecond;
    }

    public bool IsStalled => _stalledSince is not null;

    /// <summary>How long it has been stalled (zero when working).</summary>
    public TimeSpan StalledFor(DateTime now) => _stalledSince is { } since ? now - since + _window : TimeSpan.Zero;

    /// <summary>Adds one sample: CPU time and I/O bytes used since the previous sample.</summary>
    public StallTransition Add(DateTime now, TimeSpan cpuDelta, long ioDelta)
    {
        _startedAt ??= now;
        _samples.Enqueue((now, cpuDelta.TotalSeconds, Math.Max(0, ioDelta)));
        while (_samples.Count > 0 && now - _samples.Peek().At > _window) _samples.Dequeue();

        if (now - _startedAt < _window) return StallTransition.None; // not enough history yet

        var seconds = _window.TotalSeconds;
        var cpu = _samples.Sum(s => s.CpuSeconds);
        var io = _samples.Sum(s => s.IoBytes);
        var idle = cpu < seconds * _cpuFraction && io < seconds * _ioBytesPerSecond;

        if (idle && _stalledSince is null)
        {
            _stalledSince = now;
            return StallTransition.BecameStalled;
        }
        if (!idle && _stalledSince is not null)
        {
            _stalledSince = null;
            return StallTransition.Resumed;
        }
        return StallTransition.None;
    }

    /// <summary>Forget history (after restarting the setup).</summary>
    public void Reset()
    {
        _samples.Clear();
        _startedAt = null;
        _stalledSince = null;
    }
}
