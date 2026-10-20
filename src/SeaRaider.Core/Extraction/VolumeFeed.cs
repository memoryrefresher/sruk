namespace SeaRaider.Core.Extraction;

/// <summary>
/// Hand-over point between the downloader and the extractor in low disk space mode
/// (<see cref="Abstractions.IArchiveExtractor.ExtractVolumesAsync"/>): the downloader announces each finished
/// volume, the extractor waits for the next one it needs, and the downloader waits whenever it is
/// <see cref="AheadLimit"/> volumes ahead of the extractor, so finished-but-unpacked volumes never pile up on disk.
/// </summary>
public sealed class VolumeFeed
{
    private readonly object _lock = new();
    private readonly string?[] _paths;
    private Exception? _failure;
    private int _opened = -1;

    /// <param name="count">Volumes in the set.</param>
    /// <param name="aheadLimit">How many downloaded volumes may wait for the extractor.</param>
    public VolumeFeed(int count, int aheadLimit = 2)
    {
        if (count < 1) throw new ArgumentOutOfRangeException(nameof(count));
        _paths = new string?[count];
        AheadLimit = Math.Max(1, aheadLimit);
    }

    public int Count => _paths.Length;

    public int AheadLimit { get; }

    /// <summary>Highest volume the extractor has started reading (-1 before the first).</summary>
    public int OpenedIndex
    {
        get { lock (_lock) return _opened; }
    }

    /// <summary>Volumes downloaded but not yet opened by the extractor.</summary>
    public int Waiting
    {
        get
        {
            lock (_lock) return _paths.Skip(_opened + 1).Count(p => p is not null);
        }
    }

    /// <summary>Downloader: volume <paramref name="index"/> is complete at <paramref name="path"/>.</summary>
    public void Completed(int index, string path)
    {
        lock (_lock)
        {
            _paths[index] = path;
            Monitor.PulseAll(_lock);
        }
    }

    /// <summary>Downloader gave up: the extractor stops waiting and throws.</summary>
    public void Fail(Exception failure)
    {
        lock (_lock)
        {
            _failure ??= failure;
            Monitor.PulseAll(_lock);
        }
    }

    /// <summary>Extractor: blocks until volume <paramref name="index"/> is downloaded, then marks it opened.</summary>
    public string WaitFor(int index, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            while (_paths[index] is null)
            {
                if (_failure is not null) throw new OperationCanceledException("The download stopped.", _failure, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                Monitor.Wait(_lock, TimeSpan.FromMilliseconds(500));
            }
            _opened = Math.Max(_opened, index);
            Monitor.PulseAll(_lock);
            return _paths[index]!;
        }
    }

    /// <summary>Downloader: waits before fetching volume <paramref name="index"/> while the extractor is too far behind.</summary>
    public async Task WaitForRoomAsync(int index, CancellationToken cancellationToken)
    {
        while (true)
        {
            lock (_lock)
            {
                if (_failure is not null || index - _opened <= AheadLimit) return;
            }
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>True when the downloader is waiting for the extractor (for the status line).</summary>
    public bool IsAhead(int nextIndex)
    {
        lock (_lock) return nextIndex - _opened > AheadLimit;
    }
}
