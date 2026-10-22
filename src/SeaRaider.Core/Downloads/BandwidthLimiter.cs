namespace SeaRaider.Core.Downloads;

/// <summary>
/// Token-bucket rate limiter. Callers report bytes as they move them and get back how long to wait
/// so the average stays at the limit; a small burst (half a second's worth) smooths out chunky reads.
/// Thread-safe and shared by every download it governs. Limit 0 = unlimited.
/// </summary>
public sealed class BandwidthLimiter(TimeProvider? timeProvider = null)
{
    private const double MinimumBurstBytes = 64 * 1024;

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly object _lock = new();
    private long _bytesPerSecond;
    private double _tokens;
    private long _lastTimestamp;
    private bool _primed;

    /// <summary>Current limit in bytes per second (0 = unlimited).</summary>
    public long BytesPerSecond
    {
        get { lock (_lock) return _bytesPerSecond; }
    }

    public bool IsLimited => BytesPerSecond > 0;

    public void SetLimit(long bytesPerSecond)
    {
        lock (_lock)
        {
            _bytesPerSecond = Math.Max(0, bytesPerSecond);
            _tokens = Math.Min(_tokens, Capacity);
        }
    }

    private double Capacity => Math.Max(MinimumBurstBytes, _bytesPerSecond * 0.5);

    /// <summary>Takes <paramref name="bytes"/> from the bucket; returns how long the caller should pause.</summary>
    public TimeSpan Reserve(long bytes)
    {
        lock (_lock)
        {
            var rate = _bytesPerSecond;
            if (rate <= 0) return TimeSpan.Zero;

            var now = _time.GetTimestamp();
            if (!_primed)
            {
                _primed = true;
                _tokens = Capacity;
            }
            else
            {
                var elapsed = _time.GetElapsedTime(_lastTimestamp, now).TotalSeconds;
                _tokens = Math.Min(Capacity, _tokens + elapsed * rate);
            }
            _lastTimestamp = now;

            _tokens -= bytes;
            return _tokens >= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(-_tokens / rate);
        }
    }
}

/// <summary>
/// The limits that apply to one running download job: its own limit plus the shared global one.
/// Flows to the transports through an <see cref="AsyncLocal{T}"/> (like the verification context), so
/// <see cref="HttpResumableDownloader"/> and <see cref="MonoTorrentDownloader"/> pick it up without new parameters.
/// </summary>
public sealed class TransferThrottle
{
    private static readonly AsyncLocal<TransferThrottle?> CurrentThrottle = new();

    private readonly BandwidthLimiter _global;
    private readonly BandwidthLimiter _job;

    public TransferThrottle(BandwidthLimiter global, TimeProvider? timeProvider = null)
    {
        _global = global;
        _job = new BandwidthLimiter(timeProvider);
    }

    /// <summary>The throttle of the job running on this async flow, if any.</summary>
    public static TransferThrottle? Current => CurrentThrottle.Value;

    /// <summary>Makes <paramref name="throttle"/> current for the calling flow until disposed.</summary>
    public static IDisposable Use(TransferThrottle throttle)
    {
        var previous = CurrentThrottle.Value;
        CurrentThrottle.Value = throttle;
        return new Restore(previous);
    }

    /// <summary>The job's own download limit in bytes/s (0 = none).</summary>
    public int JobDownloadBytesPerSecond { get; private set; }

    /// <summary>The job's own upload limit in bytes/s (0 = none; torrents only).</summary>
    public int JobUploadBytesPerSecond { get; private set; }

    public void SetJobLimits(int downloadKBps, int uploadKBps)
    {
        JobDownloadBytesPerSecond = Math.Max(0, downloadKBps) * 1024;
        JobUploadBytesPerSecond = Math.Max(0, uploadKBps) * 1024;
        _job.SetLimit(JobDownloadBytesPerSecond);
    }

    /// <summary>Waits as long as needed after moving <paramref name="bytes"/> (HTTP downloads).</summary>
    public async ValueTask WaitAsync(int bytes, CancellationToken cancellationToken)
    {
        var jobDelay = _job.Reserve(bytes);
        var globalDelay = _global.Reserve(bytes);
        var delay = jobDelay > globalDelay ? jobDelay : globalDelay;
        if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Read size for a limited stream: about a quarter second of data, so progress stays smooth at low limits
    /// instead of one megabyte followed by a long pause.
    /// </summary>
    public int ChunkSize(int max)
    {
        var limits = new[] { _job.BytesPerSecond, _global.BytesPerSecond }.Where(l => l > 0).ToList();
        if (limits.Count == 0) return max;
        return (int)Math.Clamp(limits.Min() / 4, 16 * 1024, max);
    }

    private sealed class Restore(TransferThrottle? previous) : IDisposable
    {
        public void Dispose() => CurrentThrottle.Value = previous;
    }
}

/// <summary>
/// Async counting gate whose capacity can change at run time (the "extracting at once" setting).
/// Waiters are served in arrival order.
/// </summary>
public sealed class AdjustableSlots(int capacity)
{
    private readonly object _lock = new();
    private readonly LinkedList<TaskCompletionSource> _waiters = new();
    private int _capacity = Math.Max(1, capacity);
    private int _used;

    public int Capacity
    {
        get { lock (_lock) return _capacity; }
        set
        {
            lock (_lock)
            {
                _capacity = Math.Max(1, value);
                Pump();
            }
        }
    }

    public int InUse
    {
        get { lock (_lock) return _used; }
    }

    /// <summary>Waits for a slot; dispose the result to give it back.</summary>
    public async Task<IDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource waiter;
        LinkedListNode<TaskCompletionSource> node;
        lock (_lock)
        {
            if (_used < _capacity)
            {
                _used++;
                return new Slot(this);
            }
            waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            node = _waiters.AddLast(waiter);
        }

        await using (cancellationToken.Register(() =>
                     {
                         lock (_lock)
                         {
                             if (node.List is null) return; // already granted
                             _waiters.Remove(node);
                         }
                         waiter.TrySetCanceled(cancellationToken);
                     }).ConfigureAwait(false))
        {
            await waiter.Task.ConfigureAwait(false);
        }
        return new Slot(this);
    }

    private void Release()
    {
        lock (_lock)
        {
            _used--;
            Pump();
        }
    }

    private void Pump()
    {
        while (_used < _capacity && _waiters.First is { } next)
        {
            _waiters.RemoveFirst();
            _used++;
            next.Value.TrySetResult();
        }
    }

    private sealed class Slot(AdjustableSlots owner) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) owner.Release();
        }
    }
}
