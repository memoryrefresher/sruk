using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SeaRaider.Core.Updates;

/// <summary>
/// The release file a SeaRaider build checks (latest.json): which version is newest, where its SeaRaider.exe is, the
/// exe's SHA-256, release notes, and a signature over the version, address and hash made with the publisher's private
/// key. Written by tools/SeaRaider.ReleaseTool (<c>sign</c>).
/// </summary>
public sealed class UpdateManifest
{
    [JsonPropertyName("version")] public string Version { get; set; } = string.Empty;
    [JsonPropertyName("url")] public string Url { get; set; } = string.Empty;
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = string.Empty;
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("notes")] public string? Notes { get; set; }
    [JsonPropertyName("published")] public DateTime? Published { get; set; }
    [JsonPropertyName("signature")] public string Signature { get; set; } = string.Empty;

    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>The version as major.minor.build, or null when it isn't one.</summary>
    [JsonIgnore]
    public Version? ParsedVersion => System.Version.TryParse(Version.TrimStart('v', 'V'), out var v) ? UpdateDefaults.Normalize(v) : null;

    /// <summary>What the signature covers: version, address, hash and size, one per line.</summary>
    public byte[] SignedBytes() =>
        Encoding.UTF8.GetBytes($"searaider-update-v1\n{Version.Trim()}\n{Url.Trim()}\n{Sha256.Trim().ToLowerInvariant()}\n{Size}");
}

/// <summary>
/// ECDSA P-256 signatures for releases. The private key stays with the publisher (keygen writes it to a file you keep
/// safe and never commit); the public key is baked into the app.
/// </summary>
public static class UpdateSigning
{
    /// <summary>A new key pair: the private key as PEM (keep secret) and the public key as base64 (goes in the build).</summary>
    public static (string PrivateKeyPem, string PublicKeyBase64) CreateKeyPair()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (key.ExportPkcs8PrivateKeyPem(), Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
    }

    public static string PublicKeyOf(string privateKeyPem)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(privateKeyPem);
        return Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    }

    public static void Sign(UpdateManifest manifest, string privateKeyPem)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(privateKeyPem);
        manifest.Signature = Convert.ToBase64String(key.SignData(manifest.SignedBytes(), HashAlgorithmName.SHA256));
    }

    /// <summary>True when the manifest was signed by the holder of <paramref name="publicKeyBase64"/>'s private key.</summary>
    public static bool Verify(UpdateManifest manifest, string? publicKeyBase64)
    {
        if (string.IsNullOrWhiteSpace(publicKeyBase64) || string.IsNullOrWhiteSpace(manifest.Signature)) return false;
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);
            return key.VerifyData(manifest.SignedBytes(), Convert.FromBase64String(manifest.Signature), HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return false;
        }
    }

    public static async Task<string> Sha256OfFileAsync(string path, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false)).ToLowerInvariant();
    }
}
