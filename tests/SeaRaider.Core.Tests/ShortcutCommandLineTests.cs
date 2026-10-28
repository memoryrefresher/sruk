using SeaRaider.Core.Launching;

namespace SeaRaider.Core.Tests;

/// <summary>Desktop shortcuts routed through SeaRaider keep the game's own program and arguments.</summary>
public class ShortcutCommandLineTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("-windowed -lang \"en us\"")]
    public void A_shortcut_command_line_round_trips(string? originalArgs)
    {
        var text = ShortcutCommandLine.Build("abc123", @"D:\Games\Space Demo\bin\SpaceDemo.exe", originalArgs);
        var parsed = ShortcutCommandLine.Parse(ShortcutCommandLine.Split(text));

        Assert.NotNull(parsed);
        Assert.Equal("abc123", parsed!.GameId);
        Assert.Equal(@"D:\Games\Space Demo\bin\SpaceDemo.exe", parsed.Exe);
        Assert.Equal(string.IsNullOrEmpty(originalArgs) ? null : originalArgs, parsed.Args);
    }

    [Fact]
    public void A_plain_start_has_no_launch_request()
    {
        Assert.Null(ShortcutCommandLine.Parse([]));
        Assert.Null(ShortcutCommandLine.Parse(["--updated"]));
    }
}
