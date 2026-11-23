using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Playwright;

namespace SimpleSyndicator;

public sealed class RedditBrowserException : InvalidOperationException
{
    public int? StatusCode { get; }
    public bool ShouldAbortRun { get; }

    public RedditBrowserException(
        string message,
        int? statusCode = null,
        bool shouldAbortRun = false,
        Exception innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        ShouldAbortRun = shouldAbortRun;
    }
}

public sealed record RedditBrowserPost
{
    public string Id { get; init; } = string.Empty;
    public string Fullname { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Permalink { get; init; } = string.Empty;
    public string ContentHref { get; init; } = string.Empty;
    public string PostType { get; init; } = string.Empty;
    public bool IsStickied { get; init; }
    public bool IsGallery { get; init; }
    public bool IsSelf { get; init; }
    public bool NeedsDetailPage { get; init; }
    public string[] MediaUrls { get; init; } = [];
    public string ThumbnailUrl { get; init; } = string.Empty;
}

public sealed record RedditPageInspection
{
    public int? HttpStatusCode { get; init; }
    public string FinalUrl { get; init; } = string.Empty;
    public string PageTitle { get; init; } = string.Empty;
    public string BodySnippet { get; init; } = string.Empty;
    public bool HasPosts { get; init; }
    public bool HasCaptchaOrChallengeElement { get; init; }
}

public sealed class RedditBrowserClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly string[] SubredditScopedBlockPhrases =
    [
        "community not found",
        "is a private community",
        "this subreddit is private",
        "has been banned from reddit",
        "banned due to a violation",
        "is quarantined"
    ];

    private static readonly string[] MatureContentGatePhrases =
    [
        "you must be 18+",
        "are you over 18",
        "mature content",
        "over 18"
    ];

    private static readonly string[] GlobalAntiBotPhrases =
    [
        "whoa there, pardner",
        "blocked by network security",
        "you've been blocked",
        "prove your humanity",
        "verify you are human",
        "complete the captcha",
        "request has been blocked",
        "too many requests"
    ];

    private readonly RedditConfig _config;
    private readonly Func<TimeSpan, Task> _delayAsync;

    private IPlaywright _playwright;
    private IBrowser _browser;
    private IBrowserContext _context;
    private bool _persistedStorageStateExistsAtStart;
    private bool _disposed;

    public RedditBrowserClient(RedditConfig config)
        : this(config, delayAsync: null)
    {
    }

    internal RedditBrowserClient(RedditConfig config, Func<TimeSpan, Task> delayAsync)
    {
        _config = config ?? new RedditConfig();
        _delayAsync = delayAsync ?? (static d => Task.Delay(d));
    }

    public static string NormalizeBrowseUrl(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        var uri = new Uri(url, UriKind.Absolute);
        var host = uri.Host.Equals("old.reddit.com", StringComparison.OrdinalIgnoreCase)
            ? "old.reddit.com"
            : "www.reddit.com";

        var trimmedPath = uri.AbsolutePath.TrimEnd('/');
        if (trimmedPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            trimmedPath = trimmedPath[..^5];
        }
        else if (trimmedPath.EndsWith(".rss", StringComparison.OrdinalIgnoreCase))
        {
            trimmedPath = trimmedPath[..^4];
        }

        trimmedPath = trimmedPath.TrimEnd('/');
        var path = string.IsNullOrEmpty(trimmedPath) ? "/" : $"{trimmedPath}/";

        var rawQuery = uri.Query.TrimStart('?');
        string filteredQuery = string.Empty;
        if (!string.IsNullOrEmpty(rawQuery))
        {
            var keptSegments = new List<string>();
            foreach (var segment in rawQuery.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eqIndex = segment.IndexOf('=');
                var key = eqIndex >= 0 ? segment[..eqIndex] : segment;
                if (key.Equals("raw_json", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                keptSegments.Add(segment);
            }

            filteredQuery = string.Join("&", keptSegments);
        }

        return string.IsNullOrEmpty(filteredQuery)
            ? $"https://{host}{path}"
            : $"https://{host}{path}?{filteredQuery}";
    }

    public static bool SupportsIncrementalCursor(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^5];
        }
        else if (path.EndsWith(".rss", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^4];
        }

        path = path.TrimEnd('/');

        var rawQuery = uri.Query.TrimStart('?');
        var hasSortNew = false;
        if (!string.IsNullOrEmpty(rawQuery))
        {
            foreach (var segment in rawQuery.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eqIndex = segment.IndexOf('=');
                var key = eqIndex >= 0 ? segment[..eqIndex] : segment;
                if (key.Equals("before", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("after", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (key.Equals("sort", StringComparison.OrdinalIgnoreCase) && eqIndex >= 0)
                {
                    var value = Uri.UnescapeDataString(segment[(eqIndex + 1)..]);
                    if (value.Equals("new", StringComparison.OrdinalIgnoreCase))
                    {
                        hasSortNew = true;
                    }
                }
            }
        }

        if (path.EndsWith("/new", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return path.EndsWith("/search", StringComparison.OrdinalIgnoreCase) && hasSortNew;
    }

    public static TimeSpan ComputeJitteredDelay(int baseSeconds)
    {
        if (baseSeconds <= 0)
        {
            return TimeSpan.Zero;
        }

        var baseMs = baseSeconds * 1000.0;
        var factor = 0.80 + (Random.Shared.NextDouble() * 0.50);
        return TimeSpan.FromMilliseconds(Math.Max(500, Math.Round(baseMs * factor)));
    }

    public static IReadOnlyList<Cookie> ParseSessionCookies(string rawCookie)
    {
        if (string.IsNullOrWhiteSpace(rawCookie))
        {
            return [];
        }

        var trimmed = rawCookie.Trim();
        if (trimmed.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[7..].Trim();
        }

        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return [];
        }

        if (trimmed.Contains('='))
        {
            var cookies = new List<Cookie>();
            foreach (var segment in trimmed.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var eqIndex = segment.IndexOf('=');
                if (eqIndex <= 0)
                {
                    continue;
                }

                var name = segment[..eqIndex].Trim();
                var value = segment[(eqIndex + 1)..].Trim();
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(value))
                {
                    continue;
                }

                cookies.Add(new Cookie
                {
                    Name = name,
                    Value = value,
                    Domain = ".reddit.com",
                    Path = "/",
                    Secure = true
                });
            }

            return cookies;
        }

        var cookieName = "reddit_session";
        if (trimmed.StartsWith("eyJ", StringComparison.Ordinal))
        {
            var parts = trimmed.Split('.');
            if (parts.Length == 3 && parts.All(p => !string.IsNullOrEmpty(p)))
            {
                cookieName = "token_v2";
            }
        }

        return
        [
            new Cookie
            {
                Name = cookieName,
                Value = trimmed,
                Domain = ".reddit.com",
                Path = "/",
                Secure = true
            }
        ];
    }

    public static (bool IsBlocked, bool ShouldAbortRun, string Reason) EvaluatePageBlock(RedditPageInspection inspection)
    {
        if (inspection == null)
        {
            return (false, false, null);
        }

        var combinedText = $"{inspection.PageTitle}\n{inspection.BodySnippet}";

        // 1. Subreddit-scoped non-aborting states when no posts rendered
        if (!inspection.HasPosts)
        {
            if (inspection.HttpStatusCode == 404 ||
                ContainsAnyPhrase(combinedText, SubredditScopedBlockPhrases))
            {
                return (true, false, $"Subreddit or post is unavailable (HTTP {inspection.HttpStatusCode?.ToString() ?? "page"} / private, banned, quarantined, or missing). Skipping source.");
            }

            if (ContainsAnyPhrase(combinedText, MatureContentGatePhrases))
            {
                return (true, false, "Subreddit requires mature-content/account confirmation. Sign in with --reddit-login and enable mature content in Reddit preferences.");
            }
        }

        // 2. Global aborting states
        if (inspection.HttpStatusCode is 401 or 403 or 429)
        {
            return (true, true, $"Reddit returned HTTP {inspection.HttpStatusCode.Value}. Stopping Reddit fetches for this run (try signing in via --reddit-login or retry later).");
        }

        if (!inspection.HasPosts &&
            !string.IsNullOrWhiteSpace(inspection.FinalUrl) &&
            (inspection.FinalUrl.Contains("/login", StringComparison.OrdinalIgnoreCase) ||
             inspection.FinalUrl.Contains("/account/login", StringComparison.OrdinalIgnoreCase)))
        {
            return (true, true, "Reddit redirected to a login page. Run with --reddit-login to save a browser session.");
        }

        if (inspection.HasCaptchaOrChallengeElement)
        {
            return (true, true, "Reddit presented a CAPTCHA or verification challenge. Stopping run (try signing in interactively via --reddit-login or retry later).");
        }

        if (!inspection.HasPosts && ContainsAnyPhrase(combinedText, GlobalAntiBotPhrases))
        {
            return (true, true, "Reddit blocked or rate-limited the browser request. Stopping run (try signing in interactively via --reddit-login or retry later).");
        }

        return (false, false, null);
    }

    public static void InstallBrowsers(string browserName, bool withDeps = false)
    {
        var normalizedBrowser = NormalizeBrowserName(browserName);
        var args = withDeps
            ? new[] { "install", "--with-deps", normalizedBrowser }
            : new[] { "install", normalizedBrowser };

        var exitCode = Microsoft.Playwright.Program.Main(args);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"Playwright browser install exited with code {exitCode}.");
        }
    }

    public async Task RunInteractiveLoginAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var context = await EnsureContextAsync(forceHeadlessFalse: true).ConfigureAwait(false);
        var page = await context.NewPageAsync().ConfigureAwait(false);
        try
        {
            var timeoutMs = GetTimeoutMs();
            page.SetDefaultNavigationTimeout(timeoutMs);
            page.SetDefaultTimeout(timeoutMs);

            Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Opening https://www.reddit.com/login in browser...");
            await page.GotoAsync("https://www.reddit.com/login", new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = timeoutMs
            }).ConfigureAwait(false);

            Console.WriteLine();
            Console.WriteLine("Sign in to Reddit inside the opened browser window (including any 2FA or verification prompts).");
            Console.WriteLine("Once you see your logged-in Reddit home page or profile, return here and press Enter to save the session...");
            Console.ReadLine();

            if (!string.IsNullOrWhiteSpace(_config.UserDataDir))
            {
                var userDataDir = ResolveWorkspacePath(_config.UserDataDir);
                Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Saved browser session in UserDataDir '{userDataDir}'.");
            }
            else
            {
                var statePath = ResolveWorkspacePath(GetEffectiveStorageStatePath());
                var stateDir = Path.GetDirectoryName(statePath);
                if (!string.IsNullOrEmpty(stateDir))
                {
                    Directory.CreateDirectory(stateDir);
                }

                await context.StorageStateAsync(new BrowserContextStorageStateOptions
                {
                    Path = statePath
                }).ConfigureAwait(false);
                _persistedStorageStateExistsAtStart = true;

                Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Saved Reddit browser session state to '{statePath}'.");
            }
        }
        finally
        {
            try
            {
                await page.CloseAsync().ConfigureAwait(false);
            }
            catch
            {
                // Ignore cleanup failure when user already closed the browser window
            }
        }
    }

    public async Task<(IReadOnlyList<RedditBrowserPost> Posts, string NewestFullname)> BrowseListingAsync(
        string url,
        int limit,
        string stopBeforeFullname = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        var effectiveLimit = Math.Clamp(limit > 0 ? limit : 25, 1, 100);
        var normalizedUrl = NormalizeBrowseUrl(url);

        var context = await EnsureContextAsync().ConfigureAwait(false);
        var page = await context.NewPageAsync().ConfigureAwait(false);
        try
        {
            var timeoutMs = GetTimeoutMs();
            page.SetDefaultNavigationTimeout(timeoutMs);
            page.SetDefaultTimeout(timeoutMs);

            var response = await page.GotoAsync(normalizedUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = timeoutMs
            }).ConfigureAwait(false);

            await WaitForPostsOrChallengeAsync(page).ConfigureAwait(false);

            var inspection = await InspectPageAsync(page, response).ConfigureAwait(false);
            var blockCheck = EvaluatePageBlock(inspection);
            if (blockCheck.IsBlocked)
            {
                throw new RedditBrowserException(
                    blockCheck.Reason,
                    inspection.HttpStatusCode,
                    blockCheck.ShouldAbortRun);
            }

            var collectedPosts = new List<RedditBrowserPost>();
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string newestFullname = null;
            var hitCursor = false;
            var consecutiveEmptyPasses = 0;
            var maxScrollPasses = Math.Max(0, _config.MaxScrollPasses);

            for (int pass = 0; pass <= maxScrollPasses; pass++)
            {
                var batch = await ExtractPostsFromPageAsync(page).ConfigureAwait(false);

                if (pass == 0 && batch.Count > 0)
                {
                    var topPost = batch.FirstOrDefault(p => !p.IsStickied && !string.IsNullOrWhiteSpace(p.Fullname))
                        ?? batch.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.Fullname));
                    newestFullname = topPost?.Fullname;
                }

                var addedThisPass = 0;
                foreach (var post in batch)
                {
                    if (string.IsNullOrWhiteSpace(post.Id))
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(stopBeforeFullname) &&
                        string.Equals(post.Fullname, stopBeforeFullname, StringComparison.OrdinalIgnoreCase))
                    {
                        hitCursor = true;
                        break;
                    }

                    if (!seenIds.Add(post.Id))
                    {
                        continue;
                    }

                    collectedPosts.Add(post);
                    addedThisPass++;

                    if (collectedPosts.Count >= effectiveLimit)
                    {
                        break;
                    }
                }

                if (hitCursor || collectedPosts.Count >= effectiveLimit || pass == maxScrollPasses)
                {
                    break;
                }

                if (addedThisPass == 0)
                {
                    consecutiveEmptyPasses++;
                    if (consecutiveEmptyPasses >= 2)
                    {
                        break;
                    }
                }
                else
                {
                    consecutiveEmptyPasses = 0;
                }

                await page.EvaluateAsync("() => window.scrollTo(0, document.body.scrollHeight)").ConfigureAwait(false);
                var scrollWaitMs = Random.Shared.Next(1200, 2201);
                await _delayAsync(TimeSpan.FromMilliseconds(scrollWaitMs)).ConfigureAwait(false);
            }

            return (collectedPosts, newestFullname);
        }
        finally
        {
            await ClosePageSafeAsync(page).ConfigureAwait(false);
        }
    }

    public async Task<RedditBrowserPost> BrowsePostDetailAsync(string permalink)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(permalink);

        var targetUrl = BuildPostDetailUrl(permalink);
        var expectedPostId = ExtractPostIdFromUrl(targetUrl);

        var context = await EnsureContextAsync().ConfigureAwait(false);
        var page = await context.NewPageAsync().ConfigureAwait(false);
        try
        {
            var timeoutMs = GetTimeoutMs();
            page.SetDefaultNavigationTimeout(timeoutMs);
            page.SetDefaultTimeout(timeoutMs);

            var response = await page.GotoAsync(targetUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = timeoutMs
            }).ConfigureAwait(false);

            await WaitForPostsOrChallengeAsync(page).ConfigureAwait(false);

            var inspection = await InspectPageAsync(page, response).ConfigureAwait(false);
            var blockCheck = EvaluatePageBlock(inspection);
            if (blockCheck.IsBlocked)
            {
                throw new RedditBrowserException(
                    blockCheck.Reason,
                    inspection.HttpStatusCode,
                    blockCheck.ShouldAbortRun);
            }

            await AdvanceGalleryCarouselSlidesAsync(page).ConfigureAwait(false);

            var posts = await ExtractPostsFromPageAsync(page).ConfigureAwait(false);
            if (posts.Count == 0)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(expectedPostId))
            {
                var exact = posts.FirstOrDefault(p => string.Equals(p.Id, expectedPostId, StringComparison.OrdinalIgnoreCase));
                if (exact != null)
                {
                    return exact;
                }
            }

            return posts[0];
        }
        finally
        {
            await ClosePageSafeAsync(page).ConfigureAwait(false);
        }
    }

    public async Task DownloadMediaAsync(string mediaUrl, Stream destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaUrl);
        ArgumentNullException.ThrowIfNull(destination);

        var context = await EnsureContextAsync().ConfigureAwait(false);
        var response = await context.APIRequest.GetAsync(mediaUrl, new APIRequestContextOptions
        {
            Timeout = 30000
        }).ConfigureAwait(false);

        await using (response)
        {
            if (!response.Ok)
            {
                throw new InvalidOperationException($"Playwright media request failed ({response.Status} {response.StatusText}) for '{mediaUrl}'.");
            }

            if (response.Headers.TryGetValue("content-type", out var contentType) &&
                !string.IsNullOrWhiteSpace(contentType) &&
                contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Playwright media request returned HTML instead of media for '{mediaUrl}'.");
            }

            var bytes = await response.BodyAsync().ConfigureAwait(false);
            await destination.WriteAsync(bytes).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            if (_context != null &&
                string.IsNullOrWhiteSpace(_config.UserDataDir) &&
                _persistedStorageStateExistsAtStart)
            {
                var statePath = ResolveWorkspacePath(GetEffectiveStorageStatePath());
                await _context.StorageStateAsync(new BrowserContextStorageStateOptions
                {
                    Path = statePath
                }).ConfigureAwait(false);
            }
        }
        catch
        {
            // Best-effort cookie refresh on shutdown
        }

        if (_context != null)
        {
            try
            {
                await _context.CloseAsync().ConfigureAwait(false);
            }
            catch
            {
                // Ignore context shutdown errors
            }

            _context = null;
        }

        if (_browser != null)
        {
            try
            {
                await _browser.CloseAsync().ConfigureAwait(false);
            }
            catch
            {
                // Ignore browser shutdown errors
            }

            _browser = null;
        }

        _playwright?.Dispose();
        _playwright = null;
    }

    private async Task<IBrowserContext> EnsureContextAsync(bool forceHeadlessFalse = false)
    {
        if (_context != null)
        {
            return _context;
        }

        try
        {
            _playwright = await Playwright.CreateAsync().ConfigureAwait(false);
            var browserName = NormalizeBrowserName(_config.Browser);
            var browserType = browserName == "firefox"
                ? _playwright.Firefox
                : _playwright.Chromium;

            var headless = !forceHeadlessFalse && _config.Headless;
            var locale = string.IsNullOrWhiteSpace(_config.Locale) ? "en-US" : _config.Locale.Trim();
            var args = _config.ExtraLaunchArgs is { Length: > 0 } ? _config.ExtraLaunchArgs : null;
            var channel = string.IsNullOrWhiteSpace(_config.BrowserChannel) ? null : _config.BrowserChannel.Trim();
            var executablePath = string.IsNullOrWhiteSpace(_config.ExecutablePath) ? null : _config.ExecutablePath.Trim();

            if (!string.IsNullOrWhiteSpace(_config.UserDataDir))
            {
                var userDataDir = ResolveWorkspacePath(_config.UserDataDir);
                Directory.CreateDirectory(userDataDir);

                _context = await browserType.LaunchPersistentContextAsync(userDataDir, new BrowserTypeLaunchPersistentContextOptions
                {
                    Headless = headless,
                    Channel = channel,
                    ExecutablePath = executablePath,
                    Args = args,
                    Locale = locale,
                    ViewportSize = new ViewportSize { Width = 1440, Height = 900 }
                }).ConfigureAwait(false);
            }
            else
            {
                _browser = await browserType.LaunchAsync(new BrowserTypeLaunchOptions
                {
                    Headless = headless,
                    Channel = channel,
                    ExecutablePath = executablePath,
                    Args = args
                }).ConfigureAwait(false);

                var statePath = ResolveWorkspacePath(GetEffectiveStorageStatePath());
                _persistedStorageStateExistsAtStart = File.Exists(statePath);

                _context = await _browser.NewContextAsync(new BrowserNewContextOptions
                {
                    StorageStatePath = _persistedStorageStateExistsAtStart ? statePath : null,
                    Locale = locale,
                    ViewportSize = new ViewportSize { Width = 1440, Height = 900 }
                }).ConfigureAwait(false);
            }

            if (!string.IsNullOrWhiteSpace(_config.SessionCookie))
            {
                var cookies = ParseSessionCookies(_config.SessionCookie);
                if (cookies.Count > 0)
                {
                    await _context.AddCookiesAsync(cookies).ConfigureAwait(false);
                }
            }

            return _context;
        }
        catch (PlaywrightException ex) when (IsMissingBrowserError(ex))
        {
            var browserName = NormalizeBrowserName(_config.Browser);
            throw new RedditBrowserException(
                $"Playwright {browserName} browser executable is not installed. Run with --install-browsers (or --install-browsers --with-deps on Linux).",
                statusCode: null,
                shouldAbortRun: true,
                innerException: ex);
        }
    }

    private async Task WaitForPostsOrChallengeAsync(IPage page)
    {
        try
        {
            await page.WaitForFunctionAsync(
                WaitForPostsOrChallengeScript,
                arg: null,
                new PageWaitForFunctionOptions
                {
                    Timeout = 12000,
                    PollingInterval = 350
                }).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Proceed to DOM inspection even if timeout elapsed (e.g. empty listing)
        }
        catch (PlaywrightException)
        {
            // Proceed to DOM inspection
        }
    }

    private static async Task<RedditPageInspection> InspectPageAsync(IPage page, IResponse navigationResponse)
    {
        var json = await page.EvaluateAsync<string>(InspectPageScript).ConfigureAwait(false);
        var domState = string.IsNullOrWhiteSpace(json)
            ? new RedditPageInspection()
            : JsonSerializer.Deserialize<RedditPageInspection>(json, _jsonOptions) ?? new RedditPageInspection();

        return domState with
        {
            HttpStatusCode = navigationResponse?.Status
        };
    }

    private async Task AdvanceGalleryCarouselSlidesAsync(IPage page)
    {
        var hasCarousel = await page.EvaluateAsync<bool>(
            "() => Boolean(document.querySelector('gallery-carousel'))").ConfigureAwait(false);
        if (!hasCarousel)
        {
            return;
        }

        const int maxClicks = 20;
        for (int i = 0; i < maxClicks; i++)
        {
            var clicked = await page.EvaluateAsync<bool>(ClickGalleryNextScript).ConfigureAwait(false);
            if (!clicked)
            {
                break;
            }

            await _delayAsync(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
        }
    }

    private static async Task<List<RedditBrowserPost>> ExtractPostsFromPageAsync(IPage page)
    {
        var json = await page.EvaluateAsync<string>(ExtractPostsFromDomScript).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        return JsonSerializer.Deserialize<List<RedditBrowserPost>>(json, _jsonOptions) ?? [];
    }

    private static async Task ClosePageSafeAsync(IPage page)
    {
        if (page == null)
        {
            return;
        }

        try
        {
            await page.CloseAsync().ConfigureAwait(false);
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    private int GetTimeoutMs()
    {
        var seconds = _config.NavigationTimeoutSeconds > 0 ? _config.NavigationTimeoutSeconds : 45;
        return Math.Clamp(seconds, 5, 300) * 1000;
    }

    private string GetEffectiveStorageStatePath()
    {
        return string.IsNullOrWhiteSpace(_config.StorageStatePath)
            ? "reddit-state.json"
            : _config.StorageStatePath.Trim();
    }

    private static string ResolveWorkspacePath(string path)
    {
        return Path.IsPathRooted(path)
            ? path
            : Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), path));
    }

    private static string NormalizeBrowserName(string browserName)
    {
        return string.Equals(browserName?.Trim(), "firefox", StringComparison.OrdinalIgnoreCase)
            ? "firefox"
            : "chromium";
    }

    private static bool IsMissingBrowserError(PlaywrightException ex)
    {
        return ex.Message.Contains("Executable doesn't exist", StringComparison.OrdinalIgnoreCase) ||
               ex.Message.Contains("Please run the following command to download new browsers", StringComparison.OrdinalIgnoreCase) ||
               ex.Message.Contains("Failed to launch browser", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsAnyPhrase(string text, string[] phrases)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (var phrase in phrases)
        {
            if (text.Contains(phrase, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string BuildPostDetailUrl(string permalink)
    {
        var trimmed = permalink.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absoluteUri))
        {
            var host = absoluteUri.Host.Equals("old.reddit.com", StringComparison.OrdinalIgnoreCase)
                ? "old.reddit.com"
                : "www.reddit.com";
            return $"https://{host}{absoluteUri.PathAndQuery}";
        }

        var path = trimmed.StartsWith('/') ? trimmed : $"/{trimmed}";
        return $"https://www.reddit.com{path}";
    }

    private static string ExtractPostIdFromUrl(string url)
    {
        var match = Regex.Match(url ?? string.Empty, @"/comments/([a-z0-9]+)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    private const string WaitForPostsOrChallengeScript = """
        () => {
            if (document.querySelector(
                'shreddit-post, [data-testid="sdui-post-unit"], search-telemetry-tracker[data-testid="search-sdui-post"], ' +
                'reddit-search-large-card, div.thing.link, div.search-result-link, ' +
                'iframe[src*="recaptcha"], iframe[src*="hcaptcha"], iframe[src*="turnstile"], iframe[src*="arkoselabs"], ' +
                'div.g-recaptcha, .h-captcha, .cf-turnstile, #challenge-form, shreddit-captcha, reddit-captcha'
            )) {
                return true;
            }
            const text = ((document.body && document.body.innerText) || '').toLowerCase();
            return text.includes("there doesn't seem to be anything here") ||
                   text.includes("looks like there aren't any results") ||
                   text.includes("community not found") ||
                   text.includes("is a private community") ||
                   text.includes("has been banned from reddit") ||
                   text.includes("blocked by network security") ||
                   text.includes("whoa there, pardner") ||
                   text.includes("you must be 18+");
        }
        """;

    private const string InspectPageScript = """
        () => JSON.stringify({
            finalUrl: window.location.href || '',
            pageTitle: document.title || '',
            bodySnippet: (document.body ? (document.body.innerText || '') : '').slice(0, 4000),
            hasPosts: document.querySelectorAll(
                'shreddit-post, [data-testid="sdui-post-unit"], search-telemetry-tracker[data-testid="search-sdui-post"], ' +
                'reddit-search-large-card, div.thing.link, div.search-result-link, a[data-testid="post-title-text"], a[data-testid="post-title"]'
            ).length > 0,
            hasCaptchaOrChallengeElement: Boolean(document.querySelector(
                'iframe[src*="recaptcha"], iframe[src*="hcaptcha"], iframe[src*="turnstile"], iframe[src*="arkoselabs"], ' +
                'div.g-recaptcha, .h-captcha, .cf-turnstile, #challenge-form, shreddit-captcha, reddit-captcha'
            ))
        })
        """;

    private const string ClickGalleryNextScript = """
        () => {
            const carousel = document.querySelector('gallery-carousel');
            if (!carousel) return false;
            const roots = [carousel];
            if (carousel.shadowRoot) roots.push(carousel.shadowRoot);
            for (const root of roots) {
                const buttons = root.querySelectorAll('button[aria-label*="Next"], button[aria-label*="next"]');
                for (const btn of buttons) {
                    if (btn.disabled || btn.getAttribute('aria-disabled') === 'true') continue;
                    const style = window.getComputedStyle(btn);
                    if (style.display === 'none' || style.visibility === 'hidden' || style.opacity === '0') continue;
                    btn.click();
                    return true;
                }
            }
            return false;
        }
        """;

    private const string ExtractPostsFromDomScript = """
        () => {
            const posts = [];
            const seenIds = new Set();

            function isIgnoredAsset(url) {
                if (!url || typeof url !== 'string') return true;
                const lower = url.toLowerCase();
                return lower.startsWith('data:') ||
                       lower.startsWith('blob:') ||
                       lower.includes('styles.redditmedia.com') ||
                       lower.includes('redditstatic.com') ||
                       lower.includes('emoji.redditmedia.com') ||
                       lower.includes('avatar') ||
                       lower.includes('snoovatar') ||
                       lower.includes('award') ||
                       lower.includes('flair');
            }

            function pickBestFromSrcset(srcset) {
                if (!srcset) return null;
                let bestUrl = null;
                let bestWidth = -1;
                for (const entry of srcset.split(',')) {
                    const parts = entry.trim().split(/\s+/);
                    if (!parts[0]) continue;
                    const descriptor = parts[1] || '';
                    const width = descriptor.endsWith('w') ? parseInt(descriptor, 10) || 0 : 0;
                    if (width >= bestWidth) {
                        bestWidth = width;
                        bestUrl = parts[0];
                    }
                }
                return bestUrl;
            }

            function walkDeep(root, visitor, visited = new Set()) {
                if (!root || visited.has(root)) return;
                visited.add(root);
                visitor(root);
                if (root.shadowRoot) {
                    walkDeep(root.shadowRoot, visitor, visited);
                }
                const children = root.children || root.childNodes;
                if (children) {
                    for (const child of children) {
                        if (child.nodeType === 1) {
                            walkDeep(child, visitor, visited);
                        }
                    }
                }
            }

            function collectImgsDeep(root) {
                const urls = [];
                const seen = new Set();
                walkDeep(root, (node) => {
                    if (node.tagName === 'IMG') {
                        if (node.getAttribute('aria-hidden') === 'true') return;
                        const cls = (node.className && typeof node.className === 'string') ? node.className.toLowerCase() : '';
                        if (cls.includes('blur') || cls.includes('ambient')) return;

                        const candidates = [
                            pickBestFromSrcset(node.getAttribute('srcset')),
                            node.getAttribute('data-lazy-src'),
                            node.dataset ? node.dataset.lazySrc : null,
                            node.getAttribute('src'),
                            node.currentSrc
                        ].filter(c => c && !isIgnoredAsset(c) && !c.toLowerCase().includes('blur='));

                        if (candidates.length > 0) {
                            const chosen = candidates[0];
                            if (!seen.has(chosen)) {
                                seen.add(chosen);
                                urls.push(chosen);
                            }
                        }
                    }
                });
                return urls;
            }

            function extractVideoUrlsDeep(root) {
                const urls = [];
                const seen = new Set();
                const addUrl = (u) => {
                    if (u && typeof u === 'string' && !u.startsWith('blob:') && !seen.has(u)) {
                        seen.add(u);
                        urls.push(u);
                    }
                };
                walkDeep(root, (node) => {
                    const tag = node.tagName;
                    if (tag === 'SHREDDIT-PLAYER' || tag === 'SHREDDIT-PLAYER-2') {
                        const packaged = node.getAttribute('packaged-media-json');
                        if (packaged) {
                            try {
                                const data = JSON.parse(packaged);
                                const perms = data?.playbackMp4s?.permutations;
                                if (Array.isArray(perms) && perms.length > 0) {
                                    let best = null;
                                    let bestArea = -1;
                                    for (const p of perms) {
                                        const srcUrl = p?.source?.url;
                                        const dims = p?.source?.dimensions;
                                        const area = (dims?.width || 0) * (dims?.height || 0);
                                        if (srcUrl && area >= bestArea) {
                                            bestArea = area;
                                            best = srcUrl;
                                        }
                                    }
                                    if (best) addUrl(best);
                                }
                            } catch {}
                        }
                        const srcAttr = node.getAttribute('src');
                        if (srcAttr && /\.mp4(\?|$)/i.test(srcAttr)) addUrl(srcAttr);
                    } else if (tag === 'VIDEO' || tag === 'SOURCE') {
                        const srcAttr = node.getAttribute('src');
                        if (srcAttr && /\.mp4(\?|$)/i.test(srcAttr)) addUrl(srcAttr);
                    }
                });
                return urls;
            }

            function extractIdFromPermalink(permalink) {
                if (!permalink) return '';
                const m = permalink.match(/\/comments\/([a-z0-9]+)/i);
                return m ? m[1].toLowerCase() : '';
            }

            function normalizeIdAndFullname(rawId, permalink) {
                let id = (rawId || '').trim();
                if (id.startsWith('t3_')) id = id.substring(3);
                if (!id && permalink) id = extractIdFromPermalink(permalink);
                if (!id) return { id: '', fullname: '' };
                return { id, fullname: 't3_' + id };
            }

            // Layout 1: Shreddit feed and detail views (<shreddit-post>)
            const shredditPosts = document.querySelectorAll('shreddit-post');
            if (shredditPosts.length > 0) {
                for (const el of shredditPosts) {
                    const postType = (el.getAttribute('post-type') || el.getAttribute('content-type') || '').toLowerCase();
                    if (el.closest('shreddit-ad-post') || el.hasAttribute('is-promoted') || postType === 'promoted') {
                        continue;
                    }

                    const permalink = el.getAttribute('permalink')
                        || el.querySelector('a[slot="full-post-link"], a[href*="/comments/"]')?.getAttribute('href')
                        || '';
                    const rawId = el.getAttribute('id') || el.getAttribute('thing-id') || el.getAttribute('post-id') || '';
                    const { id, fullname } = normalizeIdAndFullname(rawId, permalink);
                    if (!id || seenIds.has(id)) continue;
                    seenIds.add(id);

                    const title = (el.getAttribute('post-title')
                        || el.querySelector('[slot="title"], h1, a[id^="post-title-"]')?.textContent
                        || '').trim();
                    const contentHref = el.getAttribute('content-href') || el.getAttribute('url') || '';
                    const isStickied = el.hasAttribute('stickied')
                        || el.getAttribute('is-stickied') === 'true'
                        || Boolean(el.querySelector('[slot="stickied-post-badge"], [icon-name="pin-fill"]'));
                    const isSelf = postType === 'text' || postType === 'self';

                    const carousel = el.querySelector('gallery-carousel');
                    const isGallery = postType === 'gallery' || /\/gallery\//i.test(contentHref) || carousel !== null;
                    const videoUrls = extractVideoUrlsDeep(el);

                    let mediaUrls = [];
                    let thumbnailUrl = '';
                    let needsDetailPage = false;

                    if (isGallery) {
                        mediaUrls = carousel ? collectImgsDeep(carousel) : [];
                        const declaredTotal = carousel ? carousel.querySelectorAll('li').length : 0;
                        if (mediaUrls.length < 2 || (declaredTotal > 1 && mediaUrls.length < declaredTotal)) {
                            needsDetailPage = true;
                        }
                    } else {
                        const imgUrls = collectImgsDeep(el);
                        const preferredImgs = imgUrls.filter(u => /i\.redd\.it|preview\.redd\.it/i.test(u));
                        const chosenImgs = preferredImgs.length > 0 ? preferredImgs : imgUrls;

                        if (videoUrls.length > 0) {
                            mediaUrls.push(videoUrls[0]);
                        } else if (chosenImgs.length > 0) {
                            mediaUrls.push(chosenImgs[0]);
                            thumbnailUrl = chosenImgs[0];
                        }

                        const hasDirectContentMedia = /\.(jpg|jpeg|png|gif|webp|mp4)(\?|$)/i.test(contentHref);
                        if (!isSelf && mediaUrls.length === 0 && !hasDirectContentMedia) {
                            needsDetailPage = true;
                        }
                    }

                    posts.push({
                        id,
                        fullname,
                        title,
                        permalink,
                        contentHref,
                        postType,
                        isStickied,
                        isGallery,
                        isSelf,
                        needsDetailPage,
                        mediaUrls,
                        thumbnailUrl
                    });
                }

                return JSON.stringify(posts);
            }

            // Layout 2: Old Reddit (old.reddit.com)
            const oldLinks = document.querySelectorAll('div.thing.link, div.search-result-link');
            if (oldLinks.length > 0) {
                for (const el of oldLinks) {
                    if (el.classList.contains('promoted') || el.classList.contains('promotedlink')) {
                        continue;
                    }

                    const titleLink = el.querySelector('a.title, a.search-title');
                    const permalink = el.getAttribute('data-permalink')
                        || el.querySelector('a.comments, a.search-comments')?.getAttribute('href')
                        || titleLink?.getAttribute('href')
                        || '';
                    const rawId = el.getAttribute('data-fullname') || '';
                    const { id, fullname } = normalizeIdAndFullname(rawId, permalink);
                    if (!id || seenIds.has(id)) continue;
                    seenIds.add(id);

                    const title = (titleLink?.textContent || '').trim();
                    const contentHref = el.getAttribute('data-url')
                        || el.querySelector('a.search-link')?.getAttribute('href')
                        || titleLink?.getAttribute('href')
                        || '';
                    const isStickied = el.classList.contains('stickied') || Boolean(el.querySelector('.stickied-tagline'));
                    const isSelf = el.classList.contains('self') || (!contentHref || contentHref.includes('/comments/'));
                    const isGallery = /\/gallery\//i.test(contentHref) || Boolean(el.querySelector('.media-gallery, .gallery-preview'));

                    let mediaUrls = [];
                    const imgs = collectImgsDeep(el).filter(u => !u.includes('thumbs.redditmedia.com'));
                    const thumbnailUrl = imgs.length > 0 ? imgs[0] : '';

                    const isVideo = /v\.redd\.it/i.test(contentHref) || Boolean(el.querySelector('[data-hls-url], [data-seek-url]'));
                    const hasDirectContentMedia = /\.(jpg|jpeg|png|gif|webp|mp4)(\?|$)/i.test(contentHref);
                    if (isGallery && imgs.length > 1) {
                        mediaUrls = imgs;
                    } else if (hasDirectContentMedia) {
                        mediaUrls = [contentHref];
                    }

                    const needsDetailPage = (isGallery && mediaUrls.length < 2) || isVideo || (!isSelf && mediaUrls.length === 0 && !hasDirectContentMedia);

                    posts.push({
                        id,
                        fullname,
                        title,
                        permalink,
                        contentHref,
                        postType: isGallery ? 'gallery' : (isVideo ? 'video' : (isSelf ? 'text' : 'link')),
                        isStickied,
                        isGallery,
                        isSelf,
                        needsDetailPage,
                        mediaUrls,
                        thumbnailUrl
                    });
                }

                return JSON.stringify(posts);
            }

            // Layout 3: Shreddit Search Results
            let searchContainers = Array.from(document.querySelectorAll(
                '[data-testid="sdui-post-unit"], search-telemetry-tracker[data-testid="search-sdui-post"], reddit-search-large-card'
            ));

            if (searchContainers.length === 0) {
                searchContainers = Array.from(document.querySelectorAll('a[data-testid="post-title-text"], a[data-testid="post-title"]'));
            }

            for (const unit of searchContainers) {
                const link = unit.tagName === 'A'
                    ? unit
                    : (unit.querySelector('a[data-testid="post-title-text"], a[data-testid="post-title"], a[href*="/comments/"]'));
                if (!link) continue;

                const permalink = link.getAttribute('href') || '';
                const { id, fullname } = normalizeIdAndFullname('', permalink);
                if (!id || seenIds.has(id)) continue;
                seenIds.add(id);

                const title = (link.textContent || link.getAttribute('aria-label') || '').trim();
                const container = unit.tagName === 'A'
                    ? (unit.closest('[data-testid="search-post-unit"], [data-testid="sdui-post-unit"], article, div') || unit)
                    : unit;
                const imgs = collectImgsDeep(container);
                const thumbnailUrl = imgs.length > 0 ? imgs[0] : '';

                posts.push({
                    id,
                    fullname,
                    title,
                    permalink,
                    contentHref: '',
                    postType: 'search',
                    isStickied: false,
                    isGallery: false,
                    isSelf: false,
                    needsDetailPage: true,
                    mediaUrls: [],
                    thumbnailUrl
                });
            }

            return JSON.stringify(posts);
        }
        """;
}
