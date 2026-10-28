using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SeaRaider.Core.Launching;

/// <summary>
/// Finds running processes by where their executable lives. Used to follow launcher stubs (game sessions)
/// and bootstrapping installers that hand over to a child process.
/// Image paths are read with QueryFullProcessImageName, which also works for elevated processes.
/// </summary>
public static class ProcessScanner
{
    /// <summary>True if any running process's executable is inside <paramref name="directory"/>.</summary>
    public static bool AnyRunningFrom(string directory)
    {
        if (string.IsNullOrEmpty(directory)) return false;

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                var path = TryGetImagePath(process);
                if (path is not null && PathSafety.IsUnder(path, directory)) return true;
            }
        }
        return false;
    }

    /// <summary>Ids of running processes whose executable is inside <paramref name="directory"/>.</summary>
    public static List<int> ProcessIdsRunningFrom(string directory)
    {
        var ids = new List<int>();
        if (string.IsNullOrEmpty(directory)) return ids;

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                var path = TryGetImagePath(process);
                if (path is not null && PathSafety.IsUnder(path, directory)) ids.Add(process.Id);
            }
        }
        return ids;
    }

    /// <summary>The program paths of every running process that can be read (one pass over the process list).</summary>
    public static List<string> RunningImagePaths()
    {
        var paths = new List<string>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (TryGetImagePath(process) is { } path) paths.Add(path);
            }
        }
        return paths;
    }

    /// <summary>Full path of a running process's program (also for elevated ones), or null.</summary>
    public static string? ImagePathOf(int processId)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static string? TryGetImagePath(Process process)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var handle = OpenProcess(ProcessQueryLimitedInformation, false, process.Id);
                if (handle == IntPtr.Zero) return null;
                try
                {
                    var buffer = new StringBuilder(1024);
                    var size = buffer.Capacity;
                    return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
                }
                finally
                {
                    CloseHandle(handle);
                }
            }
            return process.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return null; // exited, or protected system process
        }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder exeName, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
