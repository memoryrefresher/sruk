using Microsoft.Extensions.Logging;
using SeaRaider.Core.Abstractions;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;
using SharpCompress.Readers.Rar;

namespace SeaRaider.Core.Extraction;

/// <summary>
/// SharpCompress-based extractor for ZIP, RAR (incl. RAR5), 7z, TAR(+gz/bz2/xz) and every
/// split-archive layout recognised by <see cref="ArchiveSetResolver"/>.
///
/// <para>Safety: every entry path is validated against "zip-slip" traversal before writing.</para>
/// <para>Ergonomics: one level of nested archives is unpacked automatically, and a single wrapper
/// folder (Game-v1.0\…) is flattened so the install root contains the game files directly.</para>
///
/// SharpCompress is a synchronous library, so the whole extraction runs on a thread-pool thread
/// (never the UI thread) and checks the cancellation token between buffers.
/// </summary>
public sealed class ArchiveExtractor(ILogger<ArchiveExtractor> logger) : IArchiveExtractor
{
    private const int BufferSize = 1 << 20;

    public Task ExtractAsync(IReadOnlyList<string> files, string destinationDirectory, IProgress<double>? progress,
        CancellationToken cancellationToken) =>
        Task.Run(() => ExtractCore(files, destinationDirectory, progress, cancellationToken), cancellationToken);

    private void ExtractCore(IReadOnlyList<string> files, string destination, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(destination);
        var sets = ArchiveSetResolver.Resolve(files);
        if (sets.Count == 0) throw new InvalidDataException("Nothing to install — the download produced no files.");

        var commonRoot = CommonDirectory(files);
        var weights = sets.Select(s => Math.Max(1, s.TotalBytes)).ToArray();
        var totalWeight = (double)weights.Sum();
        var completedWeight = 0d;

        for (var i = 0; i < sets.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var set = sets[i];
            var baseline = completedWeight;
            var weight = weights[i];
            void SetFraction(double fraction) => progress?.Report(Math.Clamp((baseline + weight * fraction) / totalWeight * 100, 0, 100));

            logger.LogInformation("Extracting {Kind} set: {First} (+{More} files)", set.Kind, Path.GetFileName(set.Files[0]), set.Files.Count - 1);

            switch (set.Kind)
            {
                case ArchiveSetKind.Passthrough:
                    MovePreservingLayout(set.Files[0], commonRoot, destination);
                    break;
                case ArchiveSetKind.ByteSplit:
                    using (var joined = new ConcatenatedReadStream(set.Files))
                    using (var archive = ArchiveFactory.OpenArchive(joined))
                        ExtractArchive(archive, destination, SetFraction, ct);
                    break;
                case ArchiveSetKind.MultiVolume:
                    using (var archive = ArchiveFactory.OpenArchive(set.Files.Select(f => new FileInfo(f))))
                        ExtractArchive(archive, destination, SetFraction, ct);
                    break;
                default:
                    ExtractSingle(set.Files[0], destination, SetFraction, ct);
                    break;
            }

            completedWeight += weight;
            SetFraction(1);
        }

        ExtractNestedArchives(destination, ct);
        FlattenSingleWrapperFolder(destination);
        progress?.Report(100);
    }

    // ---- low disk space mode ----------------------------------------------------------------------

    public Task ExtractVolumesAsync(VolumeFeed feed, int startVolume, string destinationDirectory, StreamingCallbacks callbacks,
        CancellationToken cancellationToken) =>
        Task.Run(() => ExtractVolumesCore(feed, startVolume, destinationDirectory, callbacks, cancellationToken), cancellationToken);

    /// <summary>
    /// Reads the volumes in order with SharpCompress's multi-volume RAR reader, which asks for the next volume only when
    /// it needs it. That request is the moment the previous volume is fully read, so it is closed and deleted right
    /// there: the disk holds the unpacked files, the volume being read, and whatever the downloader is ahead.
    /// </summary>
    private void ExtractVolumesCore(VolumeFeed feed, int startVolume, string destination, StreamingCallbacks callbacks, CancellationToken ct)
    {
        Directory.CreateDirectory(destination);
        var current = startVolume - 1;
        FileStream? open = null;
        string? openPath = null;

        void ReleaseOpen()
        {
            if (open is null) return;
            open.Dispose();
            open = null;
            TryDelete(openPath);
        }

        IEnumerable<Stream> Volumes()
        {
            for (var i = startVolume; i < feed.Count; i++)
            {
                var path = feed.WaitFor(i, ct);
                ReleaseOpen(); // the reader only asks for volume i once it has read everything in volume i-1
                open = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, BufferSize,
                    FileOptions.SequentialScan);
                openPath = path;
                current = i;
                logger.LogInformation("Unpacking volume {Index} of {Count}: {File}", i + 1, feed.Count, Path.GetFileName(path));
                yield return open;
            }
        }

        try
        {
            using var reader = RarReader.OpenReader(Volumes(), new ReaderOptions { LeaveStreamOpen = true });
            var first = true;
            while (reader.MoveToNextEntry())
            {
                ct.ThrowIfCancellationRequested();
                var entry = reader.Entry;
                if (first)
                {
                    callbacks.Solid?.Invoke(entry.IsSolid);
                    first = false;
                }

                callbacks.ResumePoint?.Invoke(current);
                callbacks.Progress?.Invoke(Math.Clamp(100.0 * Math.Max(0, current) / feed.Count, 0, 99));
                if (entry.IsDirectory) continue;

                // Resumed run: files finished before the interruption are already here.
                var target = PathSafety.ResolveEntryPath(destination, entry.Key ?? string.Empty);
                if (target is not null && entry.Size > 0 && File.Exists(target) && new FileInfo(target).Length == entry.Size) continue;

                WriteEntry(entry, reader.OpenEntryStream, destination, _ => { }, ct);
            }
        }
        finally
        {
            open?.Dispose();
        }

        TryDelete(openPath);
        ExtractNestedArchives(destination, ct);
        FlattenSingleWrapperFolder(destination);
        callbacks.Progress?.Invoke(100);
    }

    private void TryDelete(string? path)
    {
        if (path is null) return;
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogInformation(ex, "Couldn't delete unpacked volume {File}; it goes with the temporary folder", path);
        }
    }

    // ---- opening --------------------------------------------------------------------------------

    private static bool IsCompressedTar(string path) =>
        path.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".tar.bz2", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".tbz2", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".tar.xz", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".txz", StringComparison.OrdinalIgnoreCase);

    private void ExtractSingle(string path, string destination, Action<double> onFraction, CancellationToken ct)
    {
        if (IsCompressedTar(path))
        {
            // Forward-only reader transparently handles tar wrapped in gzip/bzip2/xz.
            using var file = File.OpenRead(path);
            using var reader = ReaderFactory.OpenReader(file);
            while (reader.MoveToNextEntry())
            {
                ct.ThrowIfCancellationRequested();
                if (reader.Entry.IsDirectory) continue;
                WriteEntry(reader.Entry, reader.OpenEntryStream, destination, _ => { }, ct);
                onFraction(file.Length == 0 ? 1 : (double)file.Position / file.Length);
            }
            return;
        }

        using var archive = ArchiveFactory.OpenArchive(new FileInfo(path));
        ExtractArchive(archive, destination, onFraction, ct);
    }

    private void ExtractArchive(IArchive archive, string destination, Action<double> onFraction, CancellationToken ct)
    {
        // A multi-part archive with a part missing (only part 1 downloaded, say) must not be "installed" from the files
        // that happen to be complete.
        if (!archive.IsComplete)
            throw new InvalidDataException(
                "Some parts of this game are missing: the archive continues in a part that wasn't downloaded. " +
                "Remove this download and start it again from the game page (SeaRaider then plans every part), or pick " +
                "another source under Download options.");

        var total = Math.Max(1, archive.TotalUncompressedSize);
        long written = 0;
        void OnBytes(long n)
        {
            written += n;
            onFraction(Math.Min(1, (double)written / total));
        }

        if (archive.IsSolid || archive.Type == ArchiveType.SevenZip)
        {
            // Solid archives must be decompressed sequentially; random entry access would
            // re-decompress the stream from the start for every file.
            using var reader = archive.ExtractAllEntries();
            while (reader.MoveToNextEntry())
            {
                ct.ThrowIfCancellationRequested();
                if (reader.Entry.IsDirectory) continue;
                WriteEntry(reader.Entry, reader.OpenEntryStream, destination, OnBytes, ct);
            }
            return;
        }

        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.IsDirectory) continue;
            WriteEntry(entry, entry.OpenEntryStream, destination, OnBytes, ct);
        }
    }

    // ---- writing --------------------------------------------------------------------------------

    private void WriteEntry(IEntry entry, Func<Stream> open, string destination, Action<long> onBytes, CancellationToken ct)
    {
        if (entry.IsEncrypted)
            throw new InvalidDataException("This archive is password-protected. SeaRaider can't extract it automatically.");

        var target = PathSafety.ResolveEntryPath(destination, entry.Key ?? string.Empty);
        if (target is null)
        {
            logger.LogWarning("Skipped unsafe archive entry {Key}", entry.Key);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        using (var source = open())
        using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize))
        {
            var buffer = new byte[BufferSize];
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                output.Write(buffer, 0, read);
                onBytes(read);
            }
        }

        if (entry.LastModifiedTime is { } modified)
        {
            try { File.SetLastWriteTime(target, modified); }
            catch (IOException) { /* cosmetic */ }
        }
    }

    private static void MovePreservingLayout(string file, string commonRoot, string destination)
    {
        var relative = Path.GetRelativePath(commonRoot, file);
        var target = PathSafety.ResolveEntryPath(destination, relative) ?? Path.Combine(destination, Path.GetFileName(file));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Move(file, target, overwrite: true);
    }

    // ---- post-processing ------------------------------------------------------------------------

    /// <summary>
    /// Many releases wrap the real archive in an outer one (outer.zip → game.rar). If the extracted
    /// output is nothing but archives (plus readme/nfo clutter), unpack those too — one level only.
    /// </summary>
    private void ExtractNestedArchives(string destination, CancellationToken ct)
    {
        var produced = Directory.GetFiles(destination, "*", SearchOption.AllDirectories);
        var archives = produced.Where(ArchiveSetResolver.IsArchiveFile).ToList();
        var meaningfulOthers = produced.Except(archives).Count(f =>
            !f.EndsWith(".nfo", StringComparison.OrdinalIgnoreCase) && !f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) &&
            !f.EndsWith(".url", StringComparison.OrdinalIgnoreCase) && !f.EndsWith(".sfv", StringComparison.OrdinalIgnoreCase));

        if (archives.Count == 0 || meaningfulOthers > 0) return;

        logger.LogInformation("Found {Count} nested archive file(s); extracting", archives.Count);
        foreach (var set in ArchiveSetResolver.Resolve(archives))
        {
            ct.ThrowIfCancellationRequested();
            switch (set.Kind)
            {
                case ArchiveSetKind.ByteSplit:
                    using (var joined = new ConcatenatedReadStream(set.Files))
                    using (var archive = ArchiveFactory.OpenArchive(joined))
                        ExtractArchive(archive, destination, _ => { }, ct);
                    break;
                case ArchiveSetKind.MultiVolume:
                    using (var archive = ArchiveFactory.OpenArchive(set.Files.Select(f => new FileInfo(f))))
                        ExtractArchive(archive, destination, _ => { }, ct);
                    break;
                case ArchiveSetKind.Single:
                    ExtractSingle(set.Files[0], destination, _ => { }, ct);
                    break;
            }
            foreach (var f in set.Files) File.Delete(f);
        }
    }

    /// <summary>Destination\Game-v1.0\{files} → Destination\{files} when the wrapper is the only entry.</summary>
    private static void FlattenSingleWrapperFolder(string destination)
    {
        var dirs = Directory.GetDirectories(destination);
        if (dirs.Length != 1 || Directory.GetFiles(destination).Length != 0) return;

        // Rename first so a child with the same name as the wrapper (Game\Game\…) can't collide.
        var wrapper = Path.Combine(destination, ".flatten-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.Move(dirs[0], wrapper);

        foreach (var entry in Directory.EnumerateFileSystemEntries(wrapper).ToList())
        {
            var target = Path.Combine(destination, Path.GetFileName(entry));
            if (Directory.Exists(entry)) Directory.Move(entry, target);
            else File.Move(entry, target);
        }
        Directory.Delete(wrapper, recursive: false);
    }

    private static string CommonDirectory(IReadOnlyList<string> files)
    {
        var dirs = files.Select(f => Path.GetDirectoryName(Path.GetFullPath(f))!).ToList();
        if (dirs.Count == 0) return string.Empty;

        var common = dirs[0];
        foreach (var dir in dirs.Skip(1))
        {
            while (!PathSafety.IsUnder(dir, common) && !string.Equals(dir, common, StringComparison.OrdinalIgnoreCase))
            {
                common = Path.GetDirectoryName(common) ?? common;
                if (Path.GetPathRoot(common) == common) break;
            }
        }
        return common;
    }
}
