namespace SeaRaider.Core.Extraction;

/// <summary>
/// A read-only, seekable stream that presents several files as one contiguous stream.
/// Used for byte-split archives (game.7z.001, .002 …) so they can be opened directly
/// without first writing a joined multi-GB copy to disk.
/// </summary>
internal sealed class ConcatenatedReadStream : Stream
{
    private readonly FileStream[] _parts;
    private readonly long[] _offsets; // start offset of each part within the virtual stream
    private long _position;

    public ConcatenatedReadStream(IEnumerable<string> orderedPaths)
    {
        _parts = orderedPaths
            .Select(p => new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1 << 16))
            .ToArray();

        _offsets = new long[_parts.Length];
        long running = 0;
        for (var i = 0; i < _parts.Length; i++)
        {
            _offsets[i] = running;
            running += _parts[i].Length;
        }
        Length = running;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length { get; }

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var total = 0;
        while (buffer.Length > 0 && _position < Length)
        {
            var index = PartIndexAt(_position);
            var part = _parts[index];
            part.Position = _position - _offsets[index];

            var read = part.Read(buffer);
            if (read == 0) break; // file shrank underneath us; treat as EOF

            total += read;
            _position += read;
            buffer = buffer[read..];
        }
        return total;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        if (target < 0) throw new IOException("Attempted to seek before the start of the stream.");
        _position = target;
        return _position;
    }

    /// <summary>Binary search for the part containing <paramref name="position"/>.</summary>
    private int PartIndexAt(long position)
    {
        var index = Array.BinarySearch(_offsets, position);
        if (index < 0) index = ~index - 1;
        // Skip zero-length parts that share an offset with the next one.
        while (index < _parts.Length - 1 && position >= _offsets[index] + _parts[index].Length) index++;
        return index;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            foreach (var part in _parts) part.Dispose();
        base.Dispose(disposing);
    }
}
