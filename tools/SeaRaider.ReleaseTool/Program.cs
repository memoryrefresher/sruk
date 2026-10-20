using System.Diagnostics;
using System.Text.Json;
using SeaRaider.Core.Updates;

// SeaRaider release tool.
//   keygen [--out <folder>]
//       Makes the update signing key: searaider-update-key.pem (SECRET: never commit or share it) and prints the public
//       key to paste into Directory.Build.props <SeaRaiderUpdatePublicKey>.
//   sign --exe <SeaRaider.exe> --url <https address of that exe> --key <pem> [--notes "<text>"] [--version x.y.z] [--out latest.json]
//       Writes the signed release file. The version is read from the exe unless given.
//   verify --manifest <latest.json> --public-key <base64> [--exe <SeaRaider.exe>]
//       Checks a release file (and the exe) the way the app will.

return args.FirstOrDefault() switch
{
    "keygen" => KeyGen(),
    "sign" => await SignAsync(),
    "verify" => await VerifyAsync(),
    _ => Usage(),
};

int KeyGen()
{
    var folder = Option("--out") ?? Directory.GetCurrentDirectory();
    var path = Path.Combine(folder, "searaider-update-key.pem");
    if (File.Exists(path))
    {
        Console.Error.WriteLine($"{path} already exists. Keep using it (a new key would stop older builds from updating), or delete it first.");
        Console.WriteLine($"Its public key: {UpdateSigning.PublicKeyOf(File.ReadAllText(path))}");
        return 1;
    }
    var (privateKey, publicKey) = UpdateSigning.CreateKeyPair();
    Directory.CreateDirectory(folder);
    File.WriteAllText(path, privateKey);
    Console.WriteLine($"Private key written to {path}");
    Console.WriteLine("  Keep it secret and backed up. Anyone with it can push updates to your users; without it you can't.");
    Console.WriteLine();
    Console.WriteLine("Public key (paste into Directory.Build.props <SeaRaiderUpdatePublicKey>):");
    Console.WriteLine(publicKey);
    return 0;
}

async Task<int> SignAsync()
{
    var exe = Required("--exe");
    var url = Required("--url");
    var keyPath = Required("--key");
    if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return Fail("--url must be an https address.");

    var version = Option("--version") ?? VersionOf(exe);
    if (version is null) return Fail("Couldn't read the version from the exe; pass --version x.y.z.");

    var manifest = new UpdateManifest
    {
        Version = version,
        Url = url,
        Sha256 = await UpdateSigning.Sha256OfFileAsync(exe),
        Size = new FileInfo(exe).Length,
        Notes = Option("--notes"),
        Published = DateTime.UtcNow,
    };
    var privateKey = await File.ReadAllTextAsync(keyPath);
    UpdateSigning.Sign(manifest, privateKey);
    if (!UpdateSigning.Verify(manifest, UpdateSigning.PublicKeyOf(privateKey))) return Fail("Signing failed its own check.");

    var output = Option("--out") ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(exe))!, "latest.json");
    await File.WriteAllTextAsync(output, JsonSerializer.Serialize(manifest, UpdateManifest.JsonOptions));
    Console.WriteLine($"Signed {Path.GetFileName(exe)} {version} ({manifest.Size:N0} bytes) -> {output}");
    return 0;
}

async Task<int> VerifyAsync()
{
    var manifest = JsonSerializer.Deserialize<UpdateManifest>(await File.ReadAllTextAsync(Required("--manifest")), UpdateManifest.JsonOptions)!;
    var ok = UpdateSigning.Verify(manifest, Required("--public-key"));
    Console.WriteLine(ok ? $"Signature OK: version {manifest.Version}, {manifest.Url}" : "Signature does NOT match this public key.");
    if (Option("--exe") is { } exe)
    {
        var hash = await UpdateSigning.Sha256OfFileAsync(exe);
        var same = string.Equals(hash, manifest.Sha256, StringComparison.OrdinalIgnoreCase);
        Console.WriteLine(same ? "Exe hash matches." : $"Exe hash differs: {hash}");
        ok &= same;
    }
    return ok ? 0 : 1;
}

static string? VersionOf(string exe)
{
    var info = FileVersionInfo.GetVersionInfo(exe);
    var text = info.ProductVersion ?? info.FileVersion;
    if (text is null) return null;
    text = text.Split('+', '-')[0];
    return Version.TryParse(text, out var v) ? $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}" : null;
}

string? Option(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

string Required(string name) => Option(name) ?? throw new ArgumentException($"Missing {name}. Run without arguments for help.");

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}

static int Usage()
{
    Console.WriteLine("""
        SeaRaider release tool
          keygen [--out <folder>]
          sign   --exe <SeaRaider.exe> --url <https address of the exe> --key <searaider-update-key.pem> [--notes "..."] [--version x.y.z] [--out latest.json]
          verify --manifest <latest.json> --public-key <base64> [--exe <SeaRaider.exe>]
        """);
    return 1;
}
