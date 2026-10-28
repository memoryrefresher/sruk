using SeaRaider.Core.Extraction;
using SeaRaider.Core.Installing;
using SeaRaider.Core.Launching;
using SeaRaider.Core.Models;

namespace SeaRaider.Core.Tests;

/// <summary>Tests that need real files; each uses its own temp folder.</summary>
public sealed class FileSystemTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "searaider-tests-" + Guid.NewGuid().ToString("N"));

    public FileSystemTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string Touch(string relative, int bytes = 1)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    [Fact]
    public void Resolver_orders_split_zip_with_terminal_zip_last()
    {
        var files = new[] { Touch("game.zip"), Touch("game.z02"), Touch("game.z01"), Touch("readme.nfo") };

        var sets = ArchiveSetResolver.Resolve(files);

        var multi = Assert.Single(sets, s => s.Kind == ArchiveSetKind.MultiVolume);
        Assert.Equal(new[] { "game.z01", "game.z02", "game.zip" }, multi.Files.Select(Path.GetFileName));
        Assert.Contains(sets, s => s.Kind == ArchiveSetKind.Passthrough);
    }

    [Fact]
    public void Resolver_groups_byte_split_parts()
    {
        var files = new[] { Touch("game.7z.002"), Touch("game.7z.001"), Touch("game.7z.003") };
        var set = Assert.Single(ArchiveSetResolver.Resolve(files));
        Assert.Equal(ArchiveSetKind.ByteSplit, set.Kind);
        Assert.Equal("game.7z.001", Path.GetFileName(set.Files[0]));
    }

    [Fact]
    public void Concatenated_stream_reads_across_parts_and_seeks()
    {
        var a = Path.Combine(_dir, "x.001");
        var b = Path.Combine(_dir, "x.002");
        File.WriteAllBytes(a, [1, 2, 3]);
        File.WriteAllBytes(b, [4, 5]);

        using var stream = new ConcatenatedReadStream([a, b]);
        Assert.Equal(5, stream.Length);

        var buffer = new byte[5];
        Assert.Equal(5, stream.Read(buffer, 0, 5));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, buffer);

        stream.Seek(2, SeekOrigin.Begin);
        Assert.Equal(3, stream.ReadByte());
        Assert.Equal(4, stream.ReadByte());
    }

    [Fact]
    public void Locator_picks_the_game_over_redists_and_uninstallers()
    {
        Touch("unins000.exe", 200_000);
        Touch("_CommonRedist/vcredist/vc_redist.x64.exe", 20_000_000);
        Touch("Tools/CrashReporter.exe", 3_000_000);
        Touch("Launcher.exe", 400_000);
        var game = Touch("HollowDepths.exe", 60_000_000);

        var best = new ExecutableLocator().FindPrimaryExecutable(_dir, "Hollow Depths");

        Assert.Equal(game, best);
    }

    [Fact]
    public void LaunchFiles_resolves_a_bat_by_name_or_relative_path()
    {
        var shallow = Touch("start.bat", 100);
        var deep = Touch("bin/start.bat", 100);
        var exe = Touch("bin/Game.exe", 5_000_000);

        Assert.Equal(shallow, LaunchFiles.Resolve(_dir, "start.bat"));
        Assert.Equal(deep, LaunchFiles.Resolve(_dir, "bin/start.bat"));
        Assert.Equal(exe, LaunchFiles.Resolve(_dir, "Game")); // no extension: .exe/.bat/.cmd are tried
        Assert.Equal(exe, LaunchFiles.Resolve(_dir, "game.exe")); // case-insensitive, found in a subfolder
    }

    [Fact]
    public void LaunchFiles_rejects_missing_unsafe_and_non_launchable_hints()
    {
        Touch("readme.txt", 10);

        Assert.Null(LaunchFiles.Resolve(_dir, "nothing.bat"));
        Assert.Null(LaunchFiles.Resolve(_dir, "readme.txt"));
        Assert.Null(LaunchFiles.Resolve(_dir, "../outside.bat"));
        Assert.Null(LaunchFiles.Resolve(_dir, null));
        Assert.True(LaunchFiles.IsLaunchable(@"C:\Games\X\run.CMD"));
        Assert.False(LaunchFiles.IsLaunchable(@"C:\Games\X\run.ps1"));
    }

    [Fact]
    public void Locator_never_returns_a_setup_program()
    {
        Touch("setup.exe", 1_000_000);
        Assert.Null(new ExecutableLocator().FindPrimaryExecutable(_dir, "Anything"));
    }

    [Fact]
    public void Detector_flags_setup_only_folders_as_installers()
    {
        var setup = Touch("setup_hollow_depths_1.2_(64bit).exe", 3_000_000);
        Touch("setup_hollow_depths_1.2_(64bit)-1.bin", 10);
        Touch("_CommonRedist/vcredist/vc_redist.x64.exe", 20_000_000);

        var result = new InstallerDetector(new ExecutableLocator()).Inspect(_dir, "Hollow Depths");

        Assert.True(result.IsInstaller);
        Assert.Equal(setup, result.InstallerPath);
    }

    [Fact]
    public void Detector_treats_folder_with_real_game_as_playable_even_with_a_setup_tool()
    {
        var game = Touch("HollowDepths.exe", 60_000_000);
        Touch("Setup.exe", 400_000); // old-style configuration tool

        var result = new InstallerDetector(new ExecutableLocator()).Inspect(_dir, "Hollow Depths");

        Assert.False(result.IsInstaller);
        Assert.Equal(game, result.GameExecutable);
    }

    [Fact]
    public void Detector_recognises_inno_setup_by_its_signature()
    {
        var path = Path.Combine(_dir, "setup.exe");
        File.WriteAllBytes(path, [.. new byte[64], .. System.Text.Encoding.ASCII.GetBytes("Inno Setup Setup Data (6.2.0)"), .. new byte[64]]);
        Assert.Equal(InstallerKind.InnoSetup, InstallerDetector.DetectKind(path));
    }

    [Theory]
    [InlineData("../evil.dll")]
    [InlineData("..\\..\\evil.dll")]
    [InlineData("C:\\Windows\\evil.dll")]
    public void Zip_slip_entries_are_rejected(string entry) =>
        Assert.Null(PathSafety.ResolveEntryPath(_dir, entry));

    [Fact]
    public void Folder_names_are_sanitised() =>
        Assert.Equal("Hollow Depths - Remastered", PathSafety.ToFolderName("Hollow Depths: Remastered?"));
}
