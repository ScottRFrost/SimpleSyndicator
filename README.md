SimpleSyndicator browses Reddit using a real Chromium or Firefox browser via [Microsoft Playwright](https://playwright.dev/dotnet/) (no Reddit OAuth2 app or API credentials required) and posts discovered images, galleries, GIFs, and MP4s to Telegram using [TelegramBots/Telegram.Bot](https://github.com/TelegramBots/Telegram.Bot).

Multi-image galleries and videos are extracted directly from Reddit pages, and each syndicated item is posted with a link back to Reddit for source / credits. Images that exceed Telegram's pixel dimensions or 10 MB upload limit are automatically resized or resampled natively in .NET using [SixLabors/ImageSharp](https://github.com/SixLabors/ImageSharp/). Both `win-x64` and `linux-x64` are supported.

Extensible to allow new sources / new destinations easily.

## Setup

### 1. Install Playwright Browser Binaries

On **Windows**:
```bash
dotnet run -- --install-browsers
# or, using a published build:
SimpleSyndicator.exe --install-browsers
```

On **Linux (`linux-x64`)**, include `--with-deps` (as root / with `sudo`) so Playwright installs Chromium's shared OS libraries as well:
```bash
./SimpleSyndicator --install-browsers --with-deps
```

### 2. Optional Authenticated Reddit Session (`--reddit-login`)

SimpleSyndicator can browse public subreddits anonymously or use a signed-in Reddit browser session (recommended for mature/NSFW/age-gated subreddits or tighter rate limits):

1. Run the interactive login helper on a machine with a desktop window (Windows or Linux desktop):
   ```bash
   dotnet run -- --reddit-login
   # or: SimpleSyndicator.exe --reddit-login
   ```
2. Sign in to Reddit normally inside the opened browser window (completing any 2FA or verification prompts directly in the browser), then press `Enter` in the terminal.
3. SimpleSyndicator writes a portable Playwright storage state file (`reddit-state.json` by default). You can copy `reddit-state.json` alongside the app on a headless `linux-x64` server so future headless runs reuse your session automatically.

Alternative session options under `"Reddit"` in `config.json`:
- **`UserDataDir`**: Path to a persistent browser profile directory (uses `LaunchPersistentContextAsync` instead of `StorageStatePath`).
- **`SessionCookie`**: Optional fallback cookie header or `token_v2` / `reddit_session` cookie value injected into the Playwright browser context.

## Source URLs & Rate-Limit Protection

- **Automatic URL Normalization**: Existing `config.json` source URLs ending in `.json` or `.rss` (or pointing to `oauth.reddit.com`) are automatically rewritten to browse `www.reddit.com` web pages while keeping all search/filter query parameters (`q`, `restrict_sr`, `include_over_18`, `sort`, `t`, etc.) and respecting `old.reddit.com` if explicitly configured.
- **Native Browser Headers**: Uses Playwright's genuine Chromium/Firefox browser engine, headers, and network stack (`APIRequest` for media downloads) with optional HTTP fallback.
- **Circuit Breaker**: Global HTTP `401` / `403` / `429` responses, `/login` redirects, and CAPTCHA or bot-verification pages stop remaining Reddit fetches for that run cleanly (`ShouldAbortRun`) rather than hammering Reddit. Private, banned, quarantined, missing (`404`), or unconfirmed 18+ subreddits skip only that source.
- **Incremental Cursor & Limits**: `/new` and `search?sort=new` sources track `LastSeenFullname` and `LastSeenUtc` in `config.json` to stop scrolling once previously seen posts are reached (re-anchoring every 24 hours), bounded by `DefaultLimit` (default `25`, overridable per source via `Limit`) and `MaxScrollPasses` (default `4`).
- **Jittered Delays**: Source and post/media pauses apply randomized jitter (`0.80x`–`1.30x`) around `SourceDelaySeconds` (default `30`s) and `MediaDelaySeconds` (default `5`s), plus a quiet `2`s pause between Telegram uploads.

## Sample `config.json`

(`config.json`, `reddit-state.json`, and `browser-profile/` are listed in `.gitignore` so secrets and tracking state are not committed.)

```json
{
  "Reddit": {
    "Browser": "chromium",
    "BrowserChannel": null,
    "ExecutablePath": null,
    "Headless": true,
    "StorageStatePath": "reddit-state.json",
    "UserDataDir": null,
    "SessionCookie": null,
    "Locale": "en-US",
    "NavigationTimeoutSeconds": 45,
    "DefaultLimit": 25,
    "MaxScrollPasses": 4,
    "ExpandPostDetails": true
  },
  "Sources": [
    {
      "Type": "Reddit",
      "URL": "https://www.reddit.com/r/beachporn/new.json",
      "Tags": "#beach #relaxation"
    },
    {
      "Type": "Reddit",
      "URL": "https://www.reddit.com/r/cats/search.json?q=flair%3ACat%20Picture%20-%20OC\u0026restrict_sr=on\u0026include_over_18=on\u0026sort=new\u0026t=day",
      "Tags": "#cats"
    }
  ],
  "TelegramKey": "111222333",
  "TelegramChannelIDs": [
    -123456789
  ],
  "IFTTTWebHooks": [
  ],
  "SyndicatedURLs": [
  ]
}
```
