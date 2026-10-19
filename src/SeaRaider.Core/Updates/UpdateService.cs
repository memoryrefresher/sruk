using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using SeaRaider.Core.WebChallenge;

namespace SeaRaider.Core.Updates;

/// <summary>A release that can't be trusted or used (bad signature, wrong hash, unreadable release file).</summary>
public sealed class UpdateException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Keeps SeaRaider up to date from the publisher's release file (<see cref="UpdateDefaults.FeedUrl"/>):
/// <list type="number">
///   <item><see cref="CheckAsync"/> reads latest.json and accepts it only when its signature matches the key baked into
///         this build and its version is newer;</item>
///   <item><see cref="DownloadAsync"/> fetches the new SeaRaider.exe and checks its SHA-256 (and size) against the signed
///         release file, so a swapped or damaged file is never run;</item>
///   <item><see cref="StartApplying"/> starts the new exe in its helper mode (<see cref="ApplySwitch"/>): it waits for this
///         SeaRaider to close, copies itself over SeaRaider.exe (asking for administrator rights only when the folder needs
///         it), and starts the updated SeaRaider.</item>
/// </list>
/// Only a published single-file SeaRaider.exe updates itself; a development build (dotnet run) just reports new versions.
/// </summary>
public sealed class UpdateService(IHttpClientFactory httpClientFactory, ILogger<UpdateService> logger)
{
    /// <summary>Command-line switch of the helper mode: <c>--apply-update "target exe" pid</c>.</summary>
    public const string ApplySwitch = "--apply-update";

    /// <summary>Passed to the updated SeaRaider when it starts, so it can say it was updated.</summary>
    public const string UpdatedSwitch = "--updated";

    public static string UpdatesDirectory => Path.Combine(AppPaths.DataDirectory, "updates");

    /// <summary>This build has a release file and a key to check it with.</summary>
    public static bool IsConfigured => !string.IsNullOrWhiteSpace(UpdateDefaults.FeedUrl) && !string.IsNullOrWhiteSpace(UpdateDefaults.PublicKey);

    /// <summary>Running as a published single SeaRaider.exe (not from a build folder), so it can replace itself.</summary>
    public static bool CanInstall =>
        IsConfigured && Environment.ProcessPath is { } exe && exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
        !File.Exists(Path.Combine(AppContext.BaseDirectory, Path.GetFileNameWithoutExtension(exe) + ".dll"));

    /// <summary>The newest release when it's newer than this build (and properly signed); null when up to date.</summary>
    /// <exception cref="UpdateException">The release file can't be read or isn't signed with this build's key.</exception>
    public async Task<UpdateManifest?> CheckAsync(CancellationToken ct)
    {
        if (!IsConfigured) return null;

        UpdateManifest? manifest;
        try
        {
            var client = httpClientFactory.CreateClient(HttpClientNames.Metadata);
            using var request = new HttpRequestMessage(HttpMethod.Get, AddCacheBuster(UpdateDefaults.FeedUrl!));
            request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            // GitHub serves release files as application/octet-stream: read the text, then parse it.
            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            manifest = System.Text.Json.JsonSerializer.Deserialize<UpdateManifest>(json, UpdateManifest.JsonOptions);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new UpdateException($"Couldn't read the update information ({ex.Message}).", ex);
        }

        if (manifest?.ParsedVersion is not { } version) throw new UpdateException("The update information has no valid version.");
        if (!UpdateSigning.Verify(manifest, UpdateDefaults.PublicKey))
            throw new UpdateException("The update information isn't signed with this app's key, so it was ignored.");
        if (!Uri.TryCreate(manifest.Url, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
            throw new UpdateException("The update's download address isn't a secure (https) link.");
        if (manifest.Sha256.Length != 64 || !manifest.Sha256.All(Uri.IsHexDigit))
            throw new UpdateException("The update information has no valid file hash.");

        logger.LogInformation("Update check: running {Current}, newest {Latest}", UpdateDefaults.CurrentVersion, version);
        return version > UpdateDefaults.CurrentVersion ? manifest : null;
    }

    /// <summary>Downloads the release's SeaRaider.exe to the updates folder and checks it; returns its path.</summary>
    /// <exception cref="UpdateException">The file doesn't match the signed hash or size.</exception>
    public async Task<string> DownloadAsync(UpdateManifest manifest, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(UpdatesDirectory);
        var target = Path.Combine(UpdatesDirectory, $"SeaRaider-{manifest.ParsedVersion}.exe");
        if (File.Exists(target) && string.Equals(await UpdateSigning.Sha256OfFileAsync(target, ct).ConfigureAwait(false), manifest.Sha256, StringComparison.OrdinalIgnoreCase))
            return target;

        var partial = target + ".partial";
        try
        {
            var client = httpClientFactory.CreateClient(HttpClientNames.Downloads);
            using var request = new HttpRequestMessage(HttpMethod.Get, manifest.Url);
            request.Options.Set(ChallengeRequestOptions.NoChallenge, true);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? (manifest.Size > 0 ? manifest.Size : 0);

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var file = File.Create(partial))
            {
                var buffer = new byte[1 << 16];
                long read = 0;
                int n;
                while ((n = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    hash.AppendData(buffer, 0, n);
                    read += n;
                    if (total > 0) progress?.Report(Math.Min(1, (double)read / total));
                }
                if (manifest.Size > 0 && read != manifest.Size)
                    throw new UpdateException($"The update download is the wrong size ({read:N0} bytes, expected {manifest.Size:N0}).");
            }

            var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (!string.Equals(actual, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new UpdateException("The downloaded update doesn't match its signed hash, so it wasn't installed.");

            File.Move(partial, target, overwrite: true);
            progress?.Report(1);
            return target;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException && ex is not UpdateException)
        {
            throw new UpdateException($"Couldn't download the update ({ex.Message}).", ex);
        }
        finally
        {
            try { if (File.Exists(partial)) File.Delete(partial); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Starts the downloaded exe in helper mode; it replaces this SeaRaider.exe once this process has exited and starts it
    /// again. The caller closes SeaRaider right after.
    /// </summary>
    public static void StartApplying(string downloadedExe)
    {
        var target = Environment.ProcessPath ?? throw new UpdateException("Can't tell where SeaRaider.exe is.");
        var start = new ProcessStartInfo(downloadedExe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(downloadedExe)! };
        start.ArgumentList.Add(ApplySwitch);
        start.ArgumentList.Add(target);
        start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Process.Start(start);
    }

    /// <summary>
    /// The helper mode (run by the new exe, before any window): wait for the old SeaRaider to close, put this exe in its
    /// place, start it. Asks for administrator rights only when SeaRaider's folder can't be written (Program Files).
    /// </summary>
    public static int RunApplyHelper(string[] args)
    {
        if (args.Length < 3 || !int.TryParse(args[2], out var pid)) return 2;
        var target = args[1];
        var self = Environment.ProcessPath!;
        var log = Path.Combine(UpdatesDirectory, "update.log");
        void Log(string line)
        {
            try { File.AppendAllText(log, $"{DateTime.Now:u} {line}{Environment.NewLine}"); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        try
        {
            try { using var old = Process.GetProcessById(pid); old.WaitForExit(TimeSpan.FromSeconds(90)); }
            catch (ArgumentException) { } // already closed

            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Copy(self, target, overwrite: true);
                    break;
                }
                catch (UnauthorizedAccessException) when (!args.Contains("--elevated"))
                {
                    // Program Files and the like: the same step again, as administrator.
                    Log("folder needs administrator rights; asking");
                    var elevated = new ProcessStartInfo(self) { UseShellExecute = true, Verb = "runas" };
                    foreach (var a in args) elevated.ArgumentList.Add(a);
                    elevated.ArgumentList.Add("--elevated");
                    try { Process.Start(elevated); }
                    catch (System.ComponentModel.Win32Exception) { Log("administrator rights refused"); StartOld(target); }
                    return 0;
                }
                catch (IOException) when (attempt < 40)
                {
                    Thread.Sleep(500); // the old exe is still closing
                }
            }

            Log($"updated {target}");
            Process.Start(new ProcessStartInfo(target, UpdatedSwitch) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(target)! });
            return 0;
        }
        catch (Exception ex)
        {
            Log($"update failed: {ex}");
            StartOld(target);
            return 1;
        }

        static void StartOld(string exe)
        {
            try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! }); }
            catch (Exception) { /* nothing more to do */ }
        }
    }

    /// <summary>Removes downloaded updates that are no longer needed (called a while after start-up).</summary>
    public static void CleanUp()
    {
        try
        {
            if (!Directory.Exists(UpdatesDirectory)) return;
            foreach (var file in Directory.EnumerateFiles(UpdatesDirectory, "SeaRaider-*.exe*"))
            {
                if (string.Equals(file, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) continue;
                try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string AddCacheBuster(string url) =>
        url + (url.Contains('?') ? "&" : "?") + "t=" + DateTime.UtcNow.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
