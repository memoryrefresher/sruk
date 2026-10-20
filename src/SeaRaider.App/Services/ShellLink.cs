using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace SeaRaider.App.Services;

/// <summary>A Windows shortcut (.lnk), read and written through the shell's own IShellLink.</summary>
internal sealed class ShellLink : IDisposable
{
    private const int MaxPath = 1024;
    private const int StgmReadWrite = 2;

    private readonly IShellLinkW _link;
    private readonly string _path;

    private ShellLink(string path)
    {
        _path = path;
        _link = (IShellLinkW)new ShellLinkCoClass();
        ((IPersistFile)_link).Load(path, StgmReadWrite);
    }

    public static ShellLink Load(string path) => new(path);

    public string Target
    {
        get
        {
            var buffer = new StringBuilder(MaxPath);
            _link.GetPath(buffer, buffer.Capacity, IntPtr.Zero, 0);
            return buffer.ToString();
        }
        set => _link.SetPath(value);
    }

    public string Arguments
    {
        get
        {
            var buffer = new StringBuilder(MaxPath * 4);
            _link.GetArguments(buffer, buffer.Capacity);
            return buffer.ToString();
        }
        set => _link.SetArguments(value);
    }

    public string WorkingDirectory
    {
        get
        {
            var buffer = new StringBuilder(MaxPath);
            _link.GetWorkingDirectory(buffer, buffer.Capacity);
            return buffer.ToString();
        }
        set => _link.SetWorkingDirectory(value);
    }

    public (string Path, int Index) Icon
    {
        get
        {
            var buffer = new StringBuilder(MaxPath);
            _link.GetIconLocation(buffer, buffer.Capacity, out var index);
            return (buffer.ToString(), index);
        }
        set => _link.SetIconLocation(value.Path, value.Index);
    }

    public void Save() => ((IPersistFile)_link).Save(_path, true);

    public void Dispose() => Marshal.FinalReleaseComObject(_link);

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkCoClass { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
}
