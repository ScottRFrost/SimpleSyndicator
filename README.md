SimpleSyndicator gets images, gifs, and MP4s from Reddit via authenticated JSON endpoints (OAuth2 API on `oauth.reddit.com` or an authenticated browser session cookie on `.json` URLs, since Reddit discontinued RSS feeds and blocks anonymous access) and posts them to Telegram using [TelegramBots/Telegram.Bot](https://github.com/TelegramBots/Telegram.Bot).

Galleries of multiple images are supported, and each image is posted with a link back to Reddit for source / credits.  Automatically handles images that are too large (size on disk or dimensions) for Telegram in native .NET using [SixLabors/ImageSharp](https://github.com/SixLabors/ImageSharp/).  For media downloads and browser-cookie requests, the latest Chrome/Windows user-agent is looked up at runtime and pauses occur between actions.

Extensible to allow new sources / new destinations easily.

## Reddit Authentication

SimpleSyndicator automatically selects an authentication mode from the `"Reddit"` section of `config.json`:
- **OAuth2 (`oauth.reddit.com`)** is used whenever `ClientId` is configured along with a `RefreshToken`, a valid cached `AccessToken`, or script credentials (`ClientSecret`, `Username`, `Password`).
- **Browser Session Cookie (`.json`)** is used as a fallback when OAuth2 credentials are not configured and `SessionCookie` is set.

### Option 1: OAuth2 with 2FA / Refresh Token (Recommended for existing Reddit apps)
1. Configure `"ClientId"`, `"ClientSecret"`, and `"RedirectUri"` (matching your Reddit app's redirect URI, e.g. `http://localhost:8080/callback`) under `"Reddit"` in `config.json`.
2. Run a one-time interactive authorization:
   ```bash
   dotnet run -- --reddit-auth
   ```
   (or `SimpleSyndicator.exe --reddit-auth` / `./SimpleSyndicator --reddit-auth`).
3. Open the printed Reddit URL in your browser and approve access. SimpleSyndicator listens on `http://localhost:<port>/` automatically, or you can paste the redirected URL (or code) into the terminal when running on a headless/SSH machine.
4. `RefreshToken`, `AccessToken`, and `AccessTokenExpiresUtc` are saved back to `config.json` and refreshed automatically on future runs.

### Option 2: OAuth2 Script App without 2FA
Set `"ClientId"`, `"ClientSecret"`, `"Username"`, and `"Password"` (and optionally `"UserAgent"`) under `"Reddit"` in `config.json`. SimpleSyndicator requests and caches OAuth2 bearer tokens automatically.

### Option 3: Browser Session Cookie Fallback (No Reddit API app required)
If you do not have a Reddit OAuth app, sign in to Reddit in your browser, open DevTools (`Network` tab -> click a request to `www.reddit.com` -> `Request Headers`), and copy the `Cookie` header (or the `token_v2` / `reddit_session` cookie value) into `"SessionCookie"` under `"Reddit"` in `config.json`.

## API Hygiene & Rate-Limit Protection

- **OAuth2 `User-Agent`**: Automatically formatted as `dotnet:SimpleSyndicator:v1.0 (by /u/<Username>)` when `Username` is set (or overridden via `UserAgent`), without mimicking a browser on `oauth.reddit.com`.
- **Circuit Breaker**: Global `401`, `403`, and `429` errors stop remaining Reddit source fetches for that run instead of hammering Reddit, while subreddit-scoped `403` responses (`private`, `quarantined`, `banned`, `gated`) skip only that source.
- **Retry & Backoff**: Bounded retries on `429` and `5xx` honor `Retry-After` and `X-Ratelimit-Reset` (capped at 60s), plus proactive pausing when `X-Ratelimit-Remaining < 5`.
- **Incremental Cursor & Limits**: `/new` and `search?sort=new` sources track `LastSeenFullname` / `LastSeenUtc` in `config.json` and pass `before=` (re-anchored automatically every 24 hours) along with `DefaultLimit` (default `25`, overridable per source via `Limit`).
- **Jittered Delays**: Source and media pauses apply randomized jitter (`0.80x`–`1.30x`) around `SourceDelaySeconds` (default `5`s for OAuth2, `30`s for cookie mode) and `MediaDelaySeconds` (default `5`s).

## Sample `config.json`

(`config.json` is in `.gitignore` so secrets and tracking state are not committed.)

```json
{
  "Reddit": {
    "ClientId": "YOUR_REDDIT_CLIENT_ID",
    "ClientSecret": "YOUR_REDDIT_CLIENT_SECRET",
    "RedirectUri": "http://localhost:8080/callback",
    "Username": "your_reddit_username",
    "Password": "",
    "UserAgent": "dotnet:SimpleSyndicator:v1.0 (by /u/your_reddit_username)",
    "RefreshToken": "",
    "AccessToken": "",
    "AccessTokenExpiresUtc": null,
    "SessionCookie": "",
    "DefaultLimit": 25
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
