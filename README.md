# SeaRaider

A lightweight, dark-themed personal game storefront and library launcher for Windows, built with
**.NET 8 + Avalonia UI 11**. Point it at a website: it searches the site live as you type (or crawls it into an
optional offline index), shows the results as a Steam-style store, and installs with one click — download (HTTP or BitTorrent),
extract, run the setup program if there is one, find the game's `.exe`, clean up, and Play. The same game from
several sites shows up once, with each site and mirror on its store page; downloads wait in a BitTorrent-style queue;
and friends can see what you're playing and chat through a small relay you host yourself.

Use it with sources you have the right to download from.

---

## Build & run

Requirements: Windows 10/11, [.NET 8 SDK](https://dotnet.microsoft.com/download) (or newer), and the
[Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) for the verification
window (preinstalled on Windows 11 and on most up-to-date Windows 10 machines).

```powershell
dotnet restore
dotnet build
dotnet run --project src/SeaRaider.App
dotnet test        # setup engines, stall watchdog, queue & bandwidth, mirrors, search ranking, friends, metadata, crawler …
dotnet run --project src/SeaRaider.Relay   # optional: the friends relay (http://0.0.0.0:5280, WebSocket at /ws)
```

Bake your relay in as the default for everyone who runs your build (see *Friends on by default* below):

```powershell
dotnet publish src/SeaRaider.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:SeaRaiderDefaultRelay=wss://relay.example.com/ws -o publish
```

Publish a single self-contained exe:

```powershell
dotnet publish src/SeaRaider.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
```

**Targeting .NET 9:** change `<TargetFramework>` in `Directory.Build.props` to `net9.0`, and in
`src/SeaRaider.App/SeaRaider.App.csproj` to `net9.0-windows` (the App targets `-windows` because WebView2 is hosted
in a WinForms window).
**Package versions** are pinned centrally in `Directory.Packages.props`; `dotnet list package --outdated` shows newer ones.

> v0.10 was written and statically checked (XAML validity, every compiled binding against its view-model,
> bracket balance, regexes) but **not compiled in the authoring environment**, which had no NuGet access.
> If the first build reports anything, it will most likely be in one of the spots listed under
> *Version-sensitive APIs*.

---

## Unreleased

### Start-up, first run, and speed in the background (`Services/FirstRunDetector`, `Services/BackgroundThrottling`, `ViewModels/OnboardingDialogViewModel`)
- **Opens maximised and fades in**: the window starts maximised on its dark background and its content fades in over about
  a second (instantly when Settings › General › Animations is off). **F11** switches true full screen (no title bar or taskbar);
  **F11** or **Esc** leaves it. To start in true full screen instead, set `WindowState="FullScreen"` in `MainWindow.axaml`.
- **Always starts in the Store.** A brand-new install (no `searaider.db` yet) is asked a few questions over it, in this order:
  1. *Do you have a sources.txt?* - only if no sources are there yet (a `sources.txt` dropped next to SeaRaider.exe is read at
     start-up and counts as already having them, so this question is skipped).
  2. *What should we call you?* - the friends display name (starts as the Windows user name).
  3. **Install games to** and **Temporary downloads**.

  Nothing is saved until **Finish**; closing SeaRaider half-way asks again next start (`AppSettings.FirstRun`: Pending →
  Completed). Installs from before this existed are marked Completed without asking, and *Reset to defaults* keeps it.
- **Sources moved into Settings** as its own tab (Settings › Sources) with everything it had: add a source, search/crawl options,
  site readers, upcoming sites, import/export, the activity log. The rail no longer has a Sources entry. The tab saves as you go,
  so the *Save settings* row is hidden on it. (`SettingsTab` now lists every tab in order, including Updates.)
- **Alt+Tab no longer slows downloads or extraction.** Nothing in SeaRaider paused on focus loss; the slowdown was Windows'
  efficiency mode (EcoQoS) and Chromium's throttling of hidden windows. SeaRaider now opts its process out of power throttling
  (`SetProcessInformation`, at start-up and on every deactivate) and starts the download / verification / page-fetch browsers
  with `--disable-background-timer-throttling --disable-renderer-backgrounding --disable-backgrounding-occluded-windows
  --disable-features=CalculateNativeWinOcclusion`. All three share one WebView2 profile, so they must keep identical options
  (`BackgroundThrottling.CreateWebView2Options`).

### Updates: push new versions to everyone running your build
SeaRaider checks a release file you publish (`latest.json`) at start-up and every 6 hours. When there's a newer version it
shows a banner ("Update and restart"); it downloads the new `SeaRaider.exe`, checks it, swaps it in and restarts. Downloads
in progress continue afterwards. Settings › Updates has "Check now" and an on/off switch.

Releases are **signed**: the app only installs a release whose `latest.json` is signed with your private key and whose exe
matches the signed SHA-256 and size. Someone who takes over your download page can't push their own exe.

**One-time setup**
1. `.\scripts\release.ps1 -NewKey` creates `searaider-update-key.pem` in the repo folder and prints the public key.
   **Keep the .pem secret and backed up** (it's in .gitignore). Lose it and existing installs can't be updated any more;
   leak it and someone else could push updates.
2. In `Directory.Build.props`, fill in:
   - `SeaRaiderUpdatePublicKey`: the printed public key;
   - `SeaRaiderUpdateFeed`: `https://github.com/OWNER/REPO/releases/latest/download/latest.json` (a **public** GitHub repo;
     any https host works, as long as latest.json and the exe are downloadable without signing in).
3. Publish that build and give it to people. Only builds that have the key and feed baked in can update themselves.

**Each release**
1. Raise `<Version>` (or just pass `-Version`; it must be higher than the last release).
2. `.\scripts\release.ps1 -Version 0.11.0 -Repo OWNER/REPO -Notes "What changed" -Upload`
   This publishes the single-file exe, signs `latest.json`, and creates the GitHub release `v0.11.0` with both files (needs
   the GitHub CLI `gh`, signed in). Without `-Upload` it prints what to attach yourself.
3. That's it: every copy offers the update within a few hours, or at its next start.

Updates install only into a published `SeaRaider.exe`; a `dotnet run` build just reports that a new version exists. If
SeaRaider lives in a protected folder (Program Files), Windows asks for administrator rights once while it swaps the exe.
`tools/SeaRaider.ReleaseTool` also has `verify --manifest latest.json --public-key <key> --exe SeaRaider.exe` to check a
release the way the app will.

### Link shorteners and "Click Here" mirrors (Zovo)
- Sites that send every download through a link shortener (zovo.to) now show each "Click Here" as its own mirror, named
  by the text next to it ("Torrent: Click Here", "Gofile – Click Here", a table cell or heading above the button).
  Before, six buttons looked like one download in six files.
- **Settings › Sources › Mirror labels**: type the names the site uses ("Torrent, Gofile, Pixeldrain") to name mirrors exactly, on a
  new source (advanced settings) or an existing one (the *Mirror labels* button on its row).
- Names with *torrent* or *magnet* are torrents: SeaRaider goes through the shortener in the background (wait screen,
  **Visit**, the download page), takes the magnet link or .torrent, and downloads it with its torrent client. Other mirrors
  go wait screen → Visit → **Download File**, then extract and install as usual.

### Teach a reader from a pasted page (`Scraping/SiteReaderTeacher`, Settings › Sources › Site readers › *Teach it from a page*)
- Open a game's page in your browser, **Ctrl+U** (View source), select all, copy, paste into the box and press **Learn from this
  page**. SeaRaider reads that HTML and works out the title (`@owntext` when the title has an info line inside), cover, the
  description block (and the share / related / button blocks to leave out of it), screenshots, the "Developer: …" / "Size: …" /
  "Version: …" lines (or table rows), and the download buttons. Each selector is checked against the pasted page and shown with what
  it reads, so you can see it is right before it is used.
- Paste the site's **list of games** the same way to learn the list: the repeated game cards, their title / picture, and the
  "next page" link. Suggested `browseUrl` / `searchUrl` addresses are shown in the notes and written as comments in the file; they
  are not switched on by themselves (a wrong address would break the site's browsing).
- The reader is saved as `<domain>.json` in your readers folder. If the site already has a reader, the learned selectors replace the
  same fields and every other field stays; the old file is kept as `.bak`. Press **Test** afterwards to see what it finds live.
- Nothing is fetched: only the pasted HTML is read, so it also works for sites that block SeaRaider's own requests. Tests:
  `SiteReaderTeacherTests`.

### Browser fallback for sites that only trust a real browser (`WebChallenge/ChallengeHandler`, `Verification/BrowserPageFetchWindow`)
- After you solve a bot check, SeaRaider retries with the browser's cookies and user agent. If **any** site still answers that retry
  with a bot check (clearance is often tied to the browser's own TLS fingerprint), page requests are now loaded through the
  verified WebView2 session instead and the browser's HTML is used as the response — no more "still blocks SeaRaider after
  verification" for pages. Works for every site; nothing is configured per domain.
- The site is remembered for an hour: its later page requests ask the browser first. The browser window is invisible while it
  works, and shows itself only if the site shows a check again.
- Pages only (GET, not file downloads — those still need the file bytes). Feeds / JSON / sitemaps are read as raw text via an
  in-page fetch. Tests: `BrowserFallbackTests`.

### Coming soon & wishlist (`Catalog/UpcomingService`, `Scraping/UpcomingListParser`)
- **Settings › Sources → Upcoming releases**: add any page that lists upcoming freeware or games (Steam / itch.io lists, a site's
  upcoming page, an RSS feed). Its games appear in the Store under **Coming soon**, with high-resolution art looked up on the
  web (`ArtworkService`). Demos and playtests are left out, and so is any game the store already has. Sites are re-read every 6 hours.
- A tile opens the game's **coming-soon page**, popped out over the blurred window, with **Add to wishlist**.
- **Wishlist** page in the rail. When a wishlisted game turns up in the store (title match, "Demo" ignored; every catalog
  change is checked and waiting games are searched for on your download sites every few hours), a banner says so — also for
  games found while SeaRaider was closed — and the taskbar button flashes if SeaRaider is in the background.

### Feel & sound (`Styles/Motion.axaml`, `Services/SoundService`, `Services/SoundCues`)
- A light, springy feel: buttons press in and spring back, cards lift, pages settle in, banners and toasts drop in, the
  wishlist heart pops — all small moves under ⅓ s with `BackEaseOut`.
- Soft synthesized sounds (no audio files): clicks, toggles, downloads / installs finishing, friend requests, messages,
  wishlist arrivals, failures. Settings → General → Sounds: on/off, volume, each kind, "Play a sample". Throttled so
  nothing repeats or stacks.

### Performance (`Views/SoftImage`, `Services/EffectsService`, `Styles/Lite.axaml`)
- No live blur effects behind scrolling content: the game page's backdrop and the fill behind odd-shaped art are tiny
  thumbnails stretched up (same look, no per-frame cost).
- 256 MB GPU texture budget instead of ~28 MB, so scrolling back over covers doesn't re-upload them.
- The Coming soon shelf is virtualized; sounds are synthesized once in the background and cached per volume.
- Settings → General → **Appearance & performance**: *Animations* (loads/unloads `Motion.axaml` live) and *Blur and
  glow* (off: no window blur behind pop-ups, no shadows or glows — `Lite.axaml`). Style changes only happen when a switch
  actually changes.

### Media tab (`Catalog/MediaService`, `Scraping/MediaListParser`, `ViewModels/MediaViewModel`)
- Add **YouTube channels** (@handle, /channel/, /c/, /user/) and **playlists**, **Twitch channels**, **video RSS / Atom
  feeds**, or **any page with videos** (embedded or linked YouTube / Vimeo, video files). Each shows as a section of boxes
  with the video's picture, title, channel and age; Twitch channels are one box with a red **LIVE** badge and the live
  picture while streaming.
- A box plays in the in-app player (the trailer pop-out): YouTube, Twitch live / past broadcasts / clips, Vimeo, video
  files. YouTube is read through its public video feed (no API key); pages' unnamed YouTube videos get their title from
  YouTube's oEmbed.
- Refreshed in the background (Twitch every 4 min, the rest every 30 min); search across all videos; `[media]` section in
  `sources.txt`.

### Media › Shared with friends (`Social/MediaShareService`, `ViewModels/SharedMediaViewModel`)
- A second half of the Media tab where friends share **links** (YouTube, Twitch, Vimeo, video files, any page), optionally
  **from a time** ("Start at 1:23", or the `?t=` already in the link), with a note. The title, picture and channel are
  looked up (oEmbed / the page's preview tags). Any box on the Channels side has a **share** button (and right-click →
  Share with friends).
- Posts play in the in-app player from the shared time (YouTube `start`, Twitch past broadcasts `time`, Vimeo `#t`,
  video files). **Reactions** (🔥 😂 😮 👍 ❤️ 💀, several per person, hover to see who) and **comments**; times written in
  a comment ("that jump at 2:15") become ▶ chips that play from there. Delete your own post / comment, or anyone's comment
  on your post; hide someone else's post from your feed.
- Travels as ordinary friend messages (`media-post`, `media-react`, `media-comment`, `media-remove`), so **the relay
  needs no update**. The sharer's app is the post's hub: reactions and comments go to it and it passes them on to everyone
  the post went to, so friends who aren't friends with each other still see one thread; each app only accepts post
  changes from the sharer (or, on the sharer's PC, from friends it was shared with). Sends are paced under the relay's
  rate limit, and anything that couldn't be handed to the relay waits in an outbox until it reconnects. Unread count on
  the **Shared with friends** switch; a soft sound when a friend shares, or reacts / comments on your post or a thread
  you're in.

### Watching, Watch along and invites (`Services/WatchTogetherService`, `Social/PresenceMonitor`, `Social/FriendsService`)
- **"Watching: …" status**: whatever plays in SeaRaider's player (Media tab, shared links, store trailers) shows in your
  friends list, in violet, with its position. The player page reports every 2 s (YouTube and Vimeo through their
  postMessage APIs, video files and Steam streams directly); friends only get an update when something changes that they
  couldn't work out (another video, play / pause, a jump of more than 3 s), so the relay sees a handful of messages per
  video, not a stream. A video that's playing keeps you from going Away. Settings › Friends: *Show friends what I'm
  watching*.
- **Watch along**: on a watching friend's row, in their chat header, or from their invite. Opens the same video at their
  point, then follows their player (pause / play, jumps, the next video they open). The player's top bar says
  "Watching along with Sam", with **Stop**; closing the player or picking something else stops following too. Twitch
  live streams just open the stream; Twitch past broadcasts open at their point but aren't steered.
- **Invites** (chat cards; older versions see a text line): **Invite to watch** from the player's top bar; **Invite to
  play** from a game's page (Share or invite a friend → Invite), or from a friend's right-click menu / chat header while
  a game launched from SeaRaider runs. The friend gets **Play** (when it's installed), **Get it** (opens the game in
  their store) or **Watch along**, and **Decline**; the sender sees the answer as a toast and on the card. Invites
  expire after 12 hours. Invites ring like friend requests.
- All of it rides on existing friend messages (presence gains an optional `media` field; new `invite-reply` kind): **no
  relay update**.

### Site readers (`Scraping/SiteReaders.cs`, Settings › Sources › Site readers)
- A **reader** teaches SeaRaider one site's layout as a small JSON file of CSS selectors: where its games are listed
  (`browseUrl`, `searchUrl`, `listing`) and where each part of a game's page is (`page`: title, description, cover,
  screenshots, trailers, genres, tags, developer, version, size, dates, **downloads**). Fields left out fall back to
  the generic reader. `selector@attribute` reads an attribute, `a || b` tries in order; pictures and links find their
  address by themselves.
- Files live in `%LOCALAPPDATA%\SeaRaider\readers` (yours) and `readers\` next to SeaRaider.exe (shipped); a README and
  a commented sample are written there. **New reader** starts a file for a domain and opens it; **Test** fetches a page
  and shows what the reader found (title, cover, facts, description, download options, listed games) and which
  selectors matched nothing. **Reload** picks up edits.
- Used for that site's search, browsing and game pages, like the built-in itch.io reader.

### Setup files: try first, then really delete (`Installing/InstallerService`, `MainWindowViewModel`)
- After a setup installs a game: "*Hades II is installed. Try it first!*" → **Play now** / Delete now / Keep them. When
  the game is closed: "*Did Hades II work?*" → **It works, delete them** / Not yet / Keep them.
- Deleting closes setup helpers still running from the folder, clears read-only flags on files *and* folders, retries
  for ~9 s, and then checks the folder is gone; if not, the banner says what's left and why, with **Open folder** /
  **Try again** (before, failures were silent and the folder stayed).
- Uninstalling a game, and removing a setup that wasn't installed, delete its setup folder too.
- Leftover setup folders (no game needs them) are cleared quietly a few seconds after start-up; Settings › Setup shows
  any that remain, with **Delete leftovers**. Folders of games waiting to install, being downloaded, or installed and not
  yet tried are never touched. Library rows of installed games with setup files still on disk get **Delete setup files**.

### Errors from unfamiliar sites
- Every game record is normalized when read and saved (missing lists, links without an address or host), which is what
  made some sites' games fail with "Object reference not set to an instance of an object".
- Unexpected errors now go to `%LOCALAPPDATA%\SeaRaider\logs\errors.log` with the full details, and the banner says so.

### Sources file, import / export (`Catalog/SourcesFile`, `Services/SourcesFileService`)
- `sources.txt` next to SeaRaider.exe: one address per line, `#` comments, an optional `[upcoming]` section for Coming
  soon sites. Read at start-up before anything else (first start: everything in it is added; later: lines added or
  removed while SeaRaider was closed are applied), and rewritten whenever sources change. Falls back to the data folder
  if the install folder isn't writable.
- An `[upcoming]` site can carry settings on indented lines under its address: `start:` / `end:` (a custom list; `\n` is a
  line break inside the HTML) and `enabled: no` (switched off; each upcoming site has an on/off switch on the Sources tab).
- Setup files are only deleted after a setup that verifiably finished (game folder has files and its .exe exists); an
  incomplete install keeps them and is never offered for clean-up.
- The pre-download dialog shows the disk space the whole download → unpack → setup needs at its busiest moment
  (`Downloads/SpaceEstimator`) and warns when a drive is short.
- Settings › Sources: **Import sources…**, **Export sources…**, **Show sources.txt**.

### Share a game with a friend
- Store page → **Share with a friend** → pick a friend (online first). It arrives in their chat as a card with the
  picture, name, site and size, and **Open in store**: their store opens the same page, or the same game from one of
  their sites, or searches every source for it. Older versions see "Shared a game: … <link>".

### Other changes
- Coming soon: Steam listings get Steam's own art and details (app-details API; new apps keep their pictures at
  hashed addresses that can't be guessed). Demos are looked up as their game everywhere, and web wallpapers must name the
  game (stops unrelated key art on demo cards).
- Store page: one "More download options" drop-down with every host, plus Download & Install.
- Uninstall waits for the game's uninstaller *and the copy it hands over to* (Inno/NSIS), brings its window to the front,
  offers "Stop waiting and delete the files", and rows left on "Uninstalling" by a closed app are repaired.
- Repack setups: the music is also switched off with the setup's own speaker button (`Installing/SetupMusicToggle`, checked
  with the volume mixer's level meter), and "Limit installer to 2 GB of RAM usage" is unticked (`SetupOptionTweaker`).
- Settings: **Reset to defaults** (keeps the library folder, hidden games and wishlist).

## What's new in v0.10

### 1. A cleaner look
- **No more grey boxes.** The "Download options" panel on a game page, "Advanced options" in the install dialog and
  "Details" on a download are now plain disclosure rows: a title, a one-line summary and a chevron, on a thin outlined
  card. The black and grey Expander panels are gone.
- **Download rows** were rebuilt: the percentage sits next to the title, step chips (Download → Unpack → Install), one
  headline sentence, one progress bar. Reorder arrows show only with advanced options on. "Remove" is now **Clear**
  (it only clears the row; an installed game stays installed). The ⋯ button is a real three-dot icon.
- **Professional titles and descriptions** (`Catalog/DisplayText`). The release notes are taken out of the name:
  *"God of War III Remastered – v1.0.1 (shadPS4 v0.9) + 2 DLCs [FitGirl Repack]"* shows as
  **God of War III Remastered**, on cards, the game page, downloads, the library and the download bar. The version,
  DLCs and size are still shown as tags. Real names like *Mario + Rabbids* or *Grand Theft Auto V* are left alone.
  With advanced options on, the original name is shown small under the title.
  Descriptions drop the info dump ("Genres/Tags:", "Repack Size:" …), links and "Read more", and a cut-off teaser
  ends with "…".

### 2. Progress from the first second
**The bug:** a download of a multi-part game started with no percentage and a pulsing bar until about 35 %, because
the total size was only known once every part had been asked for its size.

**Now:** the total starts from the size the game page gives. As soon as the first part answers, it's checked against
*part size × number of parts* and corrected if the page was far off. When the last part is done, the real total is used.

### 3. "We saved you some space!"
A multi-part game unpacked while downloading deletes each part once it's unpacked, so a finished download could show
"4.06 of 5.86 GB" and look incomplete. It isn't: that number was the running estimate. The finished row now uses the
real size and says *"Bonus: we saved you 1.8 GB of disk space by unpacking as we went."* (or *"We tidied up … of
download files for you."* after a normal unpack).

### 4. Sharper featured game
When neither Steam nor the game page has a picture at least 1600 px wide, SeaRaider now also looks for **official key
art wallpapers** (`Catalog/WallpaperSearch`, wide wallpaper-size images only) and the trailer's frame, measures them
all and keeps the sharpest. Settings field: `UseWebArtwork` (on).

### 5. Repack setups: no music, no pop-ups, no web page (`Installing/SetupRunner`)
Repack setups run as administrator, and a normal program can't reliably mute or close an administrator's program.
That's why the music sometimes kept playing. Repack setups now run through **SeaRaider's setup helper**: SeaRaider
started again as administrator (`SeaRaider.exe --setup-runner …`). It's still the single Windows prompt the setup
asks for anyway. The helper starts the setup itself and watches everything it starts:
- **Music:** muted within a quarter of a second of starting. Each audio session is muted once, so if you unmute it in
  the volume mixer it stays unmuted.
- **DirectX, Visual C++, .NET, PhysX, OpenAL, Unreal prerequisites** and anything else from a `_Redist` folder
  (`Installing/RedistRules`): closed the moment they start, so their wizards never pop up. Then, by the setting
  *Settings → Setup programs → DirectX, Visual C++ and other runtimes*:
  - **Install them quietly** (default): the ones with a known quiet mode run silently after the setup has finished.
    Unknown ones are skipped.
  - **Skip them**: none run. Most PCs already have them; install them later only if a game won't start.
  - **Show their installers**: the old behaviour.
- **The web page at the end:** a browser the setup starts is closed (*Close the web page a repack setup opens when it
  finishes*, on by default). *Best effort:* if your browser is already open, Windows can hand the address to it before
  the helper sees anything. This closes the browser process the setup itself starts, which is the usual case.

What the helper did ("DirectX: closed its window, will install it quietly", "Closed the web page …") is in SeaRaider's
log. Its exit code is the setup's, so failures are reported as before. Non-repack setups run as before.

### 6. Delete the setup files? (`InstallerService.SetupCleanupOffered`)
Once a game's setup has installed it (repack setups check their files as they install), a banner asks:
*"God of War III Remastered is installed. Its setup files (38.2 GB) aren't needed to play. Delete them to free up the
space?"* with **Delete setup files** and **Keep them**. Several finished games queue up. A question left unanswered
comes back at the next start; **Keep them** stops asking for that game. *Settings → Setup programs → After a game is
installed*: Ask (default) / Delete / Keep. This replaces the old *Delete the setup files after a successful install*
checkbox. Only folders inside SeaRaider's own setup area are ever deleted, never the game.

---

## What's new in v0.9

### 1. Every part of a multi-part download

**The bug:** some sites link a host's parts without file names ("Part 1 … Part 6"). SeaRaider treated such links as
copies of one file, downloaded Part 1, unpacked what it could and called the game installed.

**Now:**
- Part numbers are read from the link text ("Part 3", "Pt. 03", "3/6", "3 of 6", or just "3"). The parts form a
  set, the set is planned complete, and the downloaded files are named by the server.
- Unnamed links on one host are never dropped.
- **Safety net:** an archive with a missing part is refused, with a clear message, instead of being installed half.

**A game already installed from part 1 alone** (Castlevania in the bug report): uninstall it in the Library, then
press Download & Install again.

### 2. Sharp pictures for every game

**Banners** (featured game, game page, backdrop) are picked from several candidates, and each one is **measured**
(only its first 256 KB is read). The first landscape picture at least 1600 px wide wins, else the largest.
Candidates, best first:
1. Steam's library hero;
2. Steam screenshots (1920×1080);
3. the game page's screenshots **at full size**;
4. the official YouTube trailer's 1280×720 frame;
5. Steam's page background;
6. the page's cover.

Full-size screenshots come from turning the site's thumbnail address into the original (`ImageQuality`):
riotpixels `….jpg.240p.jpg`, WordPress `-300x169.jpg`, Steam `ss_….600x338.jpg`, imgur `abc1234m.jpg`, imageban
`/thumbs/`, `?resize=` queries.

**Covers** are kept even when the image's description says "logo". That had left covers like College Football 27's
blank. Games with no cover at all get Steam's banner, the page's own picture, or the trailer frame.

**Games Steam doesn't sell** (console ports, EA titles …) get their **official YouTube trailer**, found without an API
key. The video's title must name the game and say "trailer"; reactions, reviews and soundtracks are refused. It plays in
SeaRaider's player, and its frame serves as the banner. Settings field: `UseYouTubeTrailers`.

**Retries:** pictures are looked up again after a day when nothing was found. A lookup that failed because a site
was unreachable isn't remembered at all. Pictures saved by v0.8 are looked up again once.

On the game page, screenshots are shown at full size too.

---

## What's new in v0.8

### 1. Sharp banners, and a picture for every game (`Catalog/ArtworkService`)

- **Large banners.** The featured banner, the top of a game page and the window backdrop use high-resolution art. A
  repack site's cover is a few hundred pixels wide and turned to blocks when stretched. Where the art comes from,
  in order:
  1. Steam's library hero (1920 px and up);
  2. a 1920×1080 screenshot;
  3. Steam's page background.
- **Games without a cover** (Forza Horizon 6, College Football 27 …) get Steam's banner. Steam results are matched
  carefully: "EA SPORTS College Football 27" counts for "College Football 27", but soundtracks, DLC, demos and
  companion apps never do. A game that isn't on Steam gets a screenshot from its own page instead.
- **Lookup limits.** Art is looked up once per game and stored; misses are retried after a week. At most two
  lookups run at a time.

### 2. Trailers

- **No more music videos.** The YouTube links were taken from the site's sidebar and footer, which is on every page.
  Now only videos embedded in the post itself count. Trailers come from Steam (matched by title) whenever Steam has the
  game. Pages saved by older versions are checked again once.
- **Everything plays inside SeaRaider.**
  - YouTube trailers play in SeaRaider's trailer window. Only when WebView2 is missing do they open in your browser.
  - Steam trailers play as **DASH** with the bundled dash.js 4.7.4 (BSD-3-Clause), the way Steam's own store player
    does. Video and sound are separate tracks, which is why some played silent, 7-second previews before. HLS
    (hls.js) is the fallback.

### 3. Look

- In The Next Generation, the sidebar is transparent and floats on the backdrop.
- A card's **×** only appears while the pointer is over that card.

### 4. Downloading, made simple (with everything still there for experts)

- **Quiet.** File-host pages open in SeaRaider's browser *off-screen*, with no taskbar button. The window only comes
  forward when you're needed: a captcha, or no download after 30 seconds. Countdowns run out on their own.
- **Clear.**
  - Every download shows three steps — **Download → Unpack → Install / Ready to play** — and one plain sentence:
    "Downloading… 42% · about 12m left", "Unpacking the game files… 60%", "Ready to play! You'll find it in your
    Library."
  - Parts, hosts, speeds, peers and limits are under **Details**.
- **One click.**
  - The download dialog explains the three steps and asks only for the folder name. File names and setup switches
    are under **Advanced options**.
  - On a game page the source picker is folded into **Download options** ("Automatic: SeaRaider picks the best
    source and switches if one fails").
  - Setup programs now start by themselves when the download is ready (new default).
- **Settings → Show advanced options** (top of the Settings page) turns the expert mode on everywhere:
  - the Downloads, Setup programs and Torrents tabs, the temporary folder, IGDB keys and re-crawl days;
  - the source picker opened on game pages;
  - Advanced options opened in the download dialog;
  - Details opened on downloads.

---

## What's new in v0.7

### 1. Skins: "The Next Generation" (new default) and "Legacy"

Settings → General → **Skin**. The change applies as soon as you pick one; no restart.

- **The Next Generation** — glass panels over a blurred backdrop of the featured game (or of the game page you're
  on), with neon cyan for actions and amber for what you own.
  - The featured game gets a framed, glowing banner with its title in **Orbitron** (bundled, SIL Open Font License).
  - Glowing navigation and gradient buttons.
  - Rounded glass cards that light up on hover.
- **Legacy** — the original night-water look, unchanged.

How it works:
- `Styles/Theme.axaml` is Legacy and is always loaded.
- `Styles/NextGeneration.axaml` is layered on top while that skin is selected (`Services/SkinService`). It replaces
  the palette, which views read as `DynamicResource`, and adds gradients, glow and the display font.
- The backdrop is blurred once with Skia into a small bitmap (`Services/BackdropService`), so scrolling never
  re-blurs it.
- Parts only one skin shows use the classes `ngen-only`, `hero-ngen`, `hero-legacy` and `backdrop`.

### 2. Posts that aren't games

- **Automatic:** site posts like FitGirl's *Updates Digest*, *Upcoming Repacks*, donation calls, announcements, FAQs
  and "beware of fake sites" warnings are hidden from the store (`Catalog/NonGameFilter`; on by default in Settings →
  General → Store). The rules match how sites title these posts, so games like *News Tower* stay.
- **By hand:** every store card has a translucent **×** in its top right corner that hides it. An **Undo** bar appears
  for a few seconds, and Settings → General → Store lists how many are hidden, with **Show them again**.
- Hidden games are remembered per game (all sources' copies at once), so they stay hidden after a refresh.

---

## What's new in v0.6

### 1. File hosts download in SeaRaider's own browser (`Downloads/BrowserDownloads`, `App/Verification/BrowserDownloadWindow`)

v0.5 got past a host's captcha in the verification window and then handed the file link to SeaRaider's HTTP client.
That broke in three ways, all seen in the bug report:

- **FuckingFast**: Cloudflare checks the browser's TLS fingerprint, which cookies can't carry over. That gave
  "still blocks SeaRaider after verification", or a stuck "Verification complete, continuing…": the one-time `/dl/` link
  had already been used by the window.
- **DataNodes / FileKeeper "File not found"**: the window opened the address the host *redirected SeaRaider's HTTP
  client to*, not the file page itself. Fixed (`ChallengeHandler` now keeps the requested address), and links are
  opened with the game page as Referer.
- **FileKeeper wasn't a known host**, so it counted as a direct link. It was also picked first by Automatic, and its
  page was "downloaded" as if it were the file.

Now hosts that need a browser (DataNodes, FileKeeper, Gofile …, and any host that answers SeaRaider with a page or
Cloudflare check) download **inside a SeaRaider browser window, by the browser itself**:

- The page opens, SeaRaider clicks the download button (or follows FuckingFast's direct `/dl/` link, skipping the
  "first click opens ads" step), ad pop-ups are blocked, and the file is saved straight into the download folder.
- The window shows without stealing focus and hides once the download starts. It only comes forward (with a banner
  in the main window) when a captcha is waiting for you, or no download button was found after 8 seconds.
- Downloads that pause or stop resume where the browser can resume them. Hosts that work over plain HTTP (Pixeldrain,
  Buzzheavier, MediaFire, and FuckingFast when Cloudflare lets it) still use the resumable HTTP downloader.
- Turn off the clicking in Settings → Downloads → *File hosts*.

### 2. Dead links switch hosts by themselves

A 404/410, or a host page saying *File not found / expired / deleted*, marks that copy dead. SeaRaider then fetches
**the same part from the next host** (same volume of the same set, or the same file name). Only if every host has lost
it does it switch to the page's **torrent**. The row in the Download Manager says what happened ("DataNodes lost the
file. Trying FileKeeper…").

### 3. Low disk space mode: unpack while downloading (`Extraction/VolumeFeed`, `ArchiveExtractor.ExtractVolumesAsync`)

For multi-part RAR downloads (`game.part1.rar …`, or `game.rar` + `.r00 …`):

- each part is unpacked as soon as it arrives, and **deleted once unpacked**;
- the download stays at most two parts ahead of the unpacking.

A game then needs roughly its unpacked size plus two parts, not archive *plus* game. A 70 GB download fits on
100 GB free, as long as the unpacked game fits.

- **Automatic** (default) turns it on when the drive can't hold archive and game at once (sizes from the page, e.g.
  "14.9 GB"). **Always** and **Never** are in Settings → Downloads.
- **Pause, crash, retry**: unpacked files stay. SeaRaider continues at the part where the unfinished file started,
  re-downloading that one part if it was already deleted. Solid RAR archives can't start in the middle, so they
  start over.
- **Not possible for** 7z (its index is at the end), single archives, `.001` splits and torrents. Those download
  first, as before.
- **Repacks** (FitGirl's `setup.exe` + `.bin`) are installers, not archives. When they come as RAR parts, the parts
  stream; the installer still needs its files, and then the installed game.

### 4. Setup music muted

Repack installers play music. SeaRaider now mutes the setup's own processes in the Windows volume mixer while they
run (Core Audio sessions; `Installing/Native/AudioSessionMuter`). Nothing else is muted. Turn it off in
Settings → Setup programs; a muted setup can also be unmuted in the volume mixer.

### 5. Full trailers

Since 2025, Steam's store API lists trailers only as HLS/DASH streams. The only short files are the 6–7 second
silent "microtrailer" previews, which is what played. Now:

- trailers are the real streams, in trailer order (launch/announce trailers first, spoiler videos and clips last);
- they play in SeaRaider's **trailer window**, using bundled [hls.js](https://github.com/video-dev/hls.js) 1.5.20
  (Apache-2.0, `src/SeaRaider.App/Assets/player/LICENSE-hls.js.txt`);
- without WebView2, the same player page opens in your browser, with a "Watch it on the Steam store" link if the
  stream can't load;
- games whose media came from Steam under the old rules are refreshed once when opened.

---

## What's new in v0.5

### 1. One clean "Download from" list (`Scraping/DownloadLinkDeduplicator`)

Repeated links are removed before anything is shown or planned:

- **Same magnet**: compared by info-hash (hex or base32), so the same torrent from two trackers is one entry.
- **Same URL**: `www.`, trailing slashes, `utm_*` / `ref` / `fbclid` and fragments don't make a link "different".
  File hosts are compared by their file id (FitGirl's `#name` fragment is only a label).
- **Same file on the same host** under two ids is one file.
- **Tracker pages** (1337x, RuTor, …) are dropped when the page also has a magnet or `.torrent`.

Known hosts are recognised even when their URL shape changes, so DataNodes no longer shows up as "Direct download".
What's left is grouped by kind, each option once with a tag:

| Section | Tag examples |
|---|---|
| File hosts & direct downloads | `File host`, `Direct download`, `5 files` |
| Multi-part archives | `5-part archive` |
| Torrents | `Torrent` |
| Opens in your browser | `Opens in browser` (MEGA, tracker pages) |

Picking a host downloads **all** of that host's files (a FitGirl host with `setup.exe` + `fg-01.bin` …), and picking a
multi-part archive completes missing parts from other hosts with the same set. Merged store pages (several sites) keep
their source tabs; the list above is per source.

### 2. Crash-safe downloads (`Downloads/DownloadAndExtractManager`)

- Running jobs are written to the database every **5 seconds** (progress, phase, checkpoint time), not only on state changes.
- On start, jobs that were *Downloading* or *Extracting* resume: HTTP continues from the `.part` file with
  `Range: bytes=N-` and an `If-Range` validator (strong ETag or Last-Modified, saved in `.part.meta`), so a file that
  changed on the server restarts instead of being corrupted. Torrents use MonoTorrent **fast-resume** (written
  periodically while downloading), so no full re-hash after a crash. Extraction restarts from the downloaded archive.
- Games that were **seeding** when SeaRaider closed start seeding again.

### 3. Real screenshots only (`Scraping/MediaScraperService`)

- Images are rejected by URL, `alt` and `class` keywords (stats, seeders, peers, banner, logo, button, filehost,
  icon, 1x1, donate, …) and, when the page gives sizes, by shape (smaller than 200×110, or not between 1:1 and 2.6:1).
  This removes FitGirl's "Seeders / Peers" banner. Pages saved by older versions are re-checked once.
- When fewer than 2 clean screenshots and no trailer remain, SeaRaider looks the title up on the **Steam store**
  (no key needed) and, if you add keys, **IGDB**. Only a confident title match is used ("Hades" never gets
  "Hades II"'s screenshots). The page shows where the media came from.
- Settings → General → *Screenshots and trailers*: turn Steam off, or add an IGDB (Twitch) client id + secret.

### 4. Verification window: "The system cannot find the file specified (0x80070002)"

That error means the WebView2 Runtime is missing, half-removed, or can't start from its profile folder.
The window now recovers by itself, in order:

1. a **fixed-version runtime**, if you put one in `WebView2Runtime\` next to SeaRaider.exe or in
   `%LOCALAPPDATA%\SeaRaider\webview2-runtime\`;
2. the installed **Evergreen** runtime;
3. a **fresh profile folder** (a damaged profile gives the same error);
4. **installing / repairing** the Evergreen runtime with Microsoft's bootstrapper (silent, per-user; turn off in
   Settings → General → *Install the WebView2 Runtime automatically*);
5. if all of that fails, a **"Continue in your browser"** window: the page opens in your own browser, and the download
   link you copy there (SeaRaider watches the clipboard) is handed back to the download.

Cookies from the built-in window are still passed to SeaRaider's HttpClient as before. Every failure is logged to
`%LOCALAPPDATA%\SeaRaider\logs\webview2.log` (runtime version, fixed runtime, OS, 64-bit, elevated).

### 5. Stop seeding

A **Stop seeding** button on the game page (next to the seeding ratio) and in the download manager. It stops the
torrent within 5 seconds, releases the files, removes the temporary torrent data, and records it, so it doesn't start
seeding again on the next launch.

### 6. Friends on by default, with an off switch

- Settings → Friends → **Enable Friends System & Social Overlay**. Off means *off*: the relay connection is closed, the
  idle-check timer stops, the friends button and overlay disappear, and nothing is polled.
- When on, SeaRaider connects without setup to the **default relay**. The default comes from the build
  (`-p:SeaRaiderDefaultRelay=wss://…`, or `<SeaRaiderDefaultRelay>` in `Directory.Build.props`) or the
  `SEARAIDER_RELAY` environment variable. A relay typed in Settings overrides it. Deploy `src/SeaRaider.Relay`
  (a Dockerfile is included) and point the default at it.

### Fixes

- **MSB3277 "conflicts between different versions of WindowsBase"**: the WebView2 package also ships a WPF control that
  references WPF's `WindowsBase`; SeaRaider uses the WinForms control, so the App project now drops the WPF assembly
  before references are resolved. The warning is gone and nothing WPF is copied to the output.
- File names with characters Windows doesn't allow (`:` `?` in FitGirl titles) are kept with `_` instead of being lost.

---

## What's new in v0.4

### 1. Setup programs that don't freeze in the background (`Installing/SilentInstallManager`)
The v0.3 freeze, a setup playing its music in the background while it waits on a step, came from two things. The
setup's window opened behind SeaRaider, because Windows doesn't let background processes take the foreground. Also,
repacks' custom pages show message boxes that `/SUPPRESSMSGBOXES` can't silence, so under `/VERYSILENT` they waited
on an invisible question.
- **Engine detection from the binary** (`SetupEngineDetector`): PE section names, loader window classes, embedded
  headers and UTF-16 version-info strings in the first 6 MB and last 2 MB. Recognised: Inno Setup (with version),
  NSIS, Windows Installer (.msi / OLE header), WiX Burn, InstallShield, Advanced Installer, Wise, Setup Factory and 7-Zip
  SFX. Files next to the setup (`ISDone.dll`, `unarc.dll`, `fg-*.bin` …) mark it as a **repack** (FitGirl, DODI,
  ElAmigos …).
- **Switches per engine and mode**:

  | Engine | Silent | Silent with progress window |
  |---|---|---|
  | Inno Setup | `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /DIR=` | `/SILENT /SUPPRESSMSGBOXES /NORESTART /SP- /DIR=` |
  | NSIS | `/S /D=` | `/S /D=` |
  | MSI | `msiexec /i … /qn /norestart INSTALLDIR=` | `/qb!` |
  | WiX Burn | `/quiet /norestart` | `/passive /norestart` |
  | InstallShield | `/s /v"/qn INSTALLDIR=…"` | `/s /v"/qb …"` |
  | Advanced Installer | `/exenoui /qn APPDIR=` | `/exebasicui /qb! APPDIR=` |
  | Wise / Setup Factory | `/s` / `/S` | same |

  Repacks always use the *with progress window* column, so any question they ask is visible.
- **Whole process tree watched** (`ProcessTreeTracker`): Inno's real setup runs as `%TEMP%\is-*.tmp\setup.tmp`,
  repacks run unarc helpers, and bootstrappers start an .msi. Children are followed by parent process id, and the
  install only counts as finished when all of them have exited.
- **Stall fallback** (`StallWatchdog` + `WindowSurfacer`): if the tree uses under 5 % of one core and does under
  0.5 MB/s of disk I/O for **15 s** (configurable), the setup is waiting for something.
  - Its windows are restored and brought to the front, working around the foreground lock and flashing the taskbar
    when that's not allowed. For silent runs, hidden wizard and dialog windows are revealed.
  - The Library row turns brass with **Show setup window** and **Restart with wizard**.
  - If a silent run is stalled with no window for another 30 s, and hasn't written anything to the game folder, it's
    closed and started again with its normal wizard. That game then remembers to use its wizard.

### 2. Download queue, slots and bandwidth (`Downloads/DownloadQueueManager`)
- **Settings › Downloads**:
  - maximum active downloads, and how many of them may be torrents;
  - how many games extract at once;
  - an overall download limit (KB/s), split between HTTP and the torrent engine by how many of each are running;
  - an overall torrent upload limit.
- **Settings › Torrents**: optional seeding after a torrent finishes, with a limit on seeds at once, a ratio limit and
  a time limit. Seeding uses no download slot, and the files are deleted when it stops.
- **Download Manager**:
  - a numbered queue with Move up / Move down arrows;
  - a menu per row with Move to top / bottom, **Force start** (ignores the limits), a per-download speed limit
    (download, plus upload for torrents) and Cancel;
  - Pause all / Resume all; finished jobs in their own list.
- **Slots free early**: a game gives its download slot back as soon as its transfer ends, so the next one starts
  while it extracts.
- **Live changes**: all limits apply immediately, with no restart. HTTP is limited with a token bucket
  (`BandwidthLimiter`, reading in ¼-second chunks when limited), and torrents through MonoTorrent's engine and
  per-torrent rate settings.

### 3. SteamRIP-style sources, mirrors and merged store pages
- **File hosts** (`Scraping/FileHosts`) are recognised as mirrors even though their links have no file extension.
  Hosts include Buzzheavier, Pixeldrain, FuckingFast, MediaFire, Gofile, DataNodes, DDownload, 1fichier, MegaDB,
  MultiUp, FileCrypt, Qiwi, Rapidgator, KrakenFiles, MEGA and Google Drive.
  - FitGirl-style part names in the link fragment (`#Game.part01.rar`) still form multipart sets, one set per mirror.
  - Sizes printed next to links are kept.
- **Hands-free hosts** (`Downloads/FileHostResolver`): Pixeldrain (file API), Buzzheavier (its `hx-redirect` download
  call), FuckingFast and MediaFire (direct link in the page) are turned into the real file link automatically.
  Other hosts open in the verification window: click the host's button and SeaRaider takes over the download. MEGA
  decrypts in the browser, so it opens there.
- **Automatic mirror choice** (`DownloadPlanner`), in order: plain files, then hosts SeaRaider resolves itself, then
  the torrent, then hosts that need a click.
- **Known sites** (`Scraping/SiteProfiles`): FitGirl Repacks, SteamRIP, DODI, ElAmigos, KaOsKrew and GOG Games get
  proper names, and their FAQ, digest and request pages are kept out of the store.
- **Duplicates merged** (`Catalog/GameDeduplicator`): one card per game, keyed by `TitleNormalizer.DedupKey`.
  - The key ignores release tags, editions ("Deluxe", "GOTY"), a leading "The", Roman numerals vs digits and
    "(2019)"-style years. "Hades" and "Hades II" stay separate.
  - Cards show which sites have the game.
  - The store page has a tab per source with its version and size. Under the selected tab is a mirror list (Torrent,
    Buzzheavier, DataNodes …) with size, part count and what to expect.

### 4. Search precision (`Catalog/SearchScorer`, `Catalog/TitleNormalizer`)
- **Scoring**: each result gets a relevance score from 0 to 1.
  - Exact title = 1. Then, in order: titles that start with the query, titles containing it as a phrase, and titles
    with every word in any order.
  - Typos are tolerated (Levenshtein per word, Dice coefficient overall), and acronyms work ("gta 5" finds Grand Theft
    Auto V).
  - Numbers must match: "resident evil 2" won't list part 3.
  - `"Quoted phrases"` are mandatory.
- **Noise dropped**: results scoring under 0.45 are hidden (for example, a news post that mentions the word).
  *Best match* sorts by score.
- **What the sites are asked**: release tags are stripped from the query ("elden ring v1.10 fitgirl" → "Elden
  Ring"). WordPress sites search titles only (`search_columns=post_title`), with full-text search as a fallback.
- **Speed**: normalized titles are cached in the index, so scoring tens of thousands of games is instant.

### 5. Friends & chat (`Core/Social`, `App/Views/FriendsListOverlay.axaml`, `src/SeaRaider.Relay`)
- **The panel**: a Steam-overlay-style side panel. Open it with **Friends** in the left rail or **Ctrl+Shift+F**.
  - Your name, status and **friend code** (`SR-XXXXX-XXXXX`).
  - Friend requests (send by code, Accept / Decline), and the friend list with live presence.
  - One-to-one chat with **animated emotes** (`:gg:` `:wave:` `:fire:` … bounce, wobble, pulse, spin, float or
    shake; emote-only messages show large), delivery state, and retry when offline.
  - A toast for new messages while the panel is closed.
- **Presence** (`PresenceMonitor`):
  - **In-Game: title** while a game launched from SeaRaider runs (uses the launcher's process tracking);
  - **Away** after 10 minutes without input, or when chosen;
  - **Appear offline**, where the relay stops announcing you;
  - **Online** otherwise.
  - Sharing the game title can be turned off.
- **Relay** (`src/SeaRaider.Relay`): a small ASP.NET Core WebSocket server with no database.
  - The first sign-in with a friend code claims it; the relay stores only a SHA-256 of the secret.
  - It announces online/offline only between *mutual* friends, and only friends can message you (enforced by the
    clients).
  - It queues messages for offline friends (in memory) and rate-limits senders. A `Dockerfile` is included.
- **Setup**:
  1. Run `dotnet run --project src/SeaRaider.Relay` on an always-on machine. Put it behind a TLS proxy (Caddy, nginx,
     Cloudflare Tunnel) for `wss://`, or use `ws://<lan-ip>:5280/ws` at home.
  2. Everyone enters the same address in **Settings › Friends**.

---

## What's new in v0.3

### 1. Live search and lazy loading (`Catalog/LiveSearchService`)
- Adding a source no longer crawls the whole site. The Store asks each source for results **as you type**
  (400 ms debounce), all sources in parallel, and shows each site's batch as soon as it arrives.
- Per source, *Search with* picks how: **Auto** (detects a WordPress REST API at `/wp-json/wp/v2/posts`, else uses the
  site's own search page `/?s=`), **WordPress API**, **Site search page**, or a **Custom** URL template
  (`https://site/search/{query}/page/{page}`).
- With an empty search box the Store shows each site's newest uploads. Scrolling near the bottom loads the next page
  (cached results first, then the next live page).
- Results are lightweight `CatalogEntry` cards kept in memory (`CatalogIndexService`) and in LiteDB. Full details
  (description, download links, every metadata field) are fetched only when you open a game or press Get
  (`GameDetailsService`), then cached.
- *Build offline index* on a source still runs the v0.2 crawler if you want every game searchable offline.

### 2. Sorting
The Store's sort menu: **Best match**, **Upload date (newest / oldest)**, **Release date (newest / oldest)**, **Title**.
Entries without the date being sorted on always go last. Logic in `Models/CatalogEntry.cs` → `CatalogSorting`.

### 3. Structured metadata (`Scraping/MetadataParser`)
- Titles are cleaned: `Elden Ring: Deluxe Edition (v1.10.1 + DLC, MULTi14) [FitGirl Repack]` becomes
  **Elden Ring: Deluxe Edition**, with version *1.10.1*, *DLC included* and *14 languages* moved into fields.
- Info blocks (`Genres/Tags:`, `Release Date:`, `Repack Size:`, `Languages:` …, including several labels on one line,
  tables and definition lists) are mapped to **Genre, Release date, Upload date, Version, DLC, Languages, Repack size,
  Original size, Developer, Publisher**. Upload dates also come from `article:published_time`, `<time>` and JSON-LD.
- Descriptions keep only the "About the game" prose, not the info dump.
- The store page shows them as info pills; cards show genre, version, DLC and size chips.
- Games saved by older versions are re-parsed automatically the next time you open them.

### 4. Verification window (`App/Verification/CaptchaResolverWindow`)
- Every scraper and download request goes through `WebChallenge/ChallengeHandler`. When a site answers with a bot
  check (Cloudflare "Just a moment", DDoS-Guard …), a captcha (reCAPTCHA, hCaptcha, Turnstile), or an HTML landing
  page where a file was expected, the request is **paused** and a WebView2 window opens on that page.
- Solve it (or click through to the real download). The window closes by itself when the check clears or the file
  download starts; otherwise press **Continue**. Its cookies and browser user agent are copied into the app's shared
  cookie jar and the request is retried, so the download resumes where it stopped.
- The job shows *Waiting for verification*, and a banner at the top of the window can bring the browser back.
  Only one window opens per site at a time; other requests to that site wait for it. Pressing **Cancel** fails that
  request and suppresses new prompts for that site for two minutes.
- The browser profile (and so its cookies) lives in `%LOCALAPPDATA%\SeaRaider\webview2`.

### Fixes
- Live results are de-duplicated when a site lists the same post twice.

---

## What's new in v0.2

### 1. Full-site crawler (`ScraperService`)
Adding a source now crawls the **whole site in the background** and streams games into the store as they're parsed.

| Step | How |
|---|---|
| Feed? | RSS/Atom → every item (enclosure links used when the item page has none) |
| Single game page? | The URL itself has downloads or game schema → just that page |
| **XML sitemap** (primary) | `Sitemap:` lines in robots.txt, else `/sitemap_index.xml`, `/wp-sitemap.xml`, `/sitemap.xml`… Indexes are followed recursively, `.xml.gz` is unpacked, and taxonomy / author / image / static-page sitemaps are skipped. Every historical post in a handful of requests. |
| **Pagination** (fallback) | `rel="next"`, "Older posts" / `.pagination .next`, or a `/page/N/` (`?page=N`) template — until a page brings no new posts, 404s, or the page cap is hit |

- Producer/consumer pipeline (`Channel<T>`): discovery feeds URLs, three workers fetch and parse, results stream out as
  `IAsyncEnumerable<GameMetadata>`. `IngestionService` saves them in batches (≈12 games / 1.5 s) and raises
  `CatalogChanged` with just the affected ids, so the store updates smoothly.
- Progress: `IProgress<CrawlProgress>` (phase, pages, discovered, processed, skipped, games, errors) drives the
  Settings › Sources tab; `IIngestionService.CrawlSourceAsync(sourceId, IProgress<int> gamesSaved, …)` is the simple
  "games saved so far" overload.
- **Polite by default**: one request at a time per site with a configurable pause (750 ms), robots.txt
  Allow/Disallow and Crawl-delay honoured (can be switched off per source), 429/503 back-off with Retry-After.
- **Incremental re-crawls**: every URL visited is remembered (`crawl_urls` collection). A page is re-fetched only if
  its sitemap `<lastmod>` is newer, or after *Re-check known pages after N days*; pagination stops after two pages
  of already-known posts. **Full rescan** ignores all of that.
- Per-source options: crawl mode, include/exclude URL regexes, page cap, request pause, robots.txt, plus the CSS
  selector overrides (now including a *next page* selector).

### 2. Pre-download renaming
Clicking **Download & Install** opens a dialog (can be turned off in Settings):
- **Install folder** name, with the full path previewed and a note if it exists (never overwritten: `Name (2)`).
- **Downloaded file names**: you edit the base name; extensions and volume suffixes (`.part01.rar`, `.7z.001`,
  `.z01`, `.tar.gz`) are preserved and every part of a set is renamed consistently, so the extractor still pairs them.
  Torrents can only rename the folder (file names come from the torrent).
- Installer options: silent install, start the installer automatically, custom installer arguments (`{dir}` = folder).

`DownloadJob` now carries `InstallFolderName`, `Files` (`DownloadFileSpec`: URL, original name, **SaveAsFileName**,
local path) and the installer choices; jobs from v0.1 are migrated automatically.

### 3. Setup / installer lifecycle
After extraction, `InstallerDetector` decides:
- **Playable folder** (a convincing game exe exists) → moved to the library, button shows **Play**.
- **Setup program** (`setup.exe`, `setup_*.exe`, `*-Setup.exe`, `*Installer.exe`, `.msi`; redistributables ignored)
  → kept in `<Library>\.searaider-setup\<game>`, button shows **Install**.

The installer technology is identified from the file's bytes, which decides the silent switches:

| Kind | Silent | Target folder |
|---|---|---|
| Inno Setup | `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-` | `/DIR="…"` |
| NSIS | `/S` | `/D=…` (last, unquoted) |
| MSI | `msiexec /i … /qn /norestart` | `INSTALLDIR="…" TARGETDIR="…"` |
| InstallShield | `/s /v"/qn …"` | `INSTALLDIR` |
| WiX Burn | `/quiet /norestart` | — |
| Unknown | runs the wizard normally | — |

**Install** starts the setup (UAC prompts work), waits for it and for anything else running from the setup folder,
and reads the exit code (3010/1641 = success needing reboot; Inno 2/5, MSI 1602, NSIS 1 = cancelled).

### 4. Post-install cleanup and exe resolution
On success: the install location is found (our target folder, else the new Windows *installed programs* registry
entry: `InstallLocation`, `DisplayIcon`, uninstaller path), the game exe is detected (uninstallers and redists
ignored), the uninstaller is recorded, **setup files are deleted**, and the game becomes **Play**.
On failure or cancel the setup files stay so you can retry (or *Remove setup*). Uninstalling a setup-installed game
runs its own uninstaller silently first, then sweeps the folder.

### Fixes
- Build warnings: **AngleSharp 1.5.0** and **SharpCompress 0.48.0** (patched versions of the two advisories);
  SharpCompress API renames applied (`OpenArchive`, `OpenReader`, `TotalUncompressedSize`). `CS0108` fixed
  (`DownloadJobViewModel.JobError` / `HasJobError`).
- The store grid is now virtualized (`ItemsRepeater` + `UniformGridLayout`), so thousands of crawled games scroll smoothly.

---

## Project structure

```
src/SeaRaider.Core/
  Models/          GameMetadata (+ DownloadLink.Mirror / FileHost kind), CatalogEntry (+ CatalogSort, CatalogSorting), InstalledGame (+ GameInstallState, InstallerState, InstallerKind),
                   DownloadJob (+ DownloadFileSpec), GameSource (+ CrawlMode, CrawlUrlRecord, CrawlProgress), AppSettings
  Abstractions/    IScraperService, IIngestionService, ILibraryRepository, IDownloadAndExtractManager,
                   IInstallerService, IGameLauncherService, IExecutableLocator, ISaveDataGuard, …
  Catalog/         LiveSearchService, CatalogIndexService (in-memory index + duplicate keys), GameDetailsService (lazy details),
                   TitleNormalizer, SearchScorer (+ SearchQuery), GameDeduplicator
  Social/          FriendsService, RelayFriendsTransport (+ RelayProtocol), PresenceMonitor, SocialStore, Emotes, FriendCode
  WebChallenge/    ChallengeHandler (+ TimeoutHandler), ChallengeDetector, WebSessionStore, IChallengeResolver
  Data/            LiteDbLibraryRepository (catalog + catalog_index, sources, crawl state, installs, jobs, settings)
  Scraping/        ScraperService (crawler), CrawlSession (polite HTTP), SitemapReader, RobotsTxt, UrlRules,
                   GamePageParser (HTML/feed → GameMetadata), MetadataParser (titles, info fields), IngestionService (background crawls),
                   LinkClassifier, FileHosts (mirror hosts), SiteProfiles (FitGirl, SteamRIP …)
  Downloads/       DownloadAndExtractManager (scheduler), DownloadQueueManager (slots, order, bandwidth), BandwidthLimiter
                   (+ TransferThrottle, AdjustableSlots), InstallRequest/InstallPreview/FileNameRules, DownloadPlanner,
                   FileHostResolver, HttpResumableDownloader, MonoTorrentDownloader (+ seeding)
  Extraction/      ArchiveExtractor, ArchiveSetResolver, ConcatenatedReadStream
  Installing/      SilentInstallManager, SetupEngineDetector, StallWatchdog, ProcessTreeTracker, WindowSurfacer (+ Native/),
                   InstallerDetector, InstallerService, UninstallRegistry, CommandLine
  Launching/       GameLauncherService, ExecutableLocator, ProcessScanner, SaveDataGuard
src/SeaRaider.App/
  Verification/    CaptchaResolverWindow (WinForms + WebView2), WebView2ChallengeResolver
  Services/        GameStatusService (UI-thread event funnel), GameActionService (main button), DialogService,
                   NavigationService, ShellService
  ViewModels/      Storefront, GameCard, GameDetail, Library, Downloads, Sources (hosted in Settings), Settings, Friends, InstallOptionsDialog, OnboardingDialog, MainWindow
  Views/           matching .axaml views + InstallOptionsDialog, OnboardingDialog, FriendsListOverlay
src/SeaRaider.Relay/   Program (minimal API), RelayHub (routing, presence, queues), UserRegistry; Dockerfile
tests/SeaRaider.Core.Tests/
```

## Data locations

| What | Where |
|---|---|
| Database | `%LOCALAPPDATA%\SeaRaider\searaider.db` |
| Verification browser profile | `%LOCALAPPDATA%\SeaRaider\webview2` (a fresh `webview2-<time>` if the old one was damaged) |
| WebView2 problems log | `%LOCALAPPDATA%\SeaRaider\logs\webview2.log` |
| Trailer player (page + hls.js) and its browser profile | `%LOCALAPPDATA%\SeaRaider\player`, `%LOCALAPPDATA%\SeaRaider\webview2-player` |
| Unpacked files kept by low disk space mode while paused | `<Library>\.searaider-staging\<job>` |
| Optional fixed-version WebView2 runtime | `WebView2Runtime\` next to the exe, or `%LOCALAPPDATA%\SeaRaider\webview2-runtime` |
| Image cache | `%LOCALAPPDATA%\SeaRaider\image-cache` |
| Torrent cache | `%LOCALAPPDATA%\SeaRaider\torrent-cache` |
| Save backups | `%LOCALAPPDATA%\SeaRaider\save-backups\<gameId>` |
| Temporary downloads | `%LOCALAPPDATA%\SeaRaider\temp` (configurable) |
| Setup files waiting to run | `<Library>\.searaider-setup\<gameId>` |
| Games | `C:\MyGames\<folder>` (configurable) |
| Friends, requests, chat history, your friend code | `%LOCALAPPDATA%\SeaRaider\social.db` |
| Relay registrations (server) | `relay-users.json` next to the relay (setting `Relay:UsersFile`) |

## Extending

- **Special sites**: implement `IScraperService` (`Priority > 0`, `CanHandle`, `CrawlAsync`) and register it in
  `AddSeaRaiderCore`. You can reuse `GamePageParser` for the HTML part.
- **Installer quirks**: per game, *Use custom installer arguments* in the download dialog; globally, extend
  `InstallerDetector.DetectKind` / `BuildStartInfo`.

## Known limitations

- Some Cloudflare-protected sites still refuse non-browser clients after you pass the check (they compare the TLS
  fingerprint, not just cookies). SeaRaider then stops with a clear "still blocked after verification" message
  rather than looping; downloading from the page in your browser is the workaround.
- Countdown pages are handled by the verification window, but you have to wait out the timer yourself.
- Live search depends on each site's own search. Sites whose search is JavaScript-only need a Custom template or
  an offline index.
- Upload-date sorting puts games without a known upload date (some saved by v0.2) last until they are opened again.
- Password-protected archives are reported, not extracted.
- Installers of unknown type run their normal wizard; if one ignores the target folder and registers nothing in
  Windows, use *Choose executable* in the Library.
- A setup that runs **as administrator** can't be closed or always brought forward by a non-elevated SeaRaider
  (Windows' integrity levels). The status line then says so and the window flashes in the taskbar. "Restart with
  wizard" needs you to close it yourself in that case.
- Host resolvers follow each host's current page layout. When a host changes it, SeaRaider falls back to the
  verification window, and the resolver needs updating.
- The relay keeps queued messages in memory: a relay restart drops undelivered messages (presence recovers on its
  own). Chat is not end-to-end encrypted; use `wss://` and a relay you trust.
- Merging duplicates is by title. Two different games with the same name on two sites would share a card; their
  source tabs still keep them apart.
- Downloads that run in SeaRaider's browser window ignore the bandwidth limits. They resume after a pause, but not
  after SeaRaider is closed: that part starts again from the host's page.
- The download window's button clicking is a heuristic (the biggest visible "Download" button that stays on the
  host). A host with an unusual page may need your click; the window comes forward when that happens.
- Low disk space mode covers RAR volume sets only (see v0.6 §3).
- Steam trailers play in SeaRaider's trailer window. Its browser runs with web security off for the player page
  only, because Steam's video servers only serve the Steam store's pages. That browser loads nothing but the player
  page and Steam's video files.
- The *Continue in your browser* fallback can't carry cookies over (browsers keep them encrypted). It works for file
  hosts that hand out a signed download link; sites that only check the browser itself (a Cloudflare page in front of
  a search) need the WebView2 Runtime, which the window offers to install.
- No public relay is built in: friends connect out of the box only when the build sets `SeaRaiderDefaultRelay`
  (or `SEARAIDER_RELAY` is set). Otherwise the Friends tab asks for a relay address.
- Steam/IGDB media is matched by title; games whose release name differs a lot from the store name keep the page's
  own (cleaned) media.

### Version-sensitive APIs

- `ArchiveExtractor` — SharpCompress 0.48 (`ArchiveFactory.OpenArchive`, `ReaderFactory.OpenReader`, `IArchive.TotalUncompressedSize`).
- `MonoTorrentDownloader` — MonoTorrent 3.x (`EngineSettingsBuilder`, `TorrentManager.Monitor.DownloadRate`, `RemoveMode`).
- `StorefrontView` — `Avalonia.Controls.ItemsRepeater` **11.1.5** (the last 11.x release; it runs on Avalonia 11.2).
- `CaptchaResolverWindow` — Microsoft.Web.WebView2 WinForms (`CoreWebView2Environment.CreateAsync`, `CookieManager.GetCookiesAsync`,
  `CoreWebView2Cookie.ToSystemNetCookie`, `DownloadStarting`). The App csproj sets `UseWindowsForms` and removes the
  `System.Windows.Forms` / `System.Drawing` global usings so they don't clash with Avalonia types.
- `MonoTorrentDownloader` (v0.4) — `EngineSettingsBuilder.MaximumDownloadRate/MaximumUploadRate`, `ClientEngine.UpdateSettingsAsync`,
  `TorrentSettingsBuilder`, `TorrentManager.UpdateSettingsAsync`, `Monitor.DataBytesSent` / `UploadRate`.
- `Theme.axaml` emote animations — keyframes on `TranslateTransform.Y`, `RotateTransform.Angle`, `ScaleTransform.ScaleX/Y`
  (Avalonia builds the transform group on the TextBlock automatically).
- `App.axaml.cs` — `AsyncImageLoader.ImageLoader.AsyncImageLoader` / `DiskCachedWebImageLoader`.
- `MonoTorrentDownloader` (v0.5) — `EngineSettingsBuilder.FastResumeMode = FastResumeMode.BestEffort`,
  `TorrentManager.StopAsync(TimeSpan)`.
- `BrowserDownloadWindow` (v0.6) — WebView2 `DownloadStarting` (`ResultFilePath`, `Handled`), `CoreWebView2DownloadOperation`
  (`BytesReceived`, `TotalBytesToReceive`, `Pause/Resume/CanResume`, `InterruptReason`), `NavigateWithWebResourceRequest`,
  `NavigationCompletedEventArgs.HttpStatusCode`.
- `TrailerPlayerHost` — an Avalonia `NativeControlHost` whose default child window hosts a
  `CoreWebView2Controller` (`CoreWebView2Environment.CreateCoreWebView2ControllerAsync(IntPtr)`, `Bounds` in physical pixels,
  `AcceleratorKeyPressed`), plus `CoreWebView2EnvironmentOptions(additionalBrowserArguments)`,
  `SetVirtualHostNameToFolderMapping`, `AddWebResourceRequestedFilter`.
- `ArchiveExtractor.ExtractVolumesAsync` (v0.6) — SharpCompress 0.48 `RarReader.OpenReader(IEnumerable<Stream>, ReaderOptions)`,
  which asks for the next volume lazily (checked against the 0.48.0 source).
- `WebView2Runtime` (v0.5) — `CoreWebView2Environment.GetAvailableBrowserVersionString(string)`,
  `WebView2RuntimeNotFoundException`.
- `SetupRunner` / `AudioSessionMuter` (v0.10) — `Process.Kill(entireProcessTree: true)`, `ProcessStartInfo.Verb = "runas"`,
  Core Audio `IAudioSessionControl2.GetSessionInstanceIdentifier` (string freed with `Marshal.FreeCoTaskMem`).
