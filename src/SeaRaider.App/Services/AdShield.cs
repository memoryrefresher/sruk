using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using SeaRaider.Core.Scraping;

namespace SeaRaider.App.Services;

/// <summary>Which kind of page a browser shows, and so how hard the shield works on it.</summary>
public enum AdShieldMode
{
    /// <summary>A video player (the Media tab, the hidden player finder): no pop-ups at all, click-catching layers removed, ads skipped.</summary>
    Player,

    /// <summary>A browser game: pop-ups only with a real click and never to an ad, click-catching layers removed.</summary>
    Game,

    /// <summary>The download browser: ad frames and boxes removed. Its pop-ups are handled by the window itself (a host's own buttons open some).</summary>
    Download,
}

/// <summary>
/// What keeps ads out of the pages SeaRaider shows, on top of the host list (<see cref="AdHosts"/>) every browser already uses for
/// its requests and navigations. This is the part that runs inside the page, in every frame of it (a video site's player is a frame):
/// <list type="bullet">
///   <item>pop-ups: <c>window.open</c> opens nothing for an ad, and for a player nothing but a video site's own link;</item>
///   <item>click-catching layers: the invisible full-window box an ad puts over a video so that the first click opens its page;</item>
///   <item>ad boxes: frames to ad networks are removed, with the empty box around them; links to ad networks don't follow;</item>
///   <item>redirects: a page script sending the page to an ad network is cancelled;</item>
///   <item>push-ad sign-ups: notifications are never allowed; YouTube's ads are skipped.</item>
/// </list>
/// </summary>
public static class AdShield
{
    /// <summary>Adds the shield to every page the browser opens from now on (and to every frame in it).</summary>
    public static async Task InstallAsync(CoreWebView2 core, AdShieldMode mode) =>
        await core.AddScriptToExecuteOnDocumentCreatedAsync(ScriptFor(mode)).ConfigureAwait(true);

    private static readonly Dictionary<AdShieldMode, string> Scripts = new();

    /// <summary>The page script for a mode (built once).</summary>
    public static string ScriptFor(AdShieldMode mode)
    {
        lock (Scripts)
        {
            if (!Scripts.TryGetValue(mode, out var script))
                Scripts[mode] = script = Template
                    .Replace("%MODE%", mode.ToString().ToLowerInvariant(), StringComparison.Ordinal)
                    .Replace("%DOMAINS%", JsonSerializer.Serialize(AdHosts.KnownDomains), StringComparison.Ordinal)
                    .Replace("%HINTS%", JsonSerializer.Serialize(AdHosts.NameHintList), StringComparison.Ordinal);
            return script;
        }
    }

    // =============================================================================================
    // Links a page may open outside SeaRaider
    // =============================================================================================

    /// <summary>
    /// Web addresses the author of a browser game may link to (the game's own site, and the well-known places a game links to: a store,
    /// a community, a social page). Anything else a game opens is an ad that got past the host list.
    /// </summary>
    private static readonly string[] SafeSites =
    [
        "steampowered.com", "steamcommunity.com", "itch.io", "github.com", "gitlab.com", "discord.com", "discord.gg", "youtube.com", "youtu.be",
        "twitter.com", "x.com", "bsky.app", "patreon.com", "ko-fi.com", "reddit.com", "facebook.com", "instagram.com", "tiktok.com", "twitch.tv",
        "wikipedia.org", "gog.com", "epicgames.com", "play.google.com", "apps.apple.com", "microsoft.com", "apple.com", "paypal.com", "buymeacoffee.com",
    ];

    /// <summary>May a link a game opened (with a click) go to the default browser? Its own site and the usual places, never an ad.</summary>
    public static bool IsSafeLink(Uri link, Uri? gamePage)
    {
        if (link.Scheme is not ("http" or "https") || AdHosts.IsAdRequest(link)) return false;
        if (gamePage is not null && SiteKey(link.Host) == SiteKey(gamePage.Host)) return true;
        var host = link.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? link.Host[4..] : link.Host;
        return SafeSites.Any(d => host.Equals(d, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + d, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>"cdn.example.com" → "example.com" ("example.co.uk" keeps three parts).</summary>
    internal static string SiteKey(string host)
    {
        var parts = host.ToLowerInvariant().TrimEnd('.').Split('.');
        if (parts.Length <= 2) return string.Join('.', parts);
        var second = parts[^2];
        var take = parts[^1].Length == 2 && second is "co" or "com" or "org" or "net" or "gov" or "ac" ? 3 : 2;
        return string.Join('.', parts[^Math.Min(take, parts.Length)..]);
    }

    // =============================================================================================
    // The page script
    // =============================================================================================

    private const string Template = """
        (() => {
          'use strict';
          if (window.__srShield) return;
          try { Object.defineProperty(window, '__srShield', { value: true }); } catch (e) { window.__srShield = true; }
          const MODE = '%MODE%';
          const DOMAINS = new Set(%DOMAINS%);
          const HINTS = %HINTS%;
          const PREFIX = /^(?:ads?|adv|adserver|pop|banners?|pagead)\./;
          const PATH = /(?:^|\/)(?:pagead|adserver|adsense|adframe|adview|popunder|popads|pop-under|vast|vpaid|prebid|ads)(?:[\/._-]|$)/i;
          const TRUSTED = /^https?:\/\/(?:[a-z0-9-]+\.)*(?:youtube\.com|youtu\.be|twitch\.tv|vimeo\.com|dailymotion\.com|kick\.com)(?:[\/?#:]|$)/i;

          // The same question the app asks of every request (AdHosts.IsAdRequest).
          const isAd = (url) => {
            try {
              const u = new URL(url, location.href);
              if (u.protocol !== 'http:' && u.protocol !== 'https:') return false;
              const h = u.hostname.toLowerCase().replace(/\.$/, '');
              if (PREFIX.test(h)) return true;
              for (const hint of HINTS) if (h.includes(hint)) return true;
              for (let n = h; ;) {
                if (DOMAINS.has(n)) return true;
                const d = n.indexOf('.');
                if (d < 0 || n.indexOf('.', d + 1) < 0) break;
                n = n.slice(d + 1);
              }
              return PATH.test(u.pathname);
            } catch (e) { return false; }
          };

          // ---- pop-ups: window.open opens nothing for an ad (the page's code carries on with a closed window) ----
          if (MODE !== 'download') {
            const real = typeof window.open === 'function' ? window.open.bind(window) : null;
            const closedWindow = () => ({
              closed: true, opener: null, close() {}, focus() {}, blur() {}, postMessage() {}, addEventListener() {}, removeEventListener() {},
              document: { write() {}, writeln() {}, open() {}, close() {} },
              location: { href: 'about:blank', assign() {}, replace() {}, reload() {} }
            });
            const guarded = function (url) {
              let href = '';
              try { href = url == null || url === '' ? '' : new URL(String(url), location.href).href; } catch (e) { href = ''; }
              let ok = href !== '' && !isAd(href);
              if (ok && MODE === 'player') ok = TRUSTED.test(href);          // a player opens nothing but a video site's own page
              if (ok && MODE === 'game') ok = !!(navigator.userActivation && navigator.userActivation.isActive); // a game: only for a real click
              return ok && real ? real.apply(null, arguments) : closedWindow();
            };
            try { Object.defineProperty(window, 'open', { value: guarded, writable: true, configurable: true }); }
            catch (e) { try { window.open = guarded; } catch (e2) { } }
          }

          // ---- push-ad sign-ups ----
          try { if (window.Notification) Notification.requestPermission = () => Promise.resolve('denied'); } catch (e) { }

          // ---- redirects: a script sending the page to an ad network is cancelled ----
          try {
            if (window.navigation && typeof window.navigation.addEventListener === 'function')
              window.navigation.addEventListener('navigate', (e) => {
                if (e.cancelable && e.destination && isAd(e.destination.url)) e.preventDefault();
              });
          } catch (e) { }

          // ---- links to ad networks never follow ----
          try {
            document.addEventListener('click', (e) => {
              const a = e.target && e.target.closest ? e.target.closest('a[href]') : null;
              if (a && isAd(a.href)) { e.preventDefault(); e.stopImmediatePropagation(); }
            }, true);
          } catch (e) { }

          // ---- ad boxes: hidden at once, and the ad frames taken out with the empty box around them ----
          const css = 'ins.adsbygoogle,.adsbygoogle,[id^="google_ads_iframe"],[id^="div-gpt-ad"],[id^="gpt-ad"],' +
            'iframe[src*="doubleclick." i],iframe[src*="googlesyndication." i],iframe[src*="adsterra" i],iframe[src*="exoclick" i],iframe[src*="popads" i],' +
            '[class*="popunder" i],[id*="popunder" i],[class*="pop-under" i],.ima-ad-container,.ytp-ad-overlay-container,.ytp-ad-module,' +
            '.ytp-ad-image-overlay,.ytp-ad-text-overlay,#player-ads{display:none!important}';
          const addStyle = () => {
            try {
              const s = document.createElement('style');
              s.textContent = css;
              (document.head || document.documentElement).appendChild(s);
            } catch (e) { }
          };
          if (document.documentElement) addStyle(); else document.addEventListener('DOMContentLoaded', addStyle, { once: true });

          const dropBox = (el) => {
            let box = el;
            for (let i = 0; i < 4; i++) {
              const p = box.parentElement;
              if (!p || p === document.body || p === document.documentElement || p.children.length !== 1 || (p.textContent || '').trim() !== '') break;
              box = p;
            }
            box.remove();
          };
          const checkFrame = (f) => {
            try { const s = f.getAttribute('src'); if (s && isAd(s)) dropBox(f); } catch (e) { }
          };
          const scan = (node) => {
            try {
              if (!node || node.nodeType !== 1) return;
              if (node.tagName === 'IFRAME') checkFrame(node);
              if (node.querySelectorAll) node.querySelectorAll('iframe[src]').forEach(checkFrame);
            } catch (e) { }
          };
          try {
            new MutationObserver((records) => {
              for (const r of records) {
                if (r.type === 'attributes') { if (r.target.tagName === 'IFRAME') checkFrame(r.target); continue; }
                for (const n of r.addedNodes) scan(n);
              }
            }).observe(document, { childList: true, subtree: true, attributes: true, attributeFilter: ['src'] });
          } catch (e) { }

          // ---- click-catching layers: the empty, see-through box over everything that opens an ad on the first click ----
          if (MODE !== 'download') {
            const clear = (cs) => {
              const bg = cs.backgroundColor;
              return bg === 'transparent' || /^rgba\([^)]*,\s*0(?:\.0+)?\)$/.test(bg) || parseFloat(cs.opacity) < 0.05;
            };
            const isCatcher = (el) => {
              if (!(el instanceof HTMLElement)) return false;
              if (/^(?:VIDEO|CANVAS|IFRAME|BODY|HTML|OBJECT|EMBED|IMG|INPUT|BUTTON|SELECT|TEXTAREA)$/.test(el.tagName)) return false;
              const cs = getComputedStyle(el);
              if (cs.position !== 'fixed' && cs.position !== 'absolute') return false;
              if (cs.display === 'none' || cs.visibility === 'hidden' || cs.pointerEvents === 'none') return false;
              if (!(parseInt(cs.zIndex, 10) >= 1000)) return false;
              const r = el.getBoundingClientRect();
              if (r.width < innerWidth * 0.8 || r.height < innerHeight * 0.8) return false;
              if (el.children.length > 0 || (el.textContent || '').trim() !== '') return false;
              return clear(cs) && cs.backgroundImage === 'none';
            };
            const sweep = () => {
              try {
                const w = innerWidth, h = innerHeight;
                if (w < 200 || h < 150 || !document.elementsFromPoint) return;
                for (const [x, y] of [[w / 2, h / 2], [w * 0.25, h / 2], [w * 0.75, h / 2], [w / 2, h * 0.25], [w / 2, h * 0.75]])
                  for (const el of document.elementsFromPoint(x, y)) if (isCatcher(el)) el.remove();
              } catch (e) { }
            };
            setInterval(sweep, 800);
          }

          // ---- YouTube's own ads (a trailer's or a stream's embedded player): skipped as soon as they can be ----
          if (MODE === 'player' && /(?:^|\.)youtube(?:-nocookie)?\.com$/.test(location.hostname)) {
            setInterval(() => {
              try {
                const skip = document.querySelector('.ytp-ad-skip-button,.ytp-ad-skip-button-modern,.ytp-skip-ad-button,.videoAdUiSkipButton');
                if (skip) skip.click();
                const ad = document.querySelector('.html5-video-player.ad-showing video');
                if (ad && isFinite(ad.duration) && ad.duration > 0 && ad.duration < 180) ad.currentTime = ad.duration;
              } catch (e) { }
            }, 500);
          }
        })();
        """;
}
