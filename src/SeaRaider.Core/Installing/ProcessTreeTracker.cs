using System.Runtime.InteropServices;
using SeaRaider.Core.Installing.Native;
using SeaRaider.Core.Launching;

namespace SeaRaider.Core.Installing;

/// <summary>One look at a setup's process tree.</summary>
/// <param name="AliveProcessIds">Processes that belong to the setup and are still running.</param>
/// <param name="CpuDelta">CPU time they used since the previous sample.</param>
/// <param name="IoDelta">Bytes they read and wrote since the previous sample.</param>
public sealed record ProcessTreeSample(IReadOnlySet<int> AliveProcessIds, TimeSpan CpuDelta, long IoDelta);

/// <summary>
/// Follows a setup program and everything it starts. Inno Setup's loader runs the real setup as
/// <c>%TEMP%\is-XXXX.tmp\setup.tmp</c>, repacks run unarc/ISDone helpers, bootstrappers start an .msi —
/// none of which live in the setup folder, so children are found through parent process ids
/// (CreateToolhelp32Snapshot). Processes started from the setup folder count too (handover via the shell),
/// and for .msi installs the msiexec processes, because Windows Installer does its work in its own service.
/// CPU and I/O use PROCESS_QUERY_LIMITED_INFORMATION, which also works on elevated setups.
/// </summary>
public sealed class ProcessTreeTracker
{
    private readonly long _rootStartFileTime;
    private readonly string? _setupDirectory;
    private readonly bool _includeMsiexec;
    private readonly HashSet<int> _tracked = [];
    private readonly Dictionary<int, (long Cpu100ns, ulong Io)> _last = [];
    private int _samplesTaken;

    /// <param name="rootProcessId">The setup process SeaRaider started.</param>
    /// <param name="rootStartUtc">Its start time; older processes with a reused parent id are ignored.</param>
    /// <param name="setupDirectory">Folder holding the setup files.</param>
    /// <param name="includeMsiexec">Count every msiexec.exe (Windows Installer packages).</param>
    public ProcessTreeTracker(int rootProcessId, DateTime rootStartUtc, string? setupDirectory, bool includeMsiexec)
    {
        _rootStartFileTime = rootStartUtc.AddSeconds(-2).ToFileTimeUtc();
        _setupDirectory = setupDirectory;
        _includeMsiexec = includeMsiexec;
        _tracked.Add(rootProcessId);
    }

    public IReadOnlySet<int> Tracked => _tracked;

    public ProcessTreeSample Sample()
    {
        if (!OperatingSystem.IsWindows()) return new ProcessTreeSample(new HashSet<int>(), TimeSpan.Zero, 0);

        var sampleNumber = ++_samplesTaken;
        var all = Snapshot();
        var alive = new HashSet<int>(all.Select(p => p.Id));

        // Adopt children of tracked processes until nothing new turns up (grandchildren included).
        bool added;
        do
        {
            added = false;
            foreach (var (id, parentId, name) in all)
            {
                if (_tracked.Contains(id) || !_tracked.Contains(parentId)) continue;
                if (!StartedAfterRoot(id)) continue; // parent id reused by an unrelated, older process
                _tracked.Add(id);
                added = true;
            }
        } while (added);

        foreach (var (id, _, name) in all)
        {
            if (_tracked.Contains(id)) continue;
            if (_includeMsiexec && name.Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase) && StartedAfterRoot(id))
                _tracked.Add(id);
        }

        // Image paths cost one OpenProcess per process: check the setup folder every few samples only.
        if (_setupDirectory is not null && sampleNumber % 3 == 1)
            foreach (var id in ProcessScanner.ProcessIdsRunningFrom(_setupDirectory)) _tracked.Add(id);

        _tracked.RemoveWhere(id => !alive.Contains(id));
        foreach (var gone in _last.Keys.Where(id => !_tracked.Contains(id)).ToList()) _last.Remove(gone);

        long cpuDelta = 0;
        long ioDelta = 0;
        foreach (var id in _tracked)
        {
            if (!TryReadCounters(id, out var cpu, out var io)) continue;
            if (_last.TryGetValue(id, out var previous))
            {
                cpuDelta += Math.Max(0, cpu - previous.Cpu100ns);
                ioDelta += (long)(io >= previous.Io ? io - previous.Io : 0);
            }
            else if (sampleNumber > 1)
            {
                // A process that appeared since the last sample: everything it did so far is fresh activity.
                // (On the first sample everything is the baseline.)
                cpuDelta += cpu;
                ioDelta += (long)io;
            }
            _last[id] = (cpu, io);
        }

        return new ProcessTreeSample(new HashSet<int>(_tracked), TimeSpan.FromTicks(cpuDelta), ioDelta);
    }

    private bool StartedAfterRoot(int processId) =>
        TryGetCreationTime(processId) is not { } created || created >= _rootStartFileTime;

    internal static List<(int Id, int ParentId, string Name)> Snapshot()
    {
        var result = new List<(int, int, string)>();
        var snapshot = SetupProcessNative.CreateToolhelp32Snapshot(SetupProcessNative.Th32csSnapProcess, 0);
        if (snapshot == SetupProcessNative.InvalidHandleValue) return result;
        try
        {
            var entry = new SetupProcessNative.ProcessEntry32 { dwSize = (uint)Marshal.SizeOf<SetupProcessNative.ProcessEntry32>() };
            if (!SetupProcessNative.Process32First(snapshot, ref entry)) return result;
            do
            {
                result.Add(((int)entry.th32ProcessID, (int)entry.th32ParentProcessID, entry.szExeFile ?? string.Empty));
            } while (SetupProcessNative.Process32Next(snapshot, ref entry));
        }
        finally
        {
            SetupProcessNative.CloseHandle(snapshot);
        }
        return result;
    }

    private static long? TryGetCreationTime(int processId)
    {
        var handle = SetupProcessNative.OpenProcess(SetupProcessNative.ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero) return null;
        try
        {
            return SetupProcessNative.GetProcessTimes(handle, out var created, out _, out _, out _) ? created : null;
        }
        finally
        {
            SetupProcessNative.CloseHandle(handle);
        }
    }

    private static bool TryReadCounters(int processId, out long cpu100ns, out ulong ioBytes)
    {
        cpu100ns = 0;
        ioBytes = 0;
        var handle = SetupProcessNative.OpenProcess(SetupProcessNative.ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero) return false;
        try
        {
            if (SetupProcessNative.GetProcessTimes(handle, out _, out _, out var kernel, out var user)) cpu100ns = kernel + user;
            if (SetupProcessNative.GetProcessIoCounters(handle, out var counters))
                ioBytes = counters.ReadTransferCount + counters.WriteTransferCount;
            return true;
        }
        finally
        {
            SetupProcessNative.CloseHandle(handle);
        }
    }
}
