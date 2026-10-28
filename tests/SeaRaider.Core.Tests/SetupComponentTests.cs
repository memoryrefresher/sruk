using SeaRaider.Core.Installing;

namespace SeaRaider.Core.Tests;

public class SetupComponentTests
{
    [Theory]
    [InlineData("Download and Install C++ Redist 2008 SP1 x86", true)]
    [InlineData("Download and Install C++ Redist 2010 SP1 x64", true)]
    [InlineData("Update DirectX", true)]
    [InlineData("Download and Install Microsoft .NET Framework 3.5 Client Profile", true)]
    [InlineData("Install Visual C++ 2015-2022 Redistributable", true)]
    [InlineData("Verify files integrity after install", false)]
    [InlineData("Portuguese-Brazil", false)]
    [InlineData("Bonus soundtrack", false)]
    [InlineData("HD texture pack", false)]
    public void Runtime_components_are_recognised(string label, bool runtime) =>
        Assert.Equal(runtime, SetupComponentTweaker.IsRuntimeComponent(label));
}
