using SeaRaider.Core.Catalog;
using SeaRaider.Core.Installing;
using SeaRaider.Core.Models;

namespace SeaRaider.Core.Tests;

public class DisplayTextAndSetupHelperTests
{
    // ---- titles ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData("God of War III Remastered - v1.x.x.x", "God of War III Remastered")]
    [InlineData("God of War III Remastered – v1.0.1 (shadPS4 v0.9) + 2 DLCs [FitGirl Repack]", "God of War III Remastered")]
    [InlineData("Hades II – Build 18923104 [FitGirl Repack]", "Hades II")]
    [InlineData("ELDEN RING: Deluxe Edition - v1.10 + 4 DLCs", "ELDEN RING: Deluxe Edition")]
    [InlineData("Cyberpunk 2077: Ultimate Edition (v2.12 + All DLCs + Bonus Content, MULTi19) [FitGirl Repack]", "Cyberpunk 2077: Ultimate Edition")]
    [InlineData("V Rising - v1.0", "V Rising")]
    public void Titles_lose_their_release_notes(string raw, string expected) => Assert.Equal(expected, DisplayText.Title(raw));

    [Theory]
    [InlineData("Mario + Rabbids Sparks of Hope")]
    [InlineData("Grand Theft Auto V")]
    [InlineData("Alan Wake II: Night Springs")]
    public void Real_names_are_left_alone(string title) => Assert.Equal(title, DisplayText.Title(title));

    // ---- descriptions -----------------------------------------------------------------------------------

    [Fact]
    public void Descriptions_lose_the_info_dump()
    {
        const string raw = "Genres/Tags: Action, Adventure\nCompanies: Santa Monica Studio\nLanguages: ENG/MULTi12\n" +
                           "Original Size: 40 GB\nRepack Size: 20 GB\n\n" +
                           "Kratos is back, and this time he fights the gods of Olympus. Read more »";
        Assert.Equal("Kratos is back, and this time he fights the gods of Olympus.", DisplayText.Description(raw));
    }

    [Fact]
    public void A_cut_off_teaser_ends_with_an_ellipsis()
    {
        var text = DisplayText.Description("A long story about a sailor who keeps going and going and then it just stops mid sente");
        Assert.EndsWith("…", text);
    }

    // ---- setup helper ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(@"C:\Games\Game\_Redist\dxwebsetup.exe", "DirectX", "/Q")]
    [InlineData(@"C:\Games\Game\_Redist\DirectX\DXSETUP.exe", "DirectX", "/silent")]
    [InlineData(@"C:\Users\me\AppData\Local\Temp\is-AB12C.tmp\vcredist_x64.exe", "Visual C++ runtime", "/q /norestart")]
    [InlineData(@"C:\Games\Game\_CommonRedist\dotNET\windowsdesktop-runtime-8.0.1-win-x64.exe", ".NET runtime", "/install /quiet /norestart")]
    [InlineData(@"C:\Games\Game\_Redist\oalinst.exe", "OpenAL", "/s")]
    public void Runtime_installers_are_recognised_with_their_quiet_switches(string path, string name, string arguments)
    {
        var runtime = RedistRules.Classify(path);
        Assert.NotNull(runtime);
        Assert.Equal(name, runtime.Name);
        Assert.Equal(arguments, runtime.SilentArguments);
    }

    [Fact]
    public void Unknown_installers_in_a_redist_folder_are_skipped_not_run()
    {
        var runtime = RedistRules.Classify(@"C:\Games\Game\_CommonRedist\Foo\setup_foo.exe");
        Assert.NotNull(runtime);
        Assert.Null(runtime.SilentArguments);
    }

    [Theory]
    [InlineData(@"C:\Games\Game\Game.exe")]
    [InlineData(@"C:\Games\Game\_Redist\unins000.exe")]
    [InlineData(@"C:\Users\me\AppData\Local\Temp\is-AB12C.tmp\setup.tmp")]
    public void The_game_and_the_setup_are_not_runtimes(string path) => Assert.Null(RedistRules.Classify(path));

    [Theory]
    [InlineData("msedge.exe", true)]
    [InlineData(@"C:\Program Files\Google\Chrome\Application\CHROME.EXE", true)]
    [InlineData("firefox.exe", true)]
    [InlineData("setup.tmp", false)]
    [InlineData("unarc.exe", false)]
    public void Browsers_are_recognised(string name, bool expected) => Assert.Equal(expected, RedistRules.IsBrowser(name));

    [Theory]
    [InlineData("Limit installer to 2 GB of RAM usage", true)]
    [InlineData("&Limit installer to 2 GB of RAM usage", true)]
    [InlineData("Low RAM mode (slower)", true)]
    [InlineData("Reduce memory usage", true)]
    [InlineData("Use less RAM", true)]
    [InlineData("Verify files after installation", false)]
    [InlineData("Create a desktop shortcut", false)]
    [InlineData("Install DirectX", false)]
    [InlineData("RAM", false)]
    [InlineData("", false)]
    public void Low_ram_options_are_recognised(string label, bool expected) =>
        Assert.Equal(expected, SetupOptionTweaker.IsLowRamOption(label));

    [Fact]
    public void Helper_request_survives_the_command_line()
    {
        var request = new SetupRunnerRequest(@"C:\Setup\setup.exe", "/SILENT /DIR=\"C:\\Games\\My Game\"", @"C:\Setup", @"C:\Games\My Game",
            SetupRuntimes.Skip, BlockWebPages: true, MuteAudio: true, @"C:\Temp\log.txt");
        var argument = request.Encode();
        Assert.DoesNotContain(' ', argument);
        Assert.DoesNotContain('"', argument);
        Assert.Equal(request, SetupRunnerRequest.Decode(argument));
        Assert.Null(SetupRunnerRequest.Decode("not a request!"));
    }

    [Fact]
    public void Reset_to_defaults_keeps_folders_and_hidden_games()
    {
        var settings = new AppSettings { LibraryRoot = @"D:\Games", MaxConcurrentJobs = 9, MuteSetupAudio = false, IgdbClientId = "id" };
        settings.HiddenStoreItems.Add(new HiddenStoreItem { Key = "k", Title = "Some news post" });
        var reset = settings.WithDefaults();
        Assert.Equal(@"D:\Games", reset.LibraryRoot);
        Assert.Equal(new AppSettings().MaxConcurrentJobs, reset.MaxConcurrentJobs);
        Assert.True(reset.MuteSetupAudio);
        Assert.Equal("id", reset.IgdbClientId);
        Assert.Single(reset.HiddenStoreItems);
    }

    [Fact]
    public void Reset_to_defaults_does_not_bring_the_welcome_questions_back()
    {
        Assert.Equal(FirstRunState.Unknown, new AppSettings().FirstRun);
        var settings = new AppSettings { FirstRun = FirstRunState.Completed };
        Assert.Equal(FirstRunState.Completed, settings.WithDefaults().FirstRun);
    }

    [Fact]
    public void Setup_files_are_asked_about_by_default()
    {
        var settings = new AppSettings();
        Assert.Equal(SetupFilesMode.Ask, settings.SetupFilesAfterInstall);
        Assert.Equal(SetupRuntimes.InstallQuietly, settings.SetupRuntimes);
        Assert.True(settings.BlockSetupWebPages);
        Assert.True(settings.UncheckSetupLowRam);
        Assert.True(settings.MuteSetupAudio);
    }
}
