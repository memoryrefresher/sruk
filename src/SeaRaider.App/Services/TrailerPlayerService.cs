using System.Text.Json;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SeaRaider.Core;
using SeaRaider.Core.Scraping;

namespace SeaRaider.App.Services;

/// <summary>One trailer to show in the in-app player overlay.</summary>
/// <param name="PlayerFolder">%LOCALAPPDATA%\SeaRaider\player (mapped to a private https host inside the player).</param>
/// <param name="PageName">The player page in that folder (unique per session so a reused browser reloads it).</param>
/// <param name="Title">Game title, shown above the video.</param>
/// <param name="Url">What's playing (for "Watching: …" and Watch along).</param>
/// <param name="StartSeconds">Where it started.</param>
/// <param name="ThumbnailUrl">Its picture, when known.</param>
/// <param name="IsLive">A live stream (Twitch channel): no position to follow.</param>
public sealed record TrailerSession(string PlayerFolder, string PageName, string Title, string Url = "", int StartSeconds = 0,
    string? ThumbnailUrl = null, bool IsLive = false);

/// <summary>
/// Plays trailers inside SeaRaider: <see cref="Current"/> is shown by the main window in an overlay (a WebView2 hosted in
/// the window, over the blurred app), never the user's browser unless WebView2 is missing.
/// <list type="bullet">
///   <item><b>Steam</b> (2025+: streams only): DASH first with dash.js, as Steam's own store player does — video and
///         audio come as separate tracks, which is why the HLS attempt in v0.6 could end up silent. HLS (hls.js) is the
///         fallback. Both libraries are bundled.</item>
///   <item><b>YouTube</b> (IGDB trailers, videos embedded in a post): YouTube's embedded player in the same overlay.</item>
///   <item>Plain video files play directly.</item>
/// </list>
/// </summary>
public sealed partial class TrailerPlayerService(ILogger<TrailerPlayerService> logger, ShellService shell) : ObservableObject
{
    private Action? _fallback;
    private int _sessionNumber;

    /// <summary>The trailer being shown in the overlay, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOpen))]
    private TrailerSession? _current;

    public bool IsOpen => Current is not null;

    /// <summary>The player page says where it is (every couple of seconds, and on play / pause / seek).</summary>
    public event EventHandler<PlaybackReport>? PlaybackReported;

    /// <summary>Asks the open player to be at this position and play / pause (Watch along).</summary>
    public event EventHandler<PlaybackReport>? SyncRequested;

    internal void ReportPlayback(TrailerSession session, double position, bool playing)
    {
        if (ReferenceEquals(Current, session)) PlaybackReported?.Invoke(this, new PlaybackReport(session, position, playing));
    }

    /// <summary>Moves the open player to <paramref name="position"/> if it's more than a moment off, and plays or pauses.</summary>
    public void Sync(double position, bool playing)
    {
        if (Current is { } session) SyncRequested?.Invoke(this, new PlaybackReport(session, position, playing));
    }

    [GeneratedRegex(@"\.(?:m3u8|mpd|mp4|webm|m4v)(?:[?#]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex StreamRegex();

    public static bool IsStream(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && StreamRegex().IsMatch(uri.AbsolutePath);

    /// <param name="url">The trailer (stream, file or video page).</param>
    /// <param name="storePageUrl">Where to watch it if it can't play here.</param>
    /// <param name="title">Game title for the overlay.</param>
    /// <param name="startSeconds">Start playing from here (a shared "watch from 1:23", a time in a comment).</param>
    /// <param name="thumbnailUrl">Its picture, for friends' "Watching" (optional).</param>
    public void Play(string url, string? storePageUrl, string title, int? startSeconds = null, string? thumbnailUrl = null)
    {
        var start = startSeconds is > 0 ? startSeconds.Value : 0;
        var youTubeId = MediaUrlHelper.TryGetYouTubeId(url);
        var embed = youTubeId is null ? EmbedUrl(url, start) : null; // Twitch, Vimeo
        if (youTubeId is null && embed is null && !IsStream(url))
        {
            shell.OpenUrl(url); // a video page SeaRaider can't embed
            return;
        }

        string page;
        try
        {
            page = WritePlayerPage(url, youTubeId, embed, storePageUrl, title, start, ++_sessionNumber);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Couldn't write the trailer player; opening the stream in the browser");
            shell.OpenUrl(storePageUrl ?? url);
            return;
        }

        // Without SeaRaider's player: YouTube refuses embeds in a local file, so open YouTube itself; streams use the page.
        void Fallback()
        {
            if (youTubeId is not null) shell.OpenUrl($"https://www.youtube.com/watch?v={youTubeId}" + (start > 0 ? $"&t={start}s" : ""));
            else if (embed is not null) shell.OpenUrl(url); // Twitch / Vimeo: their own site
            else shell.OpenFile(page);
        }

        if (!OperatingSystem.IsWindows())
        {
            Fallback();
            return;
        }

        // Null first, so the overlay builds a fresh player for the new trailer.
        Current = null;
        _fallback = Fallback;
        var live = embed?.Contains("player.twitch.tv/?channel=", StringComparison.Ordinal) == true;
        Current = new TrailerSession(Path.GetDirectoryName(page)!, Path.GetFileName(page), title, url, start,
            thumbnailUrl ?? (youTubeId is not null ? $"https://i.ytimg.com/vi/{youTubeId}/hqdefault.jpg" : null), live);
    }

    [RelayCommand]
    public void Close()
    {
        Current = null;
        _fallback = null;
    }

    /// <summary>The player couldn't start (no WebView2 Runtime): close the overlay and use the default browser.</summary>
    public void ReportFailed(TrailerSession session, Exception error)
    {
        logger.LogWarning(error, "Trailer player failed; using the browser");
        if (!ReferenceEquals(Current, session)) return;
        var fallback = _fallback;
        Close();
        fallback?.Invoke();
    }

    /// <summary>A link in the player ("Watch it on the Steam store"): always the default browser.</summary>
    public void OpenExternal(string url) => shell.OpenUrl(url);

    /// <summary>Writes hls.js (once) and the player page for this trailer into %LOCALAPPDATA%\SeaRaider\player.</summary>
    /// <summary>The private address the player page is served from (Twitch embeds must name it as their "parent").</summary>
    public const string PlayerHost = "player.searaider.local";

    [GeneratedRegex(@"^https?://(?:www\.|m\.)?twitch\.tv/videos/(?<video>\d+)|^https?://(?:clips\.twitch\.tv/|(?:www\.)?twitch\.tv/[^/]+/clip/)(?<clip>[\w-]+)|^https?://(?:www\.|m\.)?twitch\.tv/(?<channel>[A-Za-z0-9_]{3,25})/?$", RegexOptions.IgnoreCase)]
    private static partial Regex TwitchRegex();

    [GeneratedRegex(@"vimeo\.com/(?:video/)?(?<id>\d{6,})", RegexOptions.IgnoreCase)]
    private static partial Regex VimeoRegex();

    /// <summary>The embeddable player for a Twitch channel / past broadcast / clip or a Vimeo video, else null.</summary>
    /// <param name="startSeconds">Start time for a Twitch past broadcast or a Vimeo video (live channels and clips ignore it).</param>
    public static string? EmbedUrl(string url, int startSeconds = 0)
    {
        // Links often carry their own time (?t=1h2m3s): the regexes below need the bare address.
        var bare = url.Split('?', '#')[0];
        var twitch = TwitchRegex().Match(bare);
        if (twitch.Success)
        {
            var parent = $"&parent={PlayerHost}&autoplay=true";
            if (twitch.Groups["video"].Success)
            {
                var time = startSeconds > 0 ? $"&time={startSeconds / 3600}h{startSeconds / 60 % 60}m{startSeconds % 60}s" : "";
                return $"https://player.twitch.tv/?video=v{twitch.Groups["video"].Value}{parent}{time}";
            }
            if (twitch.Groups["clip"].Success) return $"https://clips.twitch.tv/embed?clip={twitch.Groups["clip"].Value}{parent}";
            return $"https://player.twitch.tv/?channel={twitch.Groups["channel"].Value.ToLowerInvariant()}{parent}";
        }
        return VimeoRegex().Match(url) is { Success: true } vimeo
            ? $"https://player.vimeo.com/video/{vimeo.Groups["id"].Value}?autoplay=1" + (startSeconds > 0 ? $"#t={startSeconds}s" : "")
            : null;
    }

    private static string WritePlayerPage(string url, string? youTubeId, string? embed, string? storePageUrl, string title, int start, int session)
    {
        var folder = Path.Combine(AppPaths.DataDirectory, "player");
        Directory.CreateDirectory(folder);
        CopyResource("SeaRaider.Player.hls.min.js", Path.Combine(folder, "hls.min.js"));
        CopyResource("SeaRaider.Player.LICENSE-hls.js.txt", Path.Combine(folder, "LICENSE-hls.js.txt"));
        CopyResource("SeaRaider.Player.dash.all.min.js", Path.Combine(folder, "dash.all.min.js"));
        CopyResource("SeaRaider.Player.LICENSE-dash.js.txt", Path.Combine(folder, "LICENSE-dash.js.txt"));

        // Steam lists the same trailer as HLS and as DASH in one folder: hls_264_master.m3u8 / dash_h264.mpd.
        var dash = url.Contains("hls_264_master.m3u8", StringComparison.OrdinalIgnoreCase)
            ? url.Replace("hls_264_master.m3u8", "dash_h264.mpd", StringComparison.OrdinalIgnoreCase)
            : url.Contains(".mpd", StringComparison.OrdinalIgnoreCase) ? url : null;
        var hls = url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) ? url : null;
        var parameters = JsonSerializer.Serialize(new
        {
            src = dash is null && hls is null && youTubeId is null && embed is null ? url : null,
            dash,
            hls,
            youtube = youTubeId,
            embed,
            store = storePageUrl,
            title,
            start,
        });
        // Earlier trailers' pages (each session gets its own name so the player always loads the new one).
        foreach (var old in Directory.EnumerateFiles(folder, "trailer*.html"))
        {
            try { File.Delete(old); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        var page = Path.Combine(folder, $"trailer-{session}.html");
        // Inside a <script>: "</" can't appear in the JSON.
        File.WriteAllText(page, PlayerHtml.Replace("__PARAMS__", parameters.Replace("</", "<\\/"), StringComparison.Ordinal));
        return page;
    }

    private static void CopyResource(string name, string path)
    {
        using var resource = typeof(TrailerPlayerService).Assembly.GetManifestResourceStream(name);
        if (resource is null) return;
        if (File.Exists(path) && new FileInfo(path).Length == resource.Length) return;
        using var file = File.Create(path);
        resource.CopyTo(file);
    }

    private const string PlayerHtml = """
        <!doctype html>
        <html>
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>Trailer</title>
        <style>
          html, body { margin: 0; height: 100%; background: #000; color: #e3edf2; font-family: "Segoe UI", sans-serif; overflow: hidden; }
          video { width: 100%; height: 100%; background: #000; outline: none; display: block; }
          #msg { position: absolute; inset: 0; display: none; align-items: center; justify-content: center; flex-direction: column;
                 gap: 16px; text-align: center; padding: 24px; font-size: 15px; }
          #msg a { color: #3fe0c5; font-size: 16px; font-weight: 600; cursor: pointer; }
        </style>
        </head>
        <body>
        <video id="v" controls autoplay playsinline></video>
        <div id="msg"><div id="why"></div><a id="store" style="display:none">Watch it on the Steam store</a></div>
        <script src="dash.all.min.js"></script>
        <script src="hls.min.js"></script>
        <script>
          const p = __PARAMS__;
          document.title = p.title ? p.title + ' · Trailer' : 'Trailer';
          const v = document.getElementById('v');
          const post = m => { if (window.chrome && window.chrome.webview) window.chrome.webview.postMessage(m); };
          document.addEventListener('keydown', e => { if (e.key === 'Escape' && !document.fullscreenElement) post('close'); });
          const external = url => {
            if (window.chrome && window.chrome.webview) window.chrome.webview.postMessage('open:' + url);
            else window.location.href = url;
          };
          const fail = why => {
            v.style.display = 'none';
            document.getElementById('msg').style.display = 'flex';
            document.getElementById('why').textContent = why;
            if (p.store) {
              const a = document.getElementById('store');
              a.style.display = 'block';
              a.onclick = e => { e.preventDefault(); external(p.store); };
            }
          };
          const tryDash = (url, next) => {
            if (!url || !window.dashjs) return next();
            const player = dashjs.MediaPlayer().create();
            let failed = false;
            player.on(dashjs.MediaPlayer.events.ERROR, () => {
              if (failed) return;
              failed = true;
              player.reset();
              next();
            });
            player.initialize(v, url, true);
          };
          const tryHls = (url, next) => {
            if (!url) return next();
            if (window.Hls && Hls.isSupported()) {
              const hls = new Hls({ maxBufferLength: 30 });
              hls.on(Hls.Events.ERROR, (event, data) => {
                if (!data.fatal) return;
                if (data.type === Hls.ErrorTypes.MEDIA_ERROR) hls.recoverMediaError();
                else { hls.destroy(); next(); }
              });
              hls.on(Hls.Events.MANIFEST_PARSED, () => v.play().catch(() => {}));
              hls.loadSource(url);
              hls.attachMedia(v);
            } else if (v.canPlayType('application/vnd.apple.mpegurl')) {
              v.onerror = next;
              v.src = url;
            } else {
              next();
            }
          };
          const youtube = id => frameOf('https://www.youtube.com/embed/' + encodeURIComponent(id) + '?autoplay=1&rel=0&playsinline=1&modestbranding=1'
            + '&enablejsapi=1&origin=' + encodeURIComponent(location.origin)
            + (p.start > 0 ? '&start=' + Math.floor(p.start) : ''), 'youtube');
          // Video files and Steam streams: jump to the shared time once the video knows its length.
          if (p.start > 0) v.addEventListener('loadedmetadata', () => { try { v.currentTime = p.start; } catch (e) {} }, { once: true });
          const frameOf = (src, kind) => {
            v.style.display = 'none';
            const frame = document.createElement('iframe');
            frame.onload = () => watchFrame(frame, kind || (src.indexOf('player.vimeo.com') >= 0 ? 'vimeo' : 'other'));
            frame.src = src;
            frame.allow = 'autoplay; encrypted-media; picture-in-picture; fullscreen';
            frame.allowFullscreen = true;
            frame.referrerPolicy = 'strict-origin-when-cross-origin';
            frame.style.cssText = 'position:absolute;inset:0;width:100%;height:100%;border:0;background:#000';
            document.body.appendChild(frame);
          };

          // ---- Where playback is (for friends' "Watching" and Watch along), and following a friend's player ----
          // Reported to SeaRaider as "state:<seconds>:<1 playing|0 paused>" on play / pause / seek and every 2 s.
          let pos = p.start || 0, playing = true, lastSent = 0, control = null, noPos = false;
          const report = force => {
            const now = Date.now();
            if (!force && now - lastSent < 2000) return;
            lastSent = now;
            // -1: this player can't say where it is (Twitch).
            post('state:' + (noPos ? -1 : Math.round(pos * 10) / 10) + ':' + (playing ? 1 : 0));
          };
          setInterval(() => report(true), 2000);

          // Plain video (files, Steam streams).
          ['play', 'playing'].forEach(e => v.addEventListener(e, () => { playing = true; pos = v.currentTime; report(true); }));
          v.addEventListener('pause', () => { playing = false; pos = v.currentTime; report(true); });
          v.addEventListener('seeked', () => { pos = v.currentTime; report(true); });
          v.addEventListener('timeupdate', () => { pos = v.currentTime; report(false); });
          control = {
            get: () => v.currentTime,
            seek: t => { try { v.currentTime = t; } catch (e) {} },
            play: () => v.play().catch(() => {}),
            pause: () => v.pause(),
          };

          // YouTube and Vimeo players talk over postMessage.
          const watchFrame = (frame, kind) => {
            const send = msg => { try { frame.contentWindow.postMessage(JSON.stringify(msg), '*'); } catch (e) {} };
            if (kind === 'youtube') {
              const yt = (func, args) => send({ event: 'command', func, args: args || [], id: 1, channel: 'widget' });
              send({ event: 'listening', id: 1, channel: 'widget' });
              setTimeout(() => send({ event: 'listening', id: 1, channel: 'widget' }), 1500);
              window.addEventListener('message', e => {
                if (e.source !== frame.contentWindow) return;
                let d; try { d = typeof e.data === 'string' ? JSON.parse(e.data) : e.data; } catch (x) { return; }
                const info = d && d.info;
                if (!info || typeof info !== 'object') return;
                const wasPlaying = playing;
                if (typeof info.currentTime === 'number') {
                  const jumped = Math.abs(info.currentTime - pos) > 3;
                  pos = info.currentTime;
                  if (jumped) report(true);
                }
                if (typeof info.playerState === 'number' && info.playerState !== 3) playing = info.playerState === 1;
                if (wasPlaying !== playing) report(true); else report(false);
              });
              control = { get: () => pos, seek: t => yt('seekTo', [t, true]), play: () => yt('playVideo'), pause: () => yt('pauseVideo') };
            } else if (kind === 'vimeo') {
              const vm = (method, value) => send(value === undefined ? { method } : { method, value });
              const listen = () => ['timeupdate', 'play', 'pause', 'seeked'].forEach(n => vm('addEventListener', n));
              listen();
              window.addEventListener('message', e => {
                if (e.source !== frame.contentWindow) return;
                let d; try { d = typeof e.data === 'string' ? JSON.parse(e.data) : e.data; } catch (x) { return; }
                if (!d || !d.event) return;
                if (d.event === 'ready') listen();
                if (d.data && typeof d.data.seconds === 'number') pos = d.data.seconds;
                if (d.event === 'play') { playing = true; report(true); }
                else if (d.event === 'pause') { playing = false; report(true); }
                else if (d.event === 'seeked') report(true);
                else report(false);
              });
              control = { get: () => pos, seek: t => vm('setCurrentTime', t), play: () => vm('play'), pause: () => vm('pause') };
            } else {
              control = null; // Twitch: live, or a past broadcast we can't steer
              noPos = true;
            }
          };

          // Watch along: SeaRaider sends "sync:<seconds>:<1|0>" with where the friend's player is now.
          if (window.chrome && window.chrome.webview) window.chrome.webview.addEventListener('message', e => {
            const m = /^sync:([0-9.]+):([01])$/.exec(String(e.data));
            if (!m || !control) return;
            const target = parseFloat(m[1]), play = m[2] === '1';
            if (Math.abs(control.get() - target) > 2.5) { control.seek(target); pos = target; }
            if (play && !playing) control.play();
            if (!play && playing) control.pause();
          });

          if (p.youtube) youtube(p.youtube);
          else if (p.embed) frameOf(p.embed);
          else if (p.dash || p.hls) tryDash(p.dash, () => tryHls(p.hls, () => fail('The trailer stream could not be loaded.')));
          else if (p.src) { v.onerror = () => fail('The trailer could not be loaded.'); v.src = p.src; }
          else fail('No trailer address.');
        </script>
        </body>
        </html>
        """;
}

/// <summary>Where a player is: <paramref name="Position"/> seconds in, playing or paused.</summary>
public sealed record PlaybackReport(TrailerSession Session, double Position, bool Playing);
