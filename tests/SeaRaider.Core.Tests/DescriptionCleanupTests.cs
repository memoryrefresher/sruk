using SeaRaider.Core.Catalog;

namespace SeaRaider.Core.Tests;

/// <summary>Store descriptions keep the story and lose file lists and install steps.</summary>
public class DescriptionCleanupTests
{
    [Fact]
    public void Lines_with_file_names_are_dropped()
    {
        const string text = """
            A great game about pirates sailing the seven seas.
            Download Game.part01.rar (4.1 GB) and Game.part02.rar
            Run setup.exe, then copy fg-01.bin to the folder
            Game.7z.001
            Version v1.0.003 adds new maps and uses Node.js for its mods.
            """;

        var cleaned = DisplayText.Description(text);

        Assert.DoesNotContain(".rar", cleaned);
        Assert.DoesNotContain("setup.exe", cleaned);
        Assert.DoesNotContain(".7z", cleaned);
        Assert.Contains("A great game about pirates", cleaned);
        Assert.Contains("v1.0.003", cleaned);
        Assert.Contains("Node.js", cleaned);
    }
}
