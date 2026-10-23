using SeaRaider.Core.Updates;

namespace SeaRaider.Core.Tests;

/// <summary>Releases are only trusted when signed with the build's key and unchanged since.</summary>
public class UpdateTests
{
    private static UpdateManifest Release() => new()
    {
        Version = "0.11.0",
        Url = "https://github.com/you/SeaRaider/releases/download/v0.11.0/SeaRaider.exe",
        Sha256 = new string('a', 64),
        Size = 123_456,
        Notes = "Faster downloads",
    };

    [Fact]
    public void A_release_signed_with_the_key_verifies()
    {
        var (privateKey, publicKey) = UpdateSigning.CreateKeyPair();
        var release = Release();
        UpdateSigning.Sign(release, privateKey);

        Assert.True(UpdateSigning.Verify(release, publicKey));
        Assert.Equal(publicKey, UpdateSigning.PublicKeyOf(privateKey));
    }

    [Theory]
    [InlineData("url")]
    [InlineData("sha")]
    [InlineData("version")]
    [InlineData("size")]
    public void Changing_a_signed_release_breaks_its_signature(string field)
    {
        var (privateKey, publicKey) = UpdateSigning.CreateKeyPair();
        var release = Release();
        UpdateSigning.Sign(release, privateKey);

        switch (field)
        {
            case "url": release.Url = "https://evil.example/SeaRaider.exe"; break;
            case "sha": release.Sha256 = new string('b', 64); break;
            case "version": release.Version = "9.9.9"; break;
            case "size": release.Size++; break;
        }
        Assert.False(UpdateSigning.Verify(release, publicKey));
    }

    [Fact]
    public void Another_key_or_no_key_is_never_trusted()
    {
        var (privateKey, _) = UpdateSigning.CreateKeyPair();
        var (_, otherPublic) = UpdateSigning.CreateKeyPair();
        var release = Release();
        UpdateSigning.Sign(release, privateKey);

        Assert.False(UpdateSigning.Verify(release, otherPublic));
        Assert.False(UpdateSigning.Verify(release, null));
        Assert.False(UpdateSigning.Verify(release, "not base64!"));
    }

    [Theory]
    [InlineData("0.11.0", "0.11.0")]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2", "1.2.0")]
    [InlineData("1.2.3.4", "1.2.3")]
    public void Versions_compare_as_major_minor_build(string text, string expected) =>
        Assert.Equal(Version.Parse(expected), new UpdateManifest { Version = text }.ParsedVersion);
}
