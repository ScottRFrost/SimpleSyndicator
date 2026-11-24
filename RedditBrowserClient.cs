using GhostCursorSharp;
using Microsoft.Playwright;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SimpleSyndicator;

public sealed class RedditBrowserException(
	string message,
	int? statusCode = null,
	bool shouldAbortRun = false,
	Exception innerException = null) : InvalidOperationException(message, innerException)
{
	public int? StatusCode { get; } = statusCode;
	public bool ShouldAbortRun { get; } = shouldAbortRun;
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
	public string CreatedUtc { get; init; } = string.Empty;
	public double? AgeHours { get; init; }
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
	private IPage _page;
	private PlaywrightGhostCursor _cursor;
	private HashSet<nint> _preLaunchBrowserWindowHandles;
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

	public static TimeSpan ComputeRandomDelay(int minSeconds = 2, int maxSeconds = 5)
	{
		if (maxSeconds <= 0)
		{
			return TimeSpan.Zero;
		}

		var effectiveMin = Math.Max(0, minSeconds);
		var effectiveMax = Math.Max(effectiveMin, maxSeconds);
		var minMs = effectiveMin * 1000.0;
		var maxMs = effectiveMax * 1000.0;
		var delayMs = minMs + (Random.Shared.NextDouble() * (maxMs - minMs));
		return TimeSpan.FromMilliseconds(Math.Round(delayMs));
	}

	public static RedditBrowserPost MergePostDetails(RedditBrowserPost summary, RedditBrowserPost detail)
	{
		if (summary == null)
		{
			return detail;
		}

		if (detail == null)
		{
			return summary;
		}

		return summary with
		{
			Title = !string.IsNullOrWhiteSpace(detail.Title) ? detail.Title : summary.Title,
			ContentHref = !string.IsNullOrWhiteSpace(detail.ContentHref) ? detail.ContentHref : summary.ContentHref,
			IsGallery = summary.IsGallery || detail.IsGallery,
			IsSelf = detail.IsSelf,
			MediaUrls = detail.MediaUrls is { Length: > 0 } ? detail.MediaUrls : summary.MediaUrls,
			ThumbnailUrl = !string.IsNullOrWhiteSpace(detail.ThumbnailUrl) ? detail.ThumbnailUrl : summary.ThumbnailUrl,
			CreatedUtc = !string.IsNullOrWhiteSpace(detail.CreatedUtc) ? detail.CreatedUtc : summary.CreatedUtc,
			AgeHours = detail.AgeHours ?? summary.AgeHours,
			NeedsDetailPage = false
		};
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

	public static double? GetPostAgeHours(RedditBrowserPost post, DateTimeOffset? nowUtc = null)
	{
		if (post == null)
		{
			return null;
		}

		if (!string.IsNullOrWhiteSpace(post.CreatedUtc) &&
			DateTimeOffset.TryParse(post.CreatedUtc, out var created))
		{
			return Math.Max(0.0, ((nowUtc ?? DateTimeOffset.UtcNow) - created.ToUniversalTime()).TotalHours);
		}

		if (post.AgeHours.HasValue)
		{
			return Math.Max(0.0, post.AgeHours.Value);
		}

		return null;
	}

	public static bool IsPostOlderThan(RedditBrowserPost post, double maxAgeHours, DateTimeOffset? nowUtc = null)
	{
		if (maxAgeHours <= 0)
		{
			return false;
		}

		var age = GetPostAgeHours(post, nowUtc);
		return age.HasValue && age.Value > maxAgeHours;
	}

	public static string FormatPostAge(RedditBrowserPost post, DateTimeOffset? nowUtc = null)
	{
		var age = GetPostAgeHours(post, nowUtc);
		if (!age.HasValue)
		{
			return "age unknown";
		}

		return !string.IsNullOrWhiteSpace(post?.CreatedUtc)
			? $"{age.Value:F1}h old ({post.CreatedUtc})"
			: $"{age.Value:F1}h old";
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

		if (!inspection.HasPosts && inspection.HasCaptchaOrChallengeElement)
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

			await TryMaximizeBrowserWindowAsync(page, NormalizeBrowserName(_config.Browser), headless: false).ConfigureAwait(false);

			var cursor = new PlaywrightGhostCursor(page, new GhostCursorOptions
			{
				Visible = true
			});

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

	public async Task RunInteractiveBrowserAsync(string initialUrl = null)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);

		var context = await EnsureContextAsync(forceHeadlessFalse: true).ConfigureAwait(false);
		var page = await context.NewPageAsync().ConfigureAwait(false);
		var pageClosedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		page.Close += (_, _) => pageClosedTcs.TrySetResult();

		try
		{
			var timeoutMs = GetTimeoutMs();
			page.SetDefaultNavigationTimeout(timeoutMs);
			page.SetDefaultTimeout(timeoutMs);

			await TryMaximizeBrowserWindowAsync(page, NormalizeBrowserName(_config.Browser), headless: false).ConfigureAwait(false);

			var cursor = new PlaywrightGhostCursor(page, new GhostCursorOptions
			{
				Visible = true
			});

			var targetUrl = string.IsNullOrWhiteSpace(initialUrl) ? "https://www.reddit.com/" : initialUrl.Trim();
			Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Opening {targetUrl} in browser...");
			var response = await page.GotoAsync(targetUrl, new PageGotoOptions
			{
				WaitUntil = WaitUntilState.DOMContentLoaded,
				Timeout = timeoutMs
			}).ConfigureAwait(false);

			Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Navigation completed (HTTP {response?.Status.ToString() ?? "unknown"}, Final URL: {page.Url}).");
			Console.WriteLine();
			Console.WriteLine("Browser is open for testing with the current session state. Keep the browser window open and press Enter here to save any updated cookies and exit (or close the browser tab)...");

			var consoleTask = Task.Run(() => Console.ReadLine());
			await Task.WhenAny(consoleTask, pageClosedTcs.Task).ConfigureAwait(false);

			if (string.IsNullOrWhiteSpace(_config.UserDataDir) && !page.IsClosed)
			{
				try
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

					Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Saved updated Reddit browser session state to '{statePath}'.");
				}
				catch
				{
					// Ignore if browser window was already closed concurrently
				}
			}
		}
		finally
		{
			await ClosePageSafeAsync(page).ConfigureAwait(false);
		}
	}

	public async Task<(IReadOnlyList<RedditBrowserPost> Posts, string NewestFullname)> BrowseListingAsync(
		string url,
		int limit,
		string stopBeforeFullname = null,
		Func<RedditBrowserPost, bool> shouldSkipPost = null,
		Func<RedditBrowserPost, Task> onPostVisitedAsync = null)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		ArgumentException.ThrowIfNullOrWhiteSpace(url);

		var effectiveLimit = Math.Clamp(limit > 0 ? limit : 25, 1, 100);
		var normalizedUrl = NormalizeBrowseUrl(url);
		var maxAgeHours = _config.MaxPostAgeHours > 0 ? _config.MaxPostAgeHours : 24;
		var supportsCursor = SupportsIncrementalCursor(normalizedUrl);
		var timeoutMs = GetTimeoutMs();

		var (page, cursor) = await EnsurePageAsync().ConfigureAwait(false);

		Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Browsing to post listing: {normalizedUrl}");
		var response = await page.GotoAsync(normalizedUrl, new PageGotoOptions
		{
			WaitUntil = WaitUntilState.DOMContentLoaded,
			Timeout = timeoutMs
		}).ConfigureAwait(false);

		await WaitForPostsOrChallengeAsync(page).ConfigureAwait(false);
		Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Loaded post listing (HTTP {response?.Status.ToString() ?? "unknown"}): {page.Url}");

		var inspection = await InspectPageAsync(page, response).ConfigureAwait(false);
		var blockCheck = EvaluatePageBlock(inspection);
		if (blockCheck.IsBlocked)
		{
			throw new RedditBrowserException(
				blockCheck.Reason,
				inspection.HttpStatusCode,
				blockCheck.ShouldAbortRun);
		}

		var collectedPosts = new List<RedditBrowserPost>(effectiveLimit);
		var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		string newestFullname = null;
		var hitCursor = false;
		var hitAgeLimit = false;
		var consecutiveEmptyPasses = 0;
		var maxScrollPasses = Math.Max(0, _config.MaxScrollPasses);

		for (int pass = 0; pass <= maxScrollPasses; pass++)
		{
			var batch = await ExtractPostsFromPageAsync(page).ConfigureAwait(false);

			if (pass == 0 && batch.Count > 0)
			{
				var topPost = batch.FirstOrDefault(p =>
					!p.IsStickied &&
					!IsPostOlderThan(p, maxAgeHours) &&
					!string.IsNullOrWhiteSpace(p.Fullname));
				newestFullname = topPost?.Fullname;
			}

			var addedThisPass = 0;
			var newDomPostsThisPass = 0;
			foreach (var post in batch)
			{
				if (string.IsNullOrWhiteSpace(post.Id))
				{
					continue;
				}

				if (!string.IsNullOrWhiteSpace(stopBeforeFullname) &&
					string.Equals(post.Fullname, stopBeforeFullname, StringComparison.OrdinalIgnoreCase))
				{
					Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Reached previously seen post {stopBeforeFullname}; stopping listing pagination.");
					hitCursor = true;
					break;
				}

				if (!seenIds.Add(post.Id))
				{
					continue;
				}

				newDomPostsThisPass++;

				if (IsPostOlderThan(post, maxAgeHours))
				{
					if (post.IsStickied)
					{
						Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Ignoring stickied post '{post.Title}' ({post.Fullname}) because it is {FormatPostAge(post)} (> {maxAgeHours}h limit).");
						continue;
					}

					Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Reached post '{post.Title}' ({post.Fullname}) which is {FormatPostAge(post)} (> {maxAgeHours}h limit); stopping listing pagination.");
					hitAgeLimit = true;
					break;
				}

				var hasDirectMediaHref = Regex.IsMatch(post.ContentHref ?? string.Empty, @"\.(jpg|jpeg|png|gif|webp|mp4)(\?|$)", RegexOptions.IgnoreCase);
				if (post.IsSelf && !post.IsGallery && (post.MediaUrls?.Length ?? 0) == 0 && !hasDirectMediaHref)
				{
					Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Skipping text self-post '{post.Title}' ({post.Fullname}).");
					continue;
				}

				if (shouldSkipPost != null && shouldSkipPost(post))
				{
					Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Skipping already-syndicated post '{post.Title}' ({post.Fullname}).");
					continue;
				}

				var shouldOpenPostPage = !string.IsNullOrWhiteSpace(post.Permalink) &&
					(onPostVisitedAsync != null || (_config.ExpandPostDetails && post.NeedsDetailPage));

				if (shouldOpenPostPage)
				{
					Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Clicking post [{collectedPosts.Count + 1}/{effectiveLimit}] '{post.Title}' ({post.Fullname}, {FormatPostAge(post)}, type={post.PostType}, gallery={post.IsGallery}) -> {post.Permalink}");
					double lastScrollY = await GetScrollYAsync(page).ConfigureAwait(false);
					var navigatedToPost = false;

					try
					{
						var expectedPostId = !string.IsNullOrWhiteSpace(post.Id)
							? post.Id
							: ExtractPostIdFromUrl(post.Permalink);

						lastScrollY = await ClickPostFromListingAsync(page, cursor, post, expectedPostId).ConfigureAwait(false);
						navigatedToPost = true;

						var (detailPost, detailTooOld) = await InspectCurrentPostDetailAsync(
							page,
							cursor,
							expectedPostId,
							maxAgeHours,
							sitOnPostPage: true).ConfigureAwait(false);

						if (detailTooOld)
						{
							await NavigateBackToListingAsync(page, normalizedUrl, lastScrollY).ConfigureAwait(false);
							navigatedToPost = false;

							if (supportsCursor && !post.IsStickied)
							{
								Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Reached post older than {maxAgeHours} hours on new-sorted source; stopping remaining post processing.");
								hitAgeLimit = true;
								break;
							}

							continue;
						}

						var resolvedPost = detailPost != null ? MergePostDetails(post, detailPost) : post;
						collectedPosts.Add(resolvedPost);
						addedThisPass++;

						if (onPostVisitedAsync != null)
						{
							await onPostVisitedAsync(resolvedPost).ConfigureAwait(false);
						}

						await NavigateBackToListingAsync(page, normalizedUrl, lastScrollY).ConfigureAwait(false);
						navigatedToPost = false;
					}
					catch (RedditBrowserException ex) when (!ex.ShouldAbortRun)
					{
						Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Skipping post '{post.Title}': {ex.Message}");
						if (navigatedToPost)
						{
							await NavigateBackToListingAsync(page, normalizedUrl, lastScrollY).ConfigureAwait(false);
						}
					}
					catch (Exception ex) when (ex is not RedditBrowserException)
					{
						Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Failed processing post '{post.Permalink}': {ex.Message}");
						if (navigatedToPost)
						{
							await NavigateBackToListingAsync(page, normalizedUrl, lastScrollY).ConfigureAwait(false);
						}
					}
				}
				else
				{
					collectedPosts.Add(post);
					addedThisPass++;
					Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Processed post [{collectedPosts.Count}/{effectiveLimit}] '{post.Title}' ({post.Fullname}, {FormatPostAge(post)}, type={post.PostType}, gallery={post.IsGallery}).");

					if (onPostVisitedAsync != null)
					{
						await onPostVisitedAsync(post).ConfigureAwait(false);
					}
				}

				if (collectedPosts.Count >= effectiveLimit)
				{
					break;
				}
			}

			Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Listing pass {pass + 1}/{maxScrollPasses + 1}: {batch.Count} post(s) visible in DOM, {addedThisPass} new eligible post(s) processed ({collectedPosts.Count}/{effectiveLimit} total).");

			if (hitCursor || hitAgeLimit || collectedPosts.Count >= effectiveLimit || pass == maxScrollPasses)
			{
				break;
			}

			if (newDomPostsThisPass == 0)
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

			Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Scrolling post listing for more posts (pass {pass + 1}/{maxScrollPasses})...");

			// Human-like scroll via GhostCursor
			try
			{
				await cursor.MoveToAsync(new GhostCursorSharp.Vector(
					Random.Shared.Next(180, 1260),
					Random.Shared.Next(120, 720))).ConfigureAwait(false);
			}
			catch
			{
				// Continue even if move fails
			}

			await cursor.ScrollAsync(new GhostCursorSharp.Vector(0, Random.Shared.Next(650, 1450))).ConfigureAwait(false);

			var scrollWaitMs = Random.Shared.Next(900, 1900);
			await _delayAsync(TimeSpan.FromMilliseconds(scrollWaitMs)).ConfigureAwait(false);
		}

		return (collectedPosts, newestFullname);
	}

	public async Task<RedditBrowserPost> BrowsePostDetailAsync(string permalink)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		ArgumentException.ThrowIfNullOrWhiteSpace(permalink);

		var targetUrl = BuildPostDetailUrl(permalink);
		var expectedPostId = ExtractPostIdFromUrl(targetUrl);
		var maxAgeHours = _config.MaxPostAgeHours > 0 ? _config.MaxPostAgeHours : 24;
		var timeoutMs = GetTimeoutMs();

		var (page, cursor) = await EnsurePageAsync().ConfigureAwait(false);

		Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Opening post detail page: {targetUrl}");
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

		var (detailPost, _) = await InspectCurrentPostDetailAsync(
			page,
			cursor,
			expectedPostId,
			maxAgeHours,
			sitOnPostPage: false).ConfigureAwait(false);

		return detailPost;
	}

	private async Task<double> ClickPostFromListingAsync(
		IPage page,
		PlaywrightGhostCursor cursor,
		RedditBrowserPost post,
		string expectedPostId)
	{
		var targetUrl = BuildPostDetailUrl(post.Permalink);
		var timeoutMs = GetTimeoutMs();
		var previousUrl = page.Url;
		var listingScrollY = await GetScrollYAsync(page).ConfigureAwait(false);
		IResponse clickNavResponse = null;

		try
		{
			var json = await page.EvaluateAsync<string>(
				PreparePostLinkForClickScript,
				new { postId = post.Id ?? string.Empty, permalink = post.Permalink ?? string.Empty }).ConfigureAwait(false);

			var linkInfo = string.IsNullOrWhiteSpace(json)
				? null
				: JsonSerializer.Deserialize<ClickablePointInfo>(json, _jsonOptions);

			listingScrollY = await GetScrollYAsync(page).ConfigureAwait(false);

			if (linkInfo is { Found: true })
			{
				try
				{
					await cursor.MoveToAsync(
						new GhostCursorSharp.Vector(linkInfo.X, linkInfo.Y),
						new MoveToOptions { MoveSpeed = 35 }).ConfigureAwait(false);
				}
				catch
				{
					// Continue to click even if GhostCursor movement fails
				}

				await _delayAsync(TimeSpan.FromMilliseconds(Random.Shared.Next(120, 260))).ConfigureAwait(false);
				await page.Mouse.ClickAsync((float)linkInfo.X, (float)linkInfo.Y).ConfigureAwait(false);

				try
				{
					await page.WaitForURLAsync(
						url => !string.Equals(url, previousUrl, StringComparison.OrdinalIgnoreCase) &&
							   (url.Contains("/comments/", StringComparison.OrdinalIgnoreCase) ||
							    (!string.IsNullOrEmpty(expectedPostId) && url.Contains(expectedPostId, StringComparison.OrdinalIgnoreCase))),
						new PageWaitForURLOptions
						{
							Timeout = 6000,
							WaitUntil = WaitUntilState.DOMContentLoaded
						}).ConfigureAwait(false);
				}
				catch (TimeoutException)
				{
					// Fall back to direct same-tab navigation below if link click did not navigate
				}
			}
		}
		catch (PlaywrightException)
		{
			// Fall back to direct same-tab navigation below
		}

		if (string.Equals(page.Url, previousUrl, StringComparison.OrdinalIgnoreCase) ||
			!page.Url.Contains("/comments/", StringComparison.OrdinalIgnoreCase))
		{
			Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Navigating current tab to post detail: {targetUrl}");
			clickNavResponse = await page.GotoAsync(targetUrl, new PageGotoOptions
			{
				WaitUntil = WaitUntilState.DOMContentLoaded,
				Timeout = timeoutMs
			}).ConfigureAwait(false);
		}

		await WaitForPostsOrChallengeAsync(page).ConfigureAwait(false);

		var inspection = await InspectPageAsync(page, clickNavResponse).ConfigureAwait(false);
		var blockCheck = EvaluatePageBlock(inspection);
		if (blockCheck.IsBlocked)
		{
			throw new RedditBrowserException(
				blockCheck.Reason,
				inspection.HttpStatusCode,
				blockCheck.ShouldAbortRun);
		}

		return listingScrollY;
	}

	private async Task<(RedditBrowserPost DetailPost, bool IsTooOld)> InspectCurrentPostDetailAsync(
		IPage page,
		PlaywrightGhostCursor cursor,
		string expectedPostId,
		int maxAgeHours,
		bool sitOnPostPage)
	{
		var initialPosts = await ExtractPostsFromPageAsync(page).ConfigureAwait(false);
		var initialPost = SelectPrimaryPost(initialPosts, expectedPostId);
		if (initialPost != null && IsPostOlderThan(initialPost, maxAgeHours))
		{
			Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Post '{initialPost.Title}' ({initialPost.Fullname}) is {FormatPostAge(initialPost)} (> {maxAgeHours}h limit); skipping gallery/detail extraction.");
			return (initialPost, true);
		}

		var postLabel = !string.IsNullOrWhiteSpace(expectedPostId) ? expectedPostId : (initialPost?.Id ?? page.Url);
		await RevealSpoilerIfPresentAsync(page, cursor, expectedPostId, postLabel).ConfigureAwait(false);

		if (sitOnPostPage)
		{
			var sitDelay = ComputeRandomDelay(2, 5);
			if (sitDelay > TimeSpan.Zero)
			{
				var label = !string.IsNullOrWhiteSpace(initialPost?.Title) ? initialPost.Title : expectedPostId;
				Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Viewing post '{label}' for {sitDelay.TotalSeconds:F1}s...");
				await _delayAsync(sitDelay).ConfigureAwait(false);
			}
		}
		var galleryUrls = await CollectAndPageGalleryImagesAsync(
			page,
			cursor,
			postLabel,
			initialPost?.IsGallery == true).ConfigureAwait(false);

		var posts = await ExtractPostsFromPageAsync(page).ConfigureAwait(false);
		var detailPost = SelectPrimaryPost(posts, expectedPostId) ?? initialPost;
		if (detailPost == null)
		{
			return (null, false);
		}

		if (galleryUrls.Count > 0)
		{
			var mergedUrls = MergeNormalizedMediaUrls(galleryUrls, detailPost.MediaUrls);
			if (mergedUrls.Count > 0)
			{
				detailPost = detailPost with
				{
					IsGallery = true,
					MediaUrls = [.. mergedUrls]
				};
			}
		}

		Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Finished inspecting post '{detailPost.Title}' ({detailPost.Fullname}): extracted {detailPost.MediaUrls?.Length ?? 0} media URL(s) (gallery={detailPost.IsGallery}).");
		return (detailPost, false);
	}

	private async Task NavigateBackToListingAsync(IPage page, string normalizedListingUrl, double lastScrollY)
	{
		var timeoutMs = GetTimeoutMs();
		Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Navigating back to post list via Browser Back...");

		IResponse backResponse = null;
		try
		{
			backResponse = await page.GoBackAsync(new PageGoBackOptions
			{
				WaitUntil = WaitUntilState.Commit,
				Timeout = Math.Min(timeoutMs, 15000)
			}).ConfigureAwait(false);
		}
		catch (TimeoutException)
		{
			// Handle SPA pushState or bfcache back transitions that do not emit a document navigation
		}
		catch (PlaywrightException)
		{
			// Verified via URL check below
		}

		await _delayAsync(TimeSpan.FromMilliseconds(350)).ConfigureAwait(false);

		if (page.Url.Contains("/comments/", StringComparison.OrdinalIgnoreCase) ||
			page.Url.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
		{
			Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Browser Back remained on '{page.Url}'; navigating back to listing URL: {normalizedListingUrl}");
			backResponse = await page.GotoAsync(normalizedListingUrl, new PageGotoOptions
			{
				WaitUntil = WaitUntilState.DOMContentLoaded,
				Timeout = timeoutMs
			}).ConfigureAwait(false);
		}

		await WaitForPostsOrChallengeAsync(page).ConfigureAwait(false);

		if (lastScrollY > 150)
		{
			var currentScrollY = await GetScrollYAsync(page).ConfigureAwait(false);
			if (currentScrollY < 100)
			{
				try
				{
					await page.EvaluateAsync("(y) => window.scrollTo({ top: y, behavior: 'instant' })", lastScrollY).ConfigureAwait(false);
					await _delayAsync(TimeSpan.FromMilliseconds(400)).ConfigureAwait(false);
				}
				catch
				{
					// Best-effort scroll restore
				}
			}
		}

		var inspection = await InspectPageAsync(page, backResponse).ConfigureAwait(false);
		var blockCheck = EvaluatePageBlock(inspection);
		if (blockCheck.IsBlocked)
		{
			throw new RedditBrowserException(
				blockCheck.Reason,
				inspection.HttpStatusCode,
				blockCheck.ShouldAbortRun);
		}
	}

	private static async Task<double> GetScrollYAsync(IPage page)
	{
		try
		{
			return await page.EvaluateAsync<double>("() => window.scrollY || window.pageYOffset || 0").ConfigureAwait(false);
		}
		catch
		{
			return 0;
		}
	}

	public static IReadOnlyList<string> GetDownloadCandidateUrls(string mediaUrl, string fallbackMediaUrl = null)
	{
		var candidates = new List<string>();
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		void AddCandidate(string candidate)
		{
			if (!string.IsNullOrWhiteSpace(candidate))
			{
				var trimmed = candidate.Trim();
				if (seen.Add(trimmed))
				{
					candidates.Add(trimmed);
				}
			}
		}

		if (!string.IsNullOrWhiteSpace(mediaUrl))
		{
			AddCandidate(RedditSource.GetHighResImageUrl(mediaUrl));
			AddCandidate(mediaUrl);
		}

		AddCandidate(fallbackMediaUrl);

		if (!string.IsNullOrWhiteSpace(mediaUrl))
		{
			AddCandidate(RedditSource.GetSignedPreviewFallbackUrl(mediaUrl));
		}

		return candidates;
	}

	public async Task DownloadMediaAsync(
		string mediaUrl,
		Stream destination,
		string refererUrl = null,
		string fallbackMediaUrl = null)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		ArgumentException.ThrowIfNullOrWhiteSpace(mediaUrl);
		ArgumentNullException.ThrowIfNull(destination);

		var effectiveReferer = !string.IsNullOrWhiteSpace(refererUrl)
			? refererUrl.Trim()
			: (_page is { IsClosed: false } &&
			   !string.IsNullOrWhiteSpace(_page.Url) &&
			   _page.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
			   _page.Url.Contains("reddit.com", StringComparison.OrdinalIgnoreCase)
				? _page.Url
				: "https://www.reddit.com/");

		var candidates = GetDownloadCandidateUrls(mediaUrl, fallbackMediaUrl);
		var context = await EnsureContextAsync().ConfigureAwait(false);
		Exception lastError = null;

		for (int i = 0; i < candidates.Count; i++)
		{
			var candidateUrl = candidates[i];
			try
			{
				var response = await context.APIRequest.GetAsync(candidateUrl, new APIRequestContextOptions
				{
					Timeout = 30000,
					Headers = new Dictionary<string, string>
					{
						["Referer"] = effectiveReferer,
						["Accept"] = "image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8"
					}
				}).ConfigureAwait(false);

				await using (response)
				{
					if (!response.Ok)
					{
						throw new InvalidOperationException($"Playwright media request failed ({response.Status} {response.StatusText}) for '{candidateUrl}'.");
					}

					if (response.Headers.TryGetValue("content-type", out var contentType) &&
						!string.IsNullOrWhiteSpace(contentType) &&
						contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
					{
						throw new InvalidOperationException($"Playwright media request returned HTML instead of media for '{candidateUrl}'.");
					}

					var bytes = await response.BodyAsync().ConfigureAwait(false);
					await destination.WriteAsync(bytes).ConfigureAwait(false);
					return;
				}
			}
			catch (Exception ex) when (ex is InvalidOperationException || ex is PlaywrightException || ex is TimeoutException)
			{
				lastError = ex;
				if (destination.CanSeek)
				{
					destination.SetLength(0);
				}

				if (i + 1 < candidates.Count)
				{
					Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Candidate media URL failed ({ex.Message}); trying fallback URL '{candidates[i + 1]}'...");
				}
			}
		}

		throw lastError ?? new InvalidOperationException($"Playwright media request failed for '{mediaUrl}'.");
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

		await ClosePageSafeAsync(_page).ConfigureAwait(false);
		_page = null;
		_cursor = null;

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

	public static string[] BuildLaunchArgs(string browserName, bool headless, string[] extraArgs)
	{
		List<string> args = [];
		if (extraArgs != null)
		{
			foreach (var arg in extraArgs)
			{
				if (!string.IsNullOrWhiteSpace(arg))
				{
					args.Add(arg.Trim());
				}
			}
		}

		if (!headless &&
			string.Equals(browserName, "chromium", StringComparison.OrdinalIgnoreCase) &&
			!args.Any(a => a.Equals("--start-maximized", StringComparison.OrdinalIgnoreCase)))
		{
			args.Add("--start-maximized");
		}

		return args.Count > 0 ? [.. args] : null;
	}

	private async Task<(IPage Page, PlaywrightGhostCursor Cursor)> EnsurePageAsync(bool forceHeadlessFalse = false)
	{
		var context = await EnsureContextAsync(forceHeadlessFalse).ConfigureAwait(false);
		if (_page != null && !_page.IsClosed && _cursor != null)
		{
			return (_page, _cursor);
		}

		var headless = !forceHeadlessFalse && _config.Headless;
		var browserName = NormalizeBrowserName(_config.Browser);
		var page = await context.NewPageAsync().ConfigureAwait(false);
		var timeoutMs = GetTimeoutMs();
		page.SetDefaultNavigationTimeout(timeoutMs);
		page.SetDefaultTimeout(timeoutMs);

		await TryMaximizeBrowserWindowAsync(page, browserName, headless).ConfigureAwait(false);

		var cursor = new PlaywrightGhostCursor(page, new GhostCursorOptions
		{
			Visible = !headless
		});

		page.Close += (_, _) =>
		{
			if (ReferenceEquals(_page, page))
			{
				_page = null;
				_cursor = null;
			}
		};

		_page = page;
		_cursor = cursor;
		return (_page, _cursor);
	}

	private async Task TryMaximizeBrowserWindowAsync(IPage page, string browserName, bool headless)
	{
		if (headless || page == null || page.IsClosed)
		{
			return;
		}

		if (OperatingSystem.IsWindows())
		{
			var titleMarker = $"SimpleSyndicator-{Guid.NewGuid():N}";
			try
			{
				await page.EvaluateAsync("(t) => { document.title = t; }", titleMarker).ConfigureAwait(false);
			}
			catch
			{
				titleMarker = string.Empty;
			}

			try
			{
				for (int attempt = 0; attempt < 15; attempt++)
				{
					var result = WindowsWindowMaximizer.TryMaximizeWindow(
						titleMarker,
						browserName,
						_preLaunchBrowserWindowHandles,
						allowProcessFallback: attempt >= 5);

					if (result.Found)
					{
						if (result.Maximized)
						{
							Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Maximized browser window.");
						}

						return;
					}

					await _delayAsync(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
				}
			}
			finally
			{
				if (!string.IsNullOrEmpty(titleMarker) && !page.IsClosed)
				{
					try
					{
						await page.EvaluateAsync("(t) => { if (document.title === t) document.title = ''; }", titleMarker).ConfigureAwait(false);
					}
					catch
					{
						// Ignore cleanup failure
					}
				}
			}

			return;
		}

		try
		{
			await page.EvaluateAsync("() => { try { window.moveTo(0, 0); window.resizeTo(screen.availWidth, screen.availHeight); } catch {} }").ConfigureAwait(false);
		}
		catch
		{
			// Best-effort resize on non-Windows headed runs
		}
	}

	private static class WindowsWindowMaximizer
	{
		private const int SwMaximize = 3;

		private delegate bool EnumWindowsProc(nint hWnd, nint lParam);

#pragma warning disable SYSLIB1054 // Managed EnumWindowsProc callback uses standard DllImport
		[DllImport("user32.dll")]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

		[DllImport("user32.dll")]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool IsWindowVisible(nint hWnd);

		[DllImport("user32.dll")]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool IsZoomed(nint hWnd);

		[DllImport("user32.dll")]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool ShowWindow(nint hWnd, int nCmdShow);

		[DllImport("user32.dll", CharSet = CharSet.Unicode)]
		private static extern int GetWindowTextLength(nint hWnd);

		[DllImport("user32.dll", CharSet = CharSet.Unicode)]
		private static extern int GetWindowText(nint hWnd, [Out] char[] lpString, int nMaxCount);

		[DllImport("user32.dll")]
		private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);
#pragma warning restore SYSLIB1054

		public static HashSet<nint> SnapshotBrowserWindowHandles()
		{
			HashSet<nint> handles = [];
			try
			{
				EnumWindows((hWnd, _) =>
				{
					if (!IsWindowVisible(hWnd))
					{
						return true;
					}

					var procName = TryGetWindowProcessName(hWnd);
					if (IsAnyBrowserProcess(procName))
					{
						handles.Add(hWnd);
					}

					return true;
				}, 0);
			}
			catch
			{
				// Return partial snapshot cleanly
			}

			return handles;
		}

		public static (bool Found, bool Maximized) TryMaximizeWindow(
			string titleMarker,
			string browserName,
			HashSet<nint> preLaunchHandles,
			bool allowProcessFallback)
		{
			nint exactMatch = 0;
			nint newBrowserWindow = 0;

			try
			{
				EnumWindows((hWnd, _) =>
				{
					if (!IsWindowVisible(hWnd))
					{
						return true;
					}

					if (!string.IsNullOrEmpty(titleMarker))
					{
						var title = ReadWindowTitle(hWnd);
						if (!string.IsNullOrEmpty(title) && title.Contains(titleMarker, StringComparison.Ordinal))
						{
							exactMatch = hWnd;
							return false;
						}
					}

					if (allowProcessFallback &&
						newBrowserWindow == 0 &&
						(preLaunchHandles == null || !preLaunchHandles.Contains(hWnd)))
					{
						var procName = TryGetWindowProcessName(hWnd);
						if (IsExpectedBrowserProcess(procName, browserName))
						{
							newBrowserWindow = hWnd;
						}
					}

					return true;
				}, 0);
			}
			catch
			{
				return (false, false);
			}

			var target = exactMatch != 0 ? exactMatch : newBrowserWindow;
			if (target == 0)
			{
				return (false, false);
			}

			if (IsZoomed(target))
			{
				return (true, false);
			}

			ShowWindow(target, SwMaximize);
			return (true, true);
		}

		private static string ReadWindowTitle(nint hWnd)
		{
			var length = GetWindowTextLength(hWnd);
			if (length <= 0)
			{
				return string.Empty;
			}

			var buffer = new char[length + 1];
			var copied = GetWindowText(hWnd, buffer, buffer.Length);
			return copied > 0 ? new string(buffer, 0, copied) : string.Empty;
		}

		private static string TryGetWindowProcessName(nint hWnd)
		{
			try
			{
				_ = GetWindowThreadProcessId(hWnd, out var pid);
				if (pid == 0)
				{
					return string.Empty;
				}

				using var proc = Process.GetProcessById((int)pid);
				return proc.ProcessName;
			}
			catch
			{
				return string.Empty;
			}
		}

		private static bool IsAnyBrowserProcess(string processName)
		{
			return IsExpectedBrowserProcess(processName, "firefox") ||
				   IsExpectedBrowserProcess(processName, "chromium");
		}

		private static bool IsExpectedBrowserProcess(string processName, string browserName)
		{
			if (string.IsNullOrWhiteSpace(processName))
			{
				return false;
			}

			return string.Equals(browserName, "firefox", StringComparison.OrdinalIgnoreCase)
				? processName.Contains("firefox", StringComparison.OrdinalIgnoreCase)
				: processName.Contains("chrome", StringComparison.OrdinalIgnoreCase) ||
				  processName.Contains("chromium", StringComparison.OrdinalIgnoreCase) ||
				  processName.Contains("msedge", StringComparison.OrdinalIgnoreCase);
		}
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
			var args = BuildLaunchArgs(browserName, headless, _config.ExtraLaunchArgs);
			var channel = string.IsNullOrWhiteSpace(_config.BrowserChannel) ? null : _config.BrowserChannel.Trim();
			var executablePath = string.IsNullOrWhiteSpace(_config.ExecutablePath) ? null : _config.ExecutablePath.Trim();
			var viewportSize = headless ? new ViewportSize { Width = 1440, Height = 900 } : ViewportSize.NoViewport;

			if (!headless && OperatingSystem.IsWindows())
			{
				_preLaunchBrowserWindowHandles = WindowsWindowMaximizer.SnapshotBrowserWindowHandles();
			}

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
					ViewportSize = viewportSize
				}).ConfigureAwait(false);

				Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Using persistent browser profile directory '{userDataDir}'.");
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
					ViewportSize = viewportSize
				}).ConfigureAwait(false);

				if (_persistedStorageStateExistsAtStart)
				{
					var loadedCookies = await _context.CookiesAsync().ConfigureAwait(false);
					Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Loaded browser storage state from '{statePath}' ({loadedCookies.Count} cookies).");
				}
				else
				{
					Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Storage state file not found at '{statePath}'; starting with a fresh browser context.");
				}
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

	private sealed record ClickablePointInfo
	{
		public bool Found { get; init; }
		public double X { get; init; }
		public double Y { get; init; }
	}

	private sealed record SpoilerOverlayInfo
	{
		public bool Found { get; init; }
		public double X { get; init; }
		public double Y { get; init; }
		public string Label { get; init; } = string.Empty;
	}

	private async Task RevealSpoilerIfPresentAsync(
		IPage page,
		PlaywrightGhostCursor cursor,
		string expectedPostId,
		string postLabel)
	{
		for (int attempt = 0; attempt < 2; attempt++)
		{
			SpoilerOverlayInfo info;
			try
			{
				var json = await page.EvaluateAsync<string>(
					FindSpoilerOverlayScript,
					new { postId = expectedPostId ?? string.Empty }).ConfigureAwait(false);

				info = string.IsNullOrWhiteSpace(json)
					? null
					: JsonSerializer.Deserialize<SpoilerOverlayInfo>(json, _jsonOptions);
			}
			catch (PlaywrightException)
			{
				return;
			}

			if (info is not { Found: true })
			{
				return;
			}

			var labelText = string.IsNullOrWhiteSpace(info.Label) ? "spoiler overlay" : info.Label;
			Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Clicking spoiler overlay on post '{postLabel}' ({labelText}) to reveal image...");

			try
			{
				await cursor.MoveToAsync(
					new GhostCursorSharp.Vector(info.X, info.Y),
					new MoveToOptions { MoveSpeed = 35 }).ConfigureAwait(false);
			}
			catch
			{
				// Continue to click even if GhostCursor movement fails
			}

			await _delayAsync(TimeSpan.FromMilliseconds(Random.Shared.Next(120, 250))).ConfigureAwait(false);
			await page.Mouse.ClickAsync((float)info.X, (float)info.Y).ConfigureAwait(false);
			await _delayAsync(TimeSpan.FromMilliseconds(450)).ConfigureAwait(false);

			try
			{
				var recheckJson = await page.EvaluateAsync<string>(
					FindSpoilerOverlayScript,
					new { postId = expectedPostId ?? string.Empty }).ConfigureAwait(false);
				var remaining = string.IsNullOrWhiteSpace(recheckJson)
					? null
					: JsonSerializer.Deserialize<SpoilerOverlayInfo>(recheckJson, _jsonOptions);

				if (remaining is not { Found: true })
				{
					Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Revealed spoiler media on post '{postLabel}'.");
					return;
				}

				var domClicked = await page.EvaluateAsync<bool>(
					ClickSpoilerOverlayScript,
					new { postId = expectedPostId ?? string.Empty }).ConfigureAwait(false);

				if (domClicked)
				{
					await _delayAsync(TimeSpan.FromMilliseconds(400)).ConfigureAwait(false);
					var finalJson = await page.EvaluateAsync<string>(
						FindSpoilerOverlayScript,
						new { postId = expectedPostId ?? string.Empty }).ConfigureAwait(false);
					var finalRemaining = string.IsNullOrWhiteSpace(finalJson)
						? null
						: JsonSerializer.Deserialize<SpoilerOverlayInfo>(finalJson, _jsonOptions);

					if (finalRemaining is not { Found: true })
					{
						Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Revealed spoiler media on post '{postLabel}'.");
						return;
					}
				}
			}
			catch (PlaywrightException)
			{
				return;
			}
		}
	}

	private sealed record GalleryCarouselState
	{
		public bool HasCarousel { get; init; }
		public int TotalSlides { get; init; }
		public string[] ImageUrls { get; init; } = [];
		public ClickablePointInfo NextButton { get; init; } = new();
	}

	private static RedditBrowserPost SelectPrimaryPost(List<RedditBrowserPost> posts, string expectedPostId)
	{
		if (posts == null || posts.Count == 0)
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

	private static List<string> MergeNormalizedMediaUrls(IEnumerable<string> primaryUrls, IEnumerable<string> fallbackUrls)
	{
		var merged = new List<string>();
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		void AddRange(IEnumerable<string> urls)
		{
			if (urls == null)
			{
				return;
			}

			foreach (var raw in urls)
			{
				if (string.IsNullOrWhiteSpace(raw))
				{
					continue;
				}

				var decoded = System.Net.WebUtility.HtmlDecode(raw.Trim());
				var normalized = RedditSource.GetHighResImageUrl(decoded);
				if (RedditSource.IsMediaUrl(normalized) && seen.Add(normalized))
				{
					merged.Add(normalized);
				}
			}
		}

		AddRange(primaryUrls);
		if (merged.Count == 0)
		{
			AddRange(fallbackUrls);
		}

		return merged;
	}

	private async Task<List<string>> CollectAndPageGalleryImagesAsync(
		IPage page,
		PlaywrightGhostCursor cursor,
		string postId,
		bool knownGallery)
	{
		if (knownGallery)
		{
			try
			{
				await page.WaitForFunctionAsync(
					"() => Boolean(document.querySelector('gallery-carousel, .media-gallery'))",
					arg: null,
					new PageWaitForFunctionOptions
					{
						Timeout = 4000,
						PollingInterval = 200
					}).ConfigureAwait(false);
			}
			catch (TimeoutException)
			{
				// Proceed to DOM inspection cleanly
			}
			catch (PlaywrightException)
			{
				// Proceed cleanly
			}
		}

		var state = await InspectGalleryStateAsync(page).ConfigureAwait(false);
		if (!state.HasCarousel)
		{
			return [];
		}

		var orderedUrls = new List<string>();
		var seenNormalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		void AddHarvestedUrls(IEnumerable<string> rawUrls)
		{
			if (rawUrls == null)
			{
				return;
			}

			foreach (var raw in rawUrls)
			{
				if (string.IsNullOrWhiteSpace(raw))
				{
					continue;
				}

				var decoded = System.Net.WebUtility.HtmlDecode(raw.Trim());
				var normalized = RedditSource.GetHighResImageUrl(decoded);
				if (RedditSource.IsMediaUrl(normalized) && seenNormalized.Add(normalized))
				{
					orderedUrls.Add(normalized);
				}
			}
		}

		AddHarvestedUrls(state.ImageUrls);

		var totalSlides = Math.Max(state.TotalSlides, orderedUrls.Count);
		var maxClicks = totalSlides > 1 ? Math.Min(totalSlides - 1, 25) : 15;
		Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Gallery carousel found on post '{postId}' (expected slides: {(totalSlides > 0 ? totalSlides.ToString() : "unknown")}, initial images in DOM: {orderedUrls.Count}).");

		for (int clickIndex = 1; clickIndex <= maxClicks; clickIndex++)
		{
			if (totalSlides > 1 && orderedUrls.Count >= totalSlides && !state.NextButton.Found)
			{
				break;
			}

			if (!state.NextButton.Found)
			{
				Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Gallery 'Next' button is no longer active after {clickIndex - 1} click(s); finished paging gallery ({orderedUrls.Count} image(s) collected).");
				break;
			}

			Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Clicking gallery 'Next' button on post '{postId}' (slide {clickIndex + 1}/{(totalSlides > 1 ? totalSlides.ToString() : "?")})...");

			try
			{
				await cursor.MoveToAsync(
					new GhostCursorSharp.Vector(state.NextButton.X, state.NextButton.Y),
					new MoveToOptions { MoveSpeed = 40 }).ConfigureAwait(false);
			}
			catch
			{
				// Continue to DOM click even if cursor move fails
			}

			var clicked = await page.EvaluateAsync<bool>(ClickGalleryNextScript).ConfigureAwait(false);
			if (!clicked)
			{
				Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Gallery 'Next' button click returned false after {clickIndex - 1} click(s); stopping gallery paging.");
				break;
			}

			await _delayAsync(TimeSpan.FromMilliseconds(350)).ConfigureAwait(false);

			state = await InspectGalleryStateAsync(page).ConfigureAwait(false);
			var beforeCount = orderedUrls.Count;
			AddHarvestedUrls(state.ImageUrls);
			if (state.TotalSlides > totalSlides)
			{
				totalSlides = state.TotalSlides;
			}

			Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Gallery slide {clickIndex + 1} loaded (+{orderedUrls.Count - beforeCount} new, {orderedUrls.Count} total image(s)).");
		}

		return orderedUrls;
	}

	private static async Task<GalleryCarouselState> InspectGalleryStateAsync(IPage page)
	{
		var json = await page.EvaluateAsync<string>(InspectGalleryStateScript).ConfigureAwait(false);
		if (string.IsNullOrWhiteSpace(json))
		{
			return new GalleryCarouselState();
		}

		return JsonSerializer.Deserialize<GalleryCarouselState>(json, _jsonOptions) ?? new GalleryCarouselState();
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

	public static string BuildPostDetailUrl(string permalink)
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

	private const string SharedPageCheckJs = """
            const onPostDetailPage = /\/comments\//i.test(window.location.pathname || '');
            const postSelector = onPostDetailPage
                ? 'shreddit-post, div.thing.link, [data-test-id="post-content"]'
                : 'shreddit-post, [data-testid="sdui-post-unit"], search-telemetry-tracker[data-testid="search-sdui-post"], reddit-search-large-card, div.thing.link, div.search-result-link, a[data-testid="post-title-text"], a[data-testid="post-title"]';
            const hasPosts = document.querySelectorAll(postSelector).length > 0;

            function isContainerShown(el) {
                if (!el) return false;
                if (el.closest('[hidden], [aria-hidden="true"], .grecaptcha-badge')) return false;
                const style = window.getComputedStyle(el);
                return Boolean(style && style.display !== 'none' && style.visibility !== 'hidden' && parseFloat(style.opacity || '1') !== 0);
            }

            function isChallengeNodeVisible(el) {
                if (!isContainerShown(el)) return false;
                if (el.tagName === 'IFRAME') {
                    const src = (el.getAttribute('src') || '').toLowerCase();
                    if (src.includes('/aframe')) return false;
                }
                const rect = el.getBoundingClientRect();
                if (rect.width < 60 || rect.height < 40) return false;
                return rect.bottom > 0 && rect.right > 0 && rect.top < window.innerHeight && rect.left < window.innerWidth;
            }

            function hasVisibleNestedChallenge(root, visited = new Set()) {
                if (!root || visited.has(root)) return false;
                visited.add(root);
                const innerSelector = 'iframe[src*="recaptcha"], iframe[src*="hcaptcha"], iframe[src*="turnstile"], iframe[src*="arkoselabs"], #challenge-form, .g-recaptcha, .h-captcha, .cf-turnstile, [role="dialog"], form';
                if (root.querySelectorAll) {
                    for (const child of root.querySelectorAll(innerSelector)) {
                        if (isChallengeNodeVisible(child)) return true;
                    }
                }
                if (root.shadowRoot && hasVisibleNestedChallenge(root.shadowRoot, visited)) {
                    return true;
                }
                const children = root.children;
                if (children) {
                    for (const child of children) {
                        if (hasVisibleNestedChallenge(child, visited)) return true;
                    }
                }
                return false;
            }

            function hasVisibleChallenge() {
                const candidates = document.querySelectorAll(
                    'iframe[src*="recaptcha"], iframe[src*="hcaptcha"], iframe[src*="turnstile"], iframe[src*="arkoselabs"], ' +
                    'div.g-recaptcha, .h-captcha, .cf-turnstile, #challenge-form, shreddit-captcha, reddit-captcha'
                );
                for (const el of candidates) {
                    const tag = (el.tagName || '').toUpperCase();
                    if (tag === 'SHREDDIT-CAPTCHA' || tag === 'REDDIT-CAPTCHA') {
                        if (isContainerShown(el) && hasVisibleNestedChallenge(el)) {
                            return true;
                        }
                    } else if (isChallengeNodeVisible(el)) {
                        return true;
                    }
                }
                return false;
            }
            const hasCaptchaOrChallenge = hasVisibleChallenge();
        """;

	private const string WaitForPostsOrChallengeScript = "() => {\n" + SharedPageCheckJs + "\n" + """
            if (hasPosts || hasCaptchaOrChallenge) {
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

	private const string InspectPageScript = "() => {\n" + SharedPageCheckJs + "\n" + """
            return JSON.stringify({
                finalUrl: window.location.href || '',
                pageTitle: document.title || '',
                bodySnippet: (document.body ? (document.body.innerText || '') : '').slice(0, 4000),
                hasPosts: hasPosts,
                hasCaptchaOrChallengeElement: hasCaptchaOrChallenge
            });
        }
        """;

	private const string SharedSpoilerOverlayJs = """
            const cleanId = ((arg && arg.postId) || '').replace(/^t3_/i, '').toLowerCase();

            function findPrimaryPostRoot() {
                const posts = Array.from(document.querySelectorAll('shreddit-post'));
                if (cleanId) {
                    const matched = posts.find(el => {
                        const attrs = [
                            el.getAttribute('id') || '',
                            el.getAttribute('thing-id') || '',
                            el.getAttribute('post-id') || '',
                            el.getAttribute('permalink') || ''
                        ].join(' ').toLowerCase();
                        return attrs.includes(cleanId);
                    });
                    if (matched) return matched;
                }
                return posts[0] || document.querySelector('div.thing.link, [data-test-id="post-content"]');
            }

            function isElementClickableVisible(el) {
                if (!el) return false;
                if (el.disabled || el.getAttribute('aria-disabled') === 'true') return false;
                if (el.closest('[hidden], [aria-hidden="true"]')) return false;
                const style = window.getComputedStyle(el);
                if (!style || style.display === 'none' || style.visibility === 'hidden' || style.pointerEvents === 'none' || parseFloat(style.opacity || '1') <= 0.05) {
                    return false;
                }
                const rect = el.getBoundingClientRect();
                return rect.width > 12 && rect.height > 12;
            }

            function isSpoilerRevealText(text) {
                if (!text) return false;
                const clean = text.replace(/\s+/g, ' ').trim().toLowerCase();
                return clean.includes('view spoiler') ||
                       clean.includes('click to see spoiler') ||
                       clean.includes('click to view spoiler') ||
                       clean.includes('show spoiler') ||
                       clean.includes('reveal spoiler');
            }

            function walkOverlayDeep(root, visitor, visited = new Set()) {
                if (!root || visited.has(root)) return;
                visited.add(root);
                if (root.tagName === 'SHREDDIT-COMMENT' ||
                    (root.classList && root.classList.contains('commentarea')) ||
                    root.getAttribute?.('slot') === 'comment' ||
                    (root.tagName === 'SPAN' && root.classList?.contains('md-spoiler-text'))) {
                    return;
                }
                visitor(root);
                if (root.shadowRoot) {
                    walkOverlayDeep(root.shadowRoot, visitor, visited);
                }
                const children = root.children;
                if (children) {
                    for (const child of children) {
                        walkOverlayDeep(child, visitor, visited);
                    }
                }
            }

            function findButtonInBlurredContainer(container, requireSpoilerHint) {
                let preferredBtn = null;
                let fallbackBtn = null;
                walkOverlayDeep(container, (node) => {
                    if (node.getAttribute?.('slot') === 'revealed') return;
                    const tag = (node.tagName || '').toUpperCase();
                    if (tag === 'BUTTON' || node.getAttribute?.('role') === 'button') {
                        if (!isElementClickableVisible(node)) return;
                        const label = ((node.getAttribute('aria-label') || '') + ' ' + (node.textContent || '')).replace(/\s+/g, ' ').trim().toLowerCase();
                        if (isSpoilerRevealText(label) || (requireSpoilerHint && (label.includes('view') || label.includes('show') || label.includes('see')))) {
                            if (!preferredBtn) preferredBtn = node;
                        } else if (!fallbackBtn) {
                            fallbackBtn = node;
                        }
                    }
                });
                return preferredBtn || fallbackBtn;
            }

            function findSpoilerTarget() {
                const postRoot = findPrimaryPostRoot();
                if (!postRoot) return null;

                let explicitButton = null;
                let blurredCandidate = null;
                const postHasSpoilerAttr = postRoot.hasAttribute('spoiler') || postRoot.getAttribute('is-spoiler') === 'true';

                walkOverlayDeep(postRoot, (node) => {
                    if (explicitButton) return;
                    const tag = (node.tagName || '').toUpperCase();

                    if (tag === 'SHREDDIT-BLURRED-CONTAINER') {
                        const revealedAttr = node.getAttribute('is-revealed') ?? node.getAttribute('revealed');
                        if (node.hasAttribute('is-revealed') && revealedAttr !== 'false') return;
                        if (node.hasAttribute('revealed') && revealedAttr !== 'false') return;
                        if (!isElementClickableVisible(node)) return;

                        const reason = (node.getAttribute('reason') || '').toLowerCase();
                        const innerText = (node.textContent || '').toLowerCase();
                        const hasSpoilerSignal = reason.includes('spoiler') ||
                                                 innerText.includes('spoiler') ||
                                                 (postHasSpoilerAttr && (innerText.includes('view') || innerText.includes('click to see')));

                        const innerBtn = findButtonInBlurredContainer(node, hasSpoilerSignal);
                        if (innerBtn) {
                            blurredCandidate = {
                                element: innerBtn,
                                label: (innerBtn.textContent || innerBtn.getAttribute('aria-label') || 'View spoiler').replace(/\s+/g, ' ').trim()
                            };
                        } else if (hasSpoilerSignal) {
                            blurredCandidate = {
                                element: node,
                                label: 'Blurred spoiler container'
                            };
                        }
                        return;
                    }

                    if (tag === 'BUTTON' || node.getAttribute?.('role') === 'button') {
                        if (!isElementClickableVisible(node)) return;
                        const label = ((node.getAttribute('aria-label') || '') + ' ' + (node.textContent || '')).replace(/\s+/g, ' ').trim();
                        if (isSpoilerRevealText(label)) {
                            explicitButton = { element: node, label };
                        }
                    }
                });

                return explicitButton || blurredCandidate;
            }
        """;

	private const string FindSpoilerOverlayScript = "(arg) => {\n" + SharedSpoilerOverlayJs + "\n" + """
            const target = findSpoilerTarget();
            if (!target || !target.element) {
                return JSON.stringify({ found: false, x: 0, y: 0, label: '' });
            }

            target.element.scrollIntoView({ block: 'center', inline: 'nearest', behavior: 'instant' });
            const rect = target.element.getBoundingClientRect();
            const visible = rect.width > 8 && rect.height > 8 &&
                rect.bottom > 0 && rect.top < window.innerHeight &&
                rect.right > 0 && rect.left < window.innerWidth;

            return JSON.stringify({
                found: visible,
                x: rect.left + (rect.width * 0.5),
                y: rect.top + (rect.height * 0.5),
                label: target.label || 'View spoiler'
            });
        }
        """;

	private const string ClickSpoilerOverlayScript = "(arg) => {\n" + SharedSpoilerOverlayJs + "\n" + """
            const target = findSpoilerTarget();
            if (!target || !target.element) {
                return false;
            }
            target.element.scrollIntoView({ block: 'center', inline: 'nearest', behavior: 'instant' });
            target.element.click();
            return true;
        }
        """;

	private const string InspectGalleryStateScript = """
        () => {
            const carousel = document.querySelector('gallery-carousel');
            if (!carousel) {
                return JSON.stringify({ hasCarousel: false, totalSlides: 0, imageUrls: [], nextButton: { found: false, x: 0, y: 0 } });
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

            function collectSlideImages(root) {
                const urls = [];
                const seen = new Set();
                const addCandidate = (u) => {
                    if (u && !isIgnoredAsset(u) && !u.toLowerCase().includes('blur=') && !seen.has(u)) {
                        seen.add(u);
                        urls.push(u);
                    }
                };

                walkDeep(root, (node) => {
                    if (node.tagName === 'IMG') {
                        if (node.getAttribute('aria-hidden') === 'true') return;
                        const cls = (node.className && typeof node.className === 'string') ? node.className.toLowerCase() : '';
                        if (cls.includes('blur') || cls.includes('ambient')) return;

                        const candidates = [
                            pickBestFromSrcset(node.getAttribute('srcset')),
                            pickBestFromSrcset(node.getAttribute('data-lazy-srcset')),
                            pickBestFromSrcset(node.dataset ? node.dataset.lazySrcset : null),
                            node.getAttribute('data-lazy-src'),
                            node.dataset ? node.dataset.lazySrc : null,
                            node.getAttribute('src'),
                            node.currentSrc
                        ];
                        for (const c of candidates) {
                            if (c && !isIgnoredAsset(c) && !c.toLowerCase().includes('blur=')) {
                                addCandidate(c);
                                break;
                            }
                        }
                    } else if (node.tagName === 'A') {
                        const href = node.getAttribute('href') || '';
                        if (/\.(jpg|jpeg|png|gif|webp)(\?|$)/i.test(href)) {
                            addCandidate(href);
                        }
                    }
                });

                return urls;
            }

            let slideNodes = Array.from(carousel.querySelectorAll('li[slot^="page-"], ul > li'));
            if (slideNodes.length === 0) {
                slideNodes = Array.from(carousel.querySelectorAll('figure'));
            }

            let totalSlides = slideNodes.length;
            walkDeep(carousel, (node) => {
                if (node.children && node.children.length > 0) return;
                const txt = (node.textContent || '').trim();
                const m = txt.match(/^\d+\s*\/\s*(\d+)$/);
                if (m) {
                    const count = parseInt(m[1], 10);
                    if (count > totalSlides) totalSlides = count;
                }
            });

            const imageUrls = [];
            const seenImages = new Set();
            const addImageUrl = (u) => {
                if (u && !seenImages.has(u)) {
                    seenImages.add(u);
                    imageUrls.push(u);
                }
            };

            if (slideNodes.length > 0) {
                for (const slide of slideNodes) {
                    for (const u of collectSlideImages(slide)) {
                        addImageUrl(u);
                    }
                }
            } else {
                for (const u of collectSlideImages(carousel)) {
                    addImageUrl(u);
                }
            }

            function isButtonVisibleAndEnabled(btn) {
                if (!btn || btn.disabled || btn.getAttribute('aria-disabled') === 'true') return false;
                if (btn.closest('[hidden], [aria-hidden="true"]')) return false;
                const style = window.getComputedStyle(btn);
                if (style.display === 'none' || style.visibility === 'hidden' || style.pointerEvents === 'none' || parseFloat(style.opacity || '1') <= 0.05) {
                    return false;
                }
                const parent = btn.parentElement;
                if (parent) {
                    const pStyle = window.getComputedStyle(parent);
                    if (pStyle.display === 'none' || pStyle.visibility === 'hidden' || parseFloat(pStyle.opacity || '1') <= 0.05) {
                        return false;
                    }
                }
                const rect = btn.getBoundingClientRect();
                return rect.width > 2 && rect.height > 2;
            }

            let nextButton = { found: false, x: 0, y: 0 };
            walkDeep(carousel, (node) => {
                if (nextButton.found) return;
                if (node.tagName === 'BUTTON' || node.getAttribute?.('role') === 'button') {
                    const aria = (node.getAttribute('aria-label') || '').toLowerCase();
                    const slot = (node.getAttribute('slot') || '').toLowerCase();
                    const testid = (node.getAttribute('data-testid') || '').toLowerCase();
                    if (aria.includes('next') || slot.includes('next') || testid.includes('next')) {
                        if (isButtonVisibleAndEnabled(node)) {
                            const rect = node.getBoundingClientRect();
                            nextButton = {
                                found: true,
                                x: rect.left + rect.width / 2,
                                y: rect.top + rect.height / 2
                            };
                        }
                    }
                }
            });

            return JSON.stringify({ hasCarousel: true, totalSlides, imageUrls, nextButton });
        }
        """;

	private const string ClickGalleryNextScript = """
        () => {
            const carousel = document.querySelector('gallery-carousel');
            if (!carousel) return false;

            function walkDeep(root, visitor, visited = new Set()) {
                if (!root || visited.has(root)) return false;
                visited.add(root);
                if (visitor(root) === true) return true;
                if (root.shadowRoot && walkDeep(root.shadowRoot, visitor, visited)) return true;
                const children = root.children || root.childNodes;
                if (children) {
                    for (const child of children) {
                        if (child.nodeType === 1 && walkDeep(child, visitor, visited)) {
                            return true;
                        }
                    }
                }
                return false;
            }

            function isButtonVisibleAndEnabled(btn) {
                if (!btn || btn.disabled || btn.getAttribute('aria-disabled') === 'true') return false;
                if (btn.closest('[hidden], [aria-hidden="true"]')) return false;
                const style = window.getComputedStyle(btn);
                if (style.display === 'none' || style.visibility === 'hidden' || style.pointerEvents === 'none' || parseFloat(style.opacity || '1') <= 0.05) {
                    return false;
                }
                const parent = btn.parentElement;
                if (parent) {
                    const pStyle = window.getComputedStyle(parent);
                    if (pStyle.display === 'none' || pStyle.visibility === 'hidden' || parseFloat(pStyle.opacity || '1') <= 0.05) {
                        return false;
                    }
                }
                const rect = btn.getBoundingClientRect();
                return rect.width > 2 && rect.height > 2;
            }

            return walkDeep(carousel, (node) => {
                if (node.tagName === 'BUTTON' || node.getAttribute?.('role') === 'button') {
                    const aria = (node.getAttribute('aria-label') || '').toLowerCase();
                    const slot = (node.getAttribute('slot') || '').toLowerCase();
                    const testid = (node.getAttribute('data-testid') || '').toLowerCase();
                    if ((aria.includes('next') || slot.includes('next') || testid.includes('next')) &&
                        isButtonVisibleAndEnabled(node)) {
                        node.click();
                        return true;
                    }
                }
                return false;
            });
        }
        """;

	private const string PreparePostLinkForClickScript = """
        ({ postId, permalink }) => {
            const cleanId = (postId || '').replace(/^t3_/i, '').toLowerCase();
            const candidates = [];

            function visit(root) {
                if (!root) return;
                const els = root.querySelectorAll ? root.querySelectorAll('a[href], shreddit-post') : [];
                for (const el of els) {
                    if (el.tagName === 'A') {
                        const href = el.getAttribute('href') || '';
                        const matchesId = cleanId && href.toLowerCase().includes('/comments/' + cleanId);
                        const matchesPermalink = permalink && href.includes(permalink);
                        if (matchesId || matchesPermalink) {
                            if (el.getAttribute('target') === '_blank') {
                                el.removeAttribute('target');
                            }
                            candidates.push(el);
                        }
                    }
                    if (el.shadowRoot) {
                        visit(el.shadowRoot);
                    }
                }
            }

            visit(document);

            let best = null;
            let bestScore = -1;
            for (const a of candidates) {
                const rect = a.getBoundingClientRect();
                if (rect.width < 2 || rect.height < 2) continue;

                const slot = (a.getAttribute('slot') || '').toLowerCase();
                const idAttr = (a.id || '').toLowerCase();
                const testId = (a.getAttribute('data-testid') || '').toLowerCase();
                const href = (a.getAttribute('href') || '').toLowerCase();

                let score = 1;
                if (idAttr.startsWith('post-title-') || testId === 'post-title-text' || testId === 'post-title') score = 5;
                else if (slot === 'full-post-link') score = 4;
                else if (!href.includes('#comments') && !a.closest('[data-testid="comment-count"], [slot="commentCount"]')) score = 3;

                if (score > bestScore) {
                    bestScore = score;
                    best = a;
                }
            }

            if (!best && candidates.length > 0) {
                best = candidates[0];
            }

            if (!best) {
                return JSON.stringify({ found: false, x: 0, y: 0 });
            }

            best.scrollIntoView({ block: 'center', inline: 'nearest', behavior: 'instant' });
            const rect = best.getBoundingClientRect();
            const visible = rect.width >= 2 && rect.height >= 2 &&
                rect.bottom > 0 && rect.top < window.innerHeight &&
                rect.right > 0 && rect.left < window.innerWidth;

            return JSON.stringify({
                found: visible,
                x: rect.left + Math.min(rect.width * 0.5, 160),
                y: rect.top + (rect.height * 0.5)
            });
        }
        """;

	private const string ExtractPostsFromDomScript = """
        () => {
            const posts = [];
            const seenIds = new Set();
            const onListingPage = !/\/comments\//i.test(window.location.pathname || '');

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

            function collectImgsDeep(root, includeAnchorImages = false) {
                const urls = [];
                const seen = new Set();
                walkDeep(root, (node) => {
                    if (node.tagName === 'IMG') {
                        if (node.getAttribute('aria-hidden') === 'true') return;
                        const cls = (node.className && typeof node.className === 'string') ? node.className.toLowerCase() : '';
                        if (cls.includes('blur') || cls.includes('ambient')) return;

                        const candidates = [
                            pickBestFromSrcset(node.getAttribute('srcset')),
                            pickBestFromSrcset(node.getAttribute('data-lazy-srcset')),
                            pickBestFromSrcset(node.dataset ? node.dataset.lazySrcset : null),
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
                    } else if (includeAnchorImages && node.tagName === 'A') {
                        const href = node.getAttribute('href') || '';
                        if (/\.(jpg|jpeg|png|gif|webp)(\?|$)/i.test(href) && !isIgnoredAsset(href) && !seen.has(href)) {
                            seen.add(href);
                            urls.push(href);
                        }
                    }
                });
                return urls;
            }

            function parseTimestampMs(rawValue) {
                if (!rawValue) return null;
                const s = String(rawValue).trim();
                if (!s) return null;
                if (/^\d{10,13}$/.test(s)) {
                    const num = parseInt(s, 10);
                    return s.length === 10 ? num * 1000 : num;
                }
                const normalized = s.replace(/([+-]\d{2})(\d{2})$/, '$1:$2');
                const parsed = Date.parse(normalized);
                return Number.isNaN(parsed) ? null : parsed;
            }

            function parseRelativeAgeHours(text) {
                if (!text || text.length > 40) return null;
                const clean = text.trim().toLowerCase();
                if (!clean) return null;
                if (clean === 'just now' || clean === 'now') return 0.01;
                const m = clean.match(/^(\d+)\s*(s|sec|secs|second|seconds|m|min|mins|minute|minutes|h|hr|hrs|hour|hours|d|day|days|w|wk|wks|week|weeks|mo|mos|month|months|y|yr|yrs|year|years)(?:\s*ago)?$/i);
                if (!m) return null;
                const n = parseInt(m[1], 10);
                const unit = m[2].toLowerCase();
                if (unit.startsWith('s')) return Math.max(0.01, n / 3600);
                if (unit.startsWith('m') && !unit.startsWith('mo')) return n / 60;
                if (unit.startsWith('h')) return n;
                if (unit.startsWith('d')) return n * 24 + 0.1;
                if (unit.startsWith('w')) return n * 7 * 24;
                if (unit.startsWith('mo')) return n * 30 * 24;
                if (unit.startsWith('y')) return n * 365 * 24;
                return null;
            }

            function extractPostAge(root) {
                let bestMs = null;
                let bestAgeHours = null;

                walkDeep(root, (node) => {
                    if (bestMs !== null) return;
                    if (!node.getAttribute) return;

                    const attrs = ['created-timestamp', 'ts', 'datetime', 'data-timestamp'];
                    for (const attr of attrs) {
                        const raw = node.getAttribute(attr);
                        if (raw) {
                            const ms = parseTimestampMs(raw);
                            if (ms !== null) {
                                bestMs = ms;
                                return;
                            }
                        }
                    }

                    const tag = node.tagName;
                    if (bestAgeHours === null && (tag === 'FACEPLATE-TIMEAGO' || tag === 'TIME' || tag === 'SPAN' || tag === 'A')) {
                        if (node.closest && node.closest('[slot="title"], a.title, a.search-title')) return;
                        if (node.children && node.children.length > 0 && tag !== 'FACEPLATE-TIMEAGO' && tag !== 'TIME') return;
                        const age = parseRelativeAgeHours(node.textContent || '');
                        if (age !== null) {
                            bestAgeHours = age;
                        }
                    }
                });

                if (bestMs !== null) {
                    return {
                        createdUtc: new Date(bestMs).toISOString(),
                        ageHours: Math.max(0, (Date.now() - bestMs) / 3600000)
                    };
                }

                return {
                    createdUtc: '',
                    ageHours: bestAgeHours
                };
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
                    const { createdUtc, ageHours } = extractPostAge(el);

                    let mediaUrls = [];
                    let thumbnailUrl = '';
                    let needsDetailPage = false;

                    if (isGallery) {
                        mediaUrls = carousel ? collectImgsDeep(carousel, true) : [];
                        const declaredTotal = carousel ? carousel.querySelectorAll('li').length : 0;
                        if (onListingPage || mediaUrls.length < 2 || (declaredTotal > 1 && mediaUrls.length < declaredTotal)) {
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
                        thumbnailUrl,
                        createdUtc,
                        ageHours
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
                    const galleryContainer = el.querySelector('.media-gallery, .gallery-preview');
                    const isGallery = /\/gallery\//i.test(contentHref) || Boolean(galleryContainer);
                    const { createdUtc, ageHours } = extractPostAge(el);

                    let mediaUrls = [];
                    const imgs = galleryContainer
                        ? collectImgsDeep(galleryContainer, true)
                        : collectImgsDeep(el).filter(u => !u.includes('thumbs.redditmedia.com'));
                    const thumbnailUrl = imgs.length > 0 ? imgs[0] : '';

                    const isVideo = /v\.redd\.it/i.test(contentHref) || Boolean(el.querySelector('[data-hls-url], [data-seek-url]'));
                    const hasDirectContentMedia = /\.(jpg|jpeg|png|gif|webp|mp4)(\?|$)/i.test(contentHref);
                    if (isGallery && imgs.length > 0) {
                        mediaUrls = imgs;
                    } else if (hasDirectContentMedia) {
                        mediaUrls = [contentHref];
                    }

                    const needsDetailPage = (isGallery && (onListingPage || mediaUrls.length < 2)) || isVideo || (!isSelf && mediaUrls.length === 0 && !hasDirectContentMedia);

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
                        thumbnailUrl,
                        createdUtc,
                        ageHours
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
                const { createdUtc, ageHours } = extractPostAge(container);

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
                    thumbnailUrl,
                    createdUtc,
                    ageHours
                });
            }

            return JSON.stringify(posts);
        }
        """;
}