using System.Text.RegularExpressions;

namespace SeaRaider.Core.Downloads;

/// <summary>
/// What SeaRaider's download browser does on a file host's page (used by the app's download window), plus the rules
/// for which pop-ups and downloads belong to the game file. Kept here so the rules are unit-tested.
/// </summary>
public static partial class BrowserPageScript
{
    private static readonly string[] FileExtensions =
    [
        ".rar", ".zip", ".7z", ".bin", ".exe", ".iso", ".tar", ".gz", ".xz", ".bz2", ".zst", ".cab", ".msi", ".pkg", ".dat",
    ];

    [GeneratedRegex(@"\.(?:part\d{1,4}\.rar|r\d{2,3}|z\d{2,3}|\d{3})$", RegexOptions.IgnoreCase)]
    private static partial Regex VolumeSuffixRegex();

    /// <summary>
    /// JavaScript that reports the page's state as JSON — <c>dead</c> ("file not found"), <c>captcha</c> (a captcha that
    /// isn't solved yet), <c>challenge</c> (a browser check is running), <c>waiting</c> (a link shortener's countdown is
    /// still running), <c>href</c> (a link to follow: a direct file link such as FuckingFast's <c>/dl/</c> link, or the
    /// shortener's / download page's button when it leads to another site) — and, when <paramref name="click"/> is true and
    /// nothing above applies, clicks the most prominent visible "Download" button (or, on a shortener, its unlocked
    /// "Continue" / "Get Link" button) and reports its text as <c>clicked</c>.
    /// </summary>
    /// <param name="click">May the script press a button now.</param>
    /// <param name="wantTorrent">
    /// Collecting a torrent: a magnet link on the page is reported as <c>magnet</c> (and a .torrent link as <c>href</c>),
    /// and "Download Torrent" buttons count as download buttons.
    /// </param>
    /// <param name="fromShortener">
    /// The page was reached through a link shortener: the download page it leads to may send its "Download File" button
    /// to another site (a CDN with no file extension), which is followed instead of skipped as an ad.
    /// </param>
    /// <param name="blocked">Links that led to an ad trap before: never followed or clicked again on this page.</param>
    public static string Build(bool click, bool fromShortener = false, bool wantTorrent = false, IEnumerable<string>? blocked = null) => Template
        .Replace("__BLOCKED__", System.Text.Json.JsonSerializer.Serialize((blocked ?? []).ToArray()), StringComparison.Ordinal)
        .Replace("__MARKERS__", DeadLinkDetector.MarkersJson, StringComparison.Ordinal)
        .Replace("__SHORTENERS__", System.Text.Json.JsonSerializer.Serialize(SeaRaider.Core.Scraping.FileHosts.LinkShortenerDomains), StringComparison.Ordinal)
        .Replace("__FROM_SHORTENER__", fromShortener ? "true" : "false", StringComparison.Ordinal)
        .Replace("__WANT_TORRENT__", wantTorrent ? "true" : "false", StringComparison.Ordinal)
        .Replace("__CLICK__", click ? "true" : "false", StringComparison.Ordinal);

    private const string Template = """
        (() => {
          const res = { dead: false, captcha: false, challenge: false, waiting: false, trap: false, href: null, clicked: null, magnet: null, note: null };
          const title = (document.title || '').toLowerCase();
          // A whole-page browser check (Cloudflare's "Just a moment…"). A Turnstile "I am human" box inside a host's own
          // page (DataNodes) is not one: it stays in the page after it's ticked, so it is handled as a captcha below.
          res.challenge = title.includes('just a moment') || title.includes('attention required') ||
            title.includes('checking your browser') || title.includes('ddos-guard') ||
            !!document.querySelector('#challenge-form, #challenge-running, #cf-challenge-running');
          if (!document.body) return JSON.stringify(res);

          // Ad trap: "Press Allow to prove you are not a robot" asks for notification permission, never a download.
          const bodyText = (document.body.innerText || '').slice(0, 5000);
          if (/(press|click|tap)\s*["'“”]?allow["'“”]?\s*(to|if)\b|allow\s+notifications?\s+to\s+(continue|prove|verify|download|watch)/i.test(bodyText) ||
              /\b(virus|viruses|malware|trojan|spyware)\b[^.]{0,40}\b(detected|found|alert|infected)\b|your\s+(pc|computer|device|phone|system|windows)\s+(is|may be|has been)\s+(infected|at risk|damaged|compromised)|call\s+(microsoft|windows|apple)\s+support|(scan|clean)\s+your\s+(pc|computer|device)\s+now/i.test(bodyText)) {
            res.trap = true; return JSON.stringify(res);
          }
          // Checked after the ad trap: a fake "checking your browser" page that asks you to press Allow is an ad, not a check.
          if (res.challenge) return JSON.stringify(res);
          const blocked = new Set(__BLOCKED__);
          const blockedEl = el => {
            const href = el.getAttribute && el.getAttribute('href');
            if (!href) return false;
            try { return blocked.has(new URL(href, location.href).href); } catch { return false; }
          };

          // Collecting a torrent: a magnet or .torrent link on the page is the answer.
          const wantTorrent = __WANT_TORRENT__;
          if (wantTorrent) {
            const magnet = [...document.querySelectorAll('a[href^="magnet:"]')].map(a => a.getAttribute('href'))
              .concat((document.documentElement.innerHTML.match(/magnet:\?xt=urn:bt[^"'\s<>]+/gi) || []).map(m => m.replace(/&amp;/g, '&')))
              .find(m => /xt=urn:bt/i.test(m));
            if (magnet) { res.magnet = magnet; return JSON.stringify(res); }
            const file = [...document.querySelectorAll('a[href]')].map(a => { try { return new URL(a.getAttribute('href'), location.href); } catch { return null; } })
              .find(u => u && /^https?:$/.test(u.protocol) && /\.torrent$/i.test(u.pathname));
            if (file) { res.href = file.href; return JSON.stringify(res); }
          }

          const html = document.documentElement.innerHTML;
          const site = location.hostname.replace(/^www\./, '');
          const direct = (html.match(/https?:\/\/[^"'\s<>\\]+\/dl\/[A-Za-z0-9_\-]{8,}/g) || []).find(u => {
            try { return new URL(u).hostname.endsWith(site); } catch { return false; }
          });
          if (direct) { res.href = direct; return JSON.stringify(res); }

          // Link shortener: a wait screen, then a button to the real download page (often on another site).
          const shortener = __SHORTENERS__.some(d => site === d || site.endsWith('.' + d));

          const relaxed = shortener || __FROM_SHORTENER__;

          const visible = el => {
            const r = el.getBoundingClientRect(); const st = getComputedStyle(el);
            return r.width >= 20 && r.height >= 10 && st.visibility !== 'hidden' && st.display !== 'none' && !el.disabled;
          };
          // Still locked while the timer runs: greyed out, unclickable, or marked disabled.
          const unlocked = el => {
            const st = getComputedStyle(el);
            return st.pointerEvents !== 'none' && parseFloat(st.opacity || '1') >= 0.5 &&
              !el.classList.contains('disabled') && el.getAttribute('aria-disabled') !== 'true' && !el.hasAttribute('disabled');
          };
          const label = el => (el.innerText || el.value || el.getAttribute('aria-label') || el.getAttribute('title') || '').trim(); // icon-only buttons name themselves in title / aria-label
          const counting = t => /wait|^\d+$|\b\d+\s*(s|secs?|seconds?)\b/i.test(t);
          const bad = wantTorrent
            ? /premium|fast download with|register|sign ?up|log ?in|\bapp\b|vpn|\bads?\b|sponsor|upgrade|buy|install|client/i
            : /premium|fast download with|register|sign ?up|log ?in|\bapp\b|vpn|torrent|\bads?\b|sponsor|upgrade|buy|install/i;
          const fileish = /\.(rar|zip|7z|bin|exe|iso|\d{3})(\?|#|$)/i;
          const realHref = el => {
            const href = el.getAttribute && el.getAttribute('href');
            return !!href && !href.startsWith('#') && !href.startsWith('javascript');
          };
          // megaup.net -> download.megaup.net is the same site, not an ad.
          const reg = h => h.split('.').slice(-2).join('.');
          const offsite = el => {
            if (!realHref(el)) return false;
            try { const u = new URL(el.getAttribute('href'), location.href); return reg(u.hostname) !== reg(location.hostname) && !fileish.test(u.pathname); }
            catch { return false; }
          };
          const all = [...document.querySelectorAll('a, button, input[type=submit], input[type=button]')].filter(el => !blockedEl(el));
          const buttons = all.filter(el => {
            const t = label(el);
            return (/download/i.test(t) || (wantTorrent && /torrent|magnet/i.test(t))) && t.length <= 40 && !bad.test(t) && !counting(t) && visible(el) &&
              (!relaxed || unlocked(el)) && (relaxed || !offsite(el));
          });
          const nextWords = /^\W*(continue|visit|get\s*link|go\s*to\s*link|go\s*to\s*download|next(\s*step)?|proceed|skip(\s*ad)?|open\s*link|unlock(\s*link)?|click\s*here\s*to\s*continue)\W*$/i;
          const nexts = shortener
            ? all.filter(el => { const t = label(el); return nextWords.test(t) && !bad.test(t) && visible(el) && unlocked(el); })
            : [];

          // Gofile: no "Download" button; the file itself is the thing to click (its row: checkbox, icon, name, date, size) and the
          // download starts at once. Only when the folder holds exactly one file, so a wrong file is never picked. Runs before the
          // captcha guess below, which a script-heavy page like this can trip by mistake. res.note shows what it saw in the status line.
          if (/(^|\.)gofile\.io$/.test(site)) {
            const sizeRe = /\b\d+(?:\.\d+)?\s?(?:KB|MB|GB|TB)\b/i;
            const dateRe = /\b(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)[a-z]*\.? \d{1,2}(?:st|nd|rd|th)?,? \d{4}/i;
            const rows = [...document.querySelectorAll('div, li, tr, a, button')].filter(e => {
              const t = e.innerText || '';
              return t.length < 300 && sizeRe.test(t) && dateRe.test(t) && visible(e);
            });
            const items = rows.filter(r => !rows.some(o => o !== r && r.contains(o))); // the innermost rows
            const turnstile = [...document.querySelectorAll('.cf-turnstile, .g-recaptcha, .h-captcha, iframe[src*="turnstile"], iframe[src*="recaptcha"]')].some(visible);
            res.note = 'gofile: ' + items.length + ' file row(s)' + (turnstile ? ', captcha shown' : '');
            if (__CLICK__ && items.length === 1 && !turnstile) {
              const row = items[0];
              const named = [...row.querySelectorAll('*')].filter(e => {
                const t = (e.innerText || '').trim();
                return t.length >= 3 && t.length <= 200 && !sizeRe.test(t) && !dateRe.test(t) && !e.querySelector('input, svg') && visible(e);
              }).sort((a, b) => (getComputedStyle(b).cursor === 'pointer') - (getComputedStyle(a).cursor === 'pointer') || b.innerText.length - a.innerText.length)[0];
              const pick = named || row;
              pick.scrollIntoView({ block: 'center' });
              for (const type of ['pointerdown', 'mousedown', 'pointerup', 'mouseup', 'click'])
                pick.dispatchEvent(new MouseEvent(type, { bubbles: true, cancelable: true, view: window }));
              res.clicked = 'file';
              res.note += ', clicked "' + (pick.innerText || '').trim().slice(0, 30) + '"';
              return JSON.stringify(res);
            }
          }

          const text = (document.body.innerText || '').toLowerCase();
          const markers = __MARKERS__;
          if (buttons.length === 0 && nexts.length === 0 && markers.some(m => text.includes(m))) { res.dead = true; return JSON.stringify(res); }

          const widgets = document.querySelectorAll('.g-recaptcha, .h-captcha, .cf-turnstile, iframe[src*="recaptcha"], iframe[src*="hcaptcha"], iframe[src*="turnstile"], iframe[src*="challenges.cloudflare.com"]');
          const answered = [...document.querySelectorAll('textarea[name="g-recaptcha-response"], textarea[name="h-captcha-response"], input[name="cf-turnstile-response"]')]
            .some(e => (e.value || '').length > 20);
          res.captcha = [...widgets].some(visible) && !answered;
          // A site's own check (an image or puzzle captcha) with no download button yet: the user's turn.
          if (!res.captcha && buttons.length === 0 &&
              /security check|captcha|are you (a )?human|not a robot|verify (that )?you('re| are) (a )?human|human verification/i.test(bodyText))
            res.captcha = true;
          if (res.captcha) return JSON.stringify(res);

          // On the wait screen with nothing unlocked yet: let the timer run.
          if (shortener && buttons.length === 0 && nexts.length === 0) { res.waiting = true; return JSON.stringify(res); }
          if (!__CLICK__ || (buttons.length === 0 && nexts.length === 0)) return JSON.stringify(res);

          const area = el => { const r = el.getBoundingClientRect(); return r.width * r.height; };
          const pool = buttons.length > 0 ? buttons : nexts; // the download screen's button wins over "Continue"
          pool.sort((a, b) => area(b) - area(a));
          const target = pool[0];
          const text0 = (label(target) || 'Download').slice(0, 40);

          // A shortener's button that leads to another site: open it here rather than as a pop-up (pop-ups to other
          // sites are blocked as ads).
          if (relaxed && target.tagName === 'A' && realHref(target)) {
            try {
              const u = new URL(target.getAttribute('href'), location.href);
              if (u.hostname !== location.hostname && (u.protocol === 'http:' || u.protocol === 'https:')) {
                res.href = u.href; res.clicked = text0; return JSON.stringify(res);
              }
            } catch { }
          }

          target.scrollIntoView({ block: 'center' });
          target.click();
          res.clicked = text0;
          return JSON.stringify(res);
        })()
        """;

    /// <summary>
    /// JavaScript for a link container's page (FileCrypt, after its check): its list of parts as JSON
    /// <c>[{index, name, href}]</c>, one per row that names a file ("Game.part03.rar") and has a button. With
    /// <paramref name="click"/> ≥ 0 it also presses that row's button (which opens the part's link).
    /// </summary>
    public static string BuildParts(int click) => PartsTemplate.Replace("__CLICK__", click.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);

    private const string PartsTemplate = """
        (() => {
          const fileRe = /[^\s\/\\"'<>|:*?]+\.(?:part\d{1,4}\.rar|rar|zip|7z|r\d{2,3}|z\d{2,3}|\d{3}|bin|iso|exe)(?![\w.])/i;
          const button = r => r.querySelector('button, a[href], [onclick], input[type=button], input[type=submit]');
          let rows = [];
          for (const sel of ['tr', 'li', 'div']) {
            rows = [...document.querySelectorAll(sel)].filter(r => {
              const t = (r.innerText || '').trim();
              return t.length > 0 && t.length <= 300 && fileRe.test(t) && !!button(r);
            });
            if (sel === 'div') rows = rows.filter(r => !rows.some(o => o !== r && r.contains(o)));
            if (rows.length) break;
          }
          const parts = rows.map((r, i) => {
            const el = button(r), raw = el.getAttribute && el.getAttribute('href');
            let href = null;
            try { if (raw && !raw.startsWith('#') && !raw.startsWith('javascript')) href = new URL(raw, location.href).href; } catch { }
            return { index: i, name: ((r.innerText || '').match(fileRe) || [''])[0], href };
          });
          const pick = __CLICK__;
          if (pick >= 0 && rows[pick]) { const el = button(rows[pick]); el.scrollIntoView({ block: 'center' }); el.click(); }
          return JSON.stringify(parts);
        })()
        """;

    /// <summary>A URL that is a file rather than a page (followed even when a pop-up leaves the host).</summary>
    public static bool LooksLikeFileUrl(Uri url)
    {
        var path = Uri.UnescapeDataString(url.AbsolutePath);
        return path.Contains("/dl/", StringComparison.OrdinalIgnoreCase) ||
               FileExtensions.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) ||
               VolumeSuffixRegex().IsMatch(path);
    }

    /// <summary>
    /// Is the download the browser is starting the file SeaRaider asked for? Hosts' ad buttons start their own
    /// "downloader.exe"; those, and tiny files (an error page saved as a file), are refused.
    /// </summary>
    /// <param name="expected">The file name SeaRaider wants (null when unknown).</param>
    /// <param name="suggested">The browser's name for the download.</param>
    /// <param name="totalBytes">Size the server announced, if any.</param>
    public static bool IsExpectedFile(string? expected, string suggested, ulong? totalBytes)
    {
        if (totalBytes is < 64 * 1024) return false;
        if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(suggested)) return true;
        if (string.Equals(expected, suggested, StringComparison.OrdinalIgnoreCase)) return true;

        // Same kind of file: same volume suffix (".part3.rar") or same extension.
        var expectedVolume = VolumeSuffixRegex().Match(expected);
        if (expectedVolume.Success)
            return suggested.EndsWith(expectedVolume.Value, StringComparison.OrdinalIgnoreCase);
        return string.Equals(Path.GetExtension(expected), Path.GetExtension(suggested), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A .torrent the browser is about to save (by name, or by the server's type when the name says nothing).</summary>
    public static bool IsTorrentFile(string? suggested, string? mimeType) =>
        (suggested ?? string.Empty).EndsWith(".torrent", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(mimeType, "application/x-bittorrent", StringComparison.OrdinalIgnoreCase);

    /// <summary>A file name Windows accepts.</summary>
    public static string SafeFileName(string name)
    {
        var safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is ':' or '?' or '*' or '"' or '<' or '>' or '|' ? '_' : c)).Trim();
        return safe.Length == 0 ? $"download-{Guid.NewGuid():N}.bin" : safe;
    }
}
