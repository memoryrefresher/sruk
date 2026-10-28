using System.Diagnostics;

namespace SeaRaider.Core.Downloads;

/// <summary>
/// Sliding-window throughput meter (bytes/second over the last few seconds) — smoother than an
/// instantaneous rate, more responsive than an all-time average.
/// </summary>
internal sealed class TransferRateMeter(TimeSpan? window = null)
{
    private readonly long _windowTicks = (long)((window ?? TimeSpan.FromSeconds(4)).TotalSeconds * Stopwatch.Frequency);
    private readonly Queue<(long Timestamp, long Bytes)> _samples = new();

    /// <summary>Records the running byte total and returns the current rate.</summary>
    public double Sample(long totalBytes)
    {
        var now = Stopwatch.GetTimestamp();
        _samples.Enqueue((now, totalBytes));
        while (_samples.Count > 2 && now - _samples.Peek().Timestamp > _windowTicks) _samples.Dequeue();

        var (oldestTime, oldestBytes) = _samples.Peek();
        var elapsedSeconds = (now - oldestTime) / (double)Stopwatch.Frequency;
        return elapsedSeconds <= 0.05 ? 0 : (totalBytes - oldestBytes) / elapsedSeconds;
    }

    public void Reset() => _samples.Clear();
}

/// <summary>
/// <see cref="IProgress{T}"/> that invokes its callback synchronously on the reporting thread.
/// (<see cref="Progress{T}"/> posts to a SynchronizationContext / the thread pool, which can
/// reorder reports and adds overhead inside tight download loops.)
/// </summary>
internal sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}
