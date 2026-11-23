using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SimpleSyndicator;

public class RedditSource : BaseSource, IAsyncDisposable
{
    private static readonly TimeSpan CursorReanchorInterval = TimeSpan.FromHours(24);
    private static readonly Regex SluggedMediaPathRegex = new(
        @"^/+(?:.+-v\d+-)([a-z0-9_]+\.(?:jpg|jpeg|png|gif|webp|mp4))$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly ConcurrentDictionary<string, string> SignedPreviewFallbacks = new(StringComparer.OrdinalIgnoreCase);

    private readonly RedditConfig _redditConfig;
    private readonly RedditBrowserClient _browserClient;
    private readonly bool _ownsBrowserClient;
    private readonly Func<TimeSpan, Task> _delayAsync;

    public RedditSource(RedditConfig redditConfig)
        : this(new RedditBrowserClient(redditConfig), redditConfig, ownsBrowserClient: true, delayAsync: null)
    {
    }

    internal RedditSource(RedditBrowserClient browserClient, RedditConfig redditConfig = null)
        : this(browserClient, redditConfig, ownsBrowserClient: false, delayAsync: null)
    {
    }

    private RedditSource(
        RedditBrowserClient browserClient,
        RedditConfig redditConfig,
        bool ownsBrowserClient,
        Func<TimeSpan, Task> delayAsync)
    {
        _browserClient = browserClient ?? throw new ArgumentNullException(nameof(browserClient));
        _redditConfig = redditConfig;
        _ownsBrowserClient = ownsBrowserClient;
        _delayAsync = delayAsync ?? (static d => Task.Delay(d));
    }

    public override Task<SyndicatorContent[]> Parse(string Url, string Tags = "")
    {
        return Parse(new ConfigSource
        {
            Type = "Reddit",
            URL = Url,
            Tags = Tags
        });
    }

    public async Task<SyndicatorContent[]> Parse(
        ConfigSource source,
        ISet<string> alreadySyndicatedUrls = null,
        ISet<string> alreadySyndicatedDescriptions = null,
        Func<SyndicatorContent, Task> onSyndicateItemAsync = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} -=[ Start RedditSource (Playwright) ]=-");

        var effectiveLimit = source.Limit ?? _redditConfig?.DefaultLimit ?? 25;
        var supportsCursor = RedditBrowserClient.SupportsIncrementalCursor(source.URL);
        var cursorIsFresh = !source.LastSeenUtc.HasValue || (DateTime.UtcNow - source.LastSeenUtc.Value.EnsureUtc()) <= CursorReanchorInterval;
        var stopBeforeFullname = supportsCursor && !string.IsNullOrWhiteSpace(source.LastSeenFullname) && cursorIsFresh
            ? source.LastSeenFullname
            : null;

        if (stopBeforeFullname != null)
        {
            Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Browsing posts newer than {stopBeforeFullname} (limit={effectiveLimit})...");
        }
        else if (supportsCursor && !string.IsNullOrWhiteSpace(source.LastSeenFullname) && !cursorIsFresh)
        {
            Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Re-anchoring 24h+ cursor for {source.URL} (limit={effectiveLimit})...");
        }

        List<SyndicatorContent> streamedItems = null;
        Func<RedditBrowserPost, Task> onPostVisited = null;
        if (onSyndicateItemAsync != null)
        {
            streamedItems = [];
            onPostVisited = async visitedPost =>
            {
                var postItems = ConvertPostsToContent([visitedPost], source.Tags ?? string.Empty);
                foreach (var item in postItems)
                {
                    Console.WriteLine($"{item.Description} | {item.LinkURL} | {item.ImageURL}");
                    streamedItems.Add(item);
                    await onSyndicateItemAsync(item).ConfigureAwait(false);
                }
            };
        }

        var (rawPosts, newestFullname) = await _browserClient
            .BrowseListingAsync(
                source.URL,
                effectiveLimit,
                stopBeforeFullname,
                shouldSkipPost: post => IsPostAlreadySyndicated(post, alreadySyndicatedUrls, alreadySyndicatedDescriptions),
                onPostVisitedAsync: onPostVisited)
            .ConfigureAwait(false);

        if (supportsCursor)
        {
            if (!string.IsNullOrWhiteSpace(newestFullname))
            {
                source.LastSeenFullname = newestFullname;
                source.LastSeenUtc = DateTime.UtcNow;
            }
            else if (stopBeforeFullname == null || !source.LastSeenUtc.HasValue)
            {
                source.LastSeenUtc = DateTime.UtcNow;
            }
        }

        if (streamedItems != null)
        {
            Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} -=[ End RedditSource ]=-");
            Console.WriteLine();
            return [.. streamedItems];
        }

        var expandDetails = _redditConfig?.ExpandPostDetails ?? true;
        var detailDelayBaseSeconds = _redditConfig?.MediaDelaySeconds ?? 4;
        var maxAgeHours = _redditConfig?.MaxPostAgeHours ?? 24;
        List<RedditBrowserPost> resolvedPosts = [with(capacity: rawPosts.Count)];

        foreach (var post in rawPosts)
        {
            if (post == null || string.IsNullOrWhiteSpace(post.Id))
            {
                continue;
            }

            if (RedditBrowserClient.IsPostOlderThan(post, maxAgeHours))
            {
                Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Ignoring post '{post.Title}' ({post.Fullname}) because it is {RedditBrowserClient.FormatPostAge(post)} (> {maxAgeHours}h limit).");
                if (supportsCursor && !post.IsStickied)
                {
                    Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Reached post older than {maxAgeHours} hours on new-sorted source; stopping remaining post processing.");
                    break;
                }

                continue;
            }

            if (expandDetails &&
                post.NeedsDetailPage &&
                !string.IsNullOrWhiteSpace(post.Permalink))
            {
                if (IsPostAlreadySyndicated(post, alreadySyndicatedUrls, alreadySyndicatedDescriptions))
                {
                    Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Skipping already-syndicated post '{post.Title}' ({post.Fullname}).");
                    continue;
                }

                var delay = RedditBrowserClient.ComputeJitteredDelay(detailDelayBaseSeconds);
                if (delay > TimeSpan.Zero)
                {
                    await _delayAsync(delay).ConfigureAwait(false);
                }

                Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Opening post '{post.Title}' ({post.Fullname}) to inspect media/gallery -> {post.Permalink}");

                try
                {
                    var detailPost = await _browserClient.BrowsePostDetailAsync(post.Permalink).ConfigureAwait(false);
                    if (detailPost != null)
                    {
                        if (RedditBrowserClient.IsPostOlderThan(detailPost, maxAgeHours))
                        {
                            Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Ignoring post '{detailPost.Title}' ({detailPost.Fullname}) because it is {RedditBrowserClient.FormatPostAge(detailPost)} (> {maxAgeHours}h limit).");
                            if (supportsCursor && !detailPost.IsStickied)
                            {
                                Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Reached post older than {maxAgeHours} hours on new-sorted source; stopping remaining post detail checks.");
                                break;
                            }

                            continue;
                        }

                        resolvedPosts.Add(RedditBrowserClient.MergePostDetails(post, detailPost));
                        continue;
                    }
                }
                catch (RedditBrowserException ex) when (!ex.ShouldAbortRun)
                {
                    Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Skipping detail page for '{post.Title}': {ex.Message}");
                }
                catch (Exception ex) when (ex is not RedditBrowserException)
                {
                    Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Failed to inspect post detail '{post.Permalink}': {ex.Message}");
                }
            }

            resolvedPosts.Add(post);
        }

        var items = ConvertPostsToContent(resolvedPosts, source.Tags ?? string.Empty);
        foreach (var content in items)
        {
            Console.WriteLine($"{content.Description} | {content.LinkURL} | {content.ImageURL}");
        }

        Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} -=[ End RedditSource ]=-");
        Console.WriteLine();
        return items;
    }

    public Task DownloadMediaAsync(
        string mediaUrl,
        Stream destination,
        string refererUrl = null,
        string fallbackMediaUrl = null)
    {
        return _browserClient.DownloadMediaAsync(mediaUrl, destination, refererUrl, fallbackMediaUrl);
    }

    public static SyndicatorContent[] ConvertPostsToContent(IEnumerable<RedditBrowserPost> posts, string tags = "")
    {
        if (posts == null)
        {
            return [];
        }

        var results = new List<SyndicatorContent>();

        foreach (var post in posts)
        {
            if (post == null || string.IsNullOrWhiteSpace(post.Id))
            {
                continue;
            }

            var title = WebUtility.HtmlDecode(post.Title ?? string.Empty).Trim();
            var normalizedGalleryMedia = NormalizeAndDeduplicateMedia(post.MediaUrls);
            var referrerUrl = !string.IsNullOrWhiteSpace(post.Permalink)
                ? RedditBrowserClient.BuildPostDetailUrl(post.Permalink)
                : $"https://www.reddit.com/comments/{post.Id}/";

            if (post.IsGallery || normalizedGalleryMedia.Count > 1)
            {
                if (normalizedGalleryMedia.Count == 0)
                {
                    continue;
                }

                var total = normalizedGalleryMedia.Count;
                for (int i = 0; i < total; i++)
                {
                    var index = i + 1;
                    var imageUrl = normalizedGalleryMedia[i];
                    results.Add(new SyndicatorContent
                    {
                        Description = $"{title} {index}/{total}",
                        LinkURL = $"https://redd.it/{post.Id}#{index}",
                        ImageURL = imageUrl,
                        FallbackImageURL = GetSignedPreviewFallbackUrl(imageUrl),
                        ReferrerURL = referrerUrl,
                        Tags = tags
                    });
                }

                continue;
            }

            var candidateUrl = ResolveSinglePostMediaUrl(post, normalizedGalleryMedia);
            if (!string.IsNullOrWhiteSpace(candidateUrl))
            {
                results.Add(new SyndicatorContent
                {
                    Description = title,
                    LinkURL = $"https://redd.it/{post.Id}",
                    ImageURL = candidateUrl,
                    FallbackImageURL = GetSignedPreviewFallbackUrl(candidateUrl),
                    ReferrerURL = referrerUrl,
                    Tags = tags
                });
            }
        }

        return [.. results];
    }

    public static bool IsPostAlreadySyndicated(
        RedditBrowserPost post,
        ISet<string> syndicatedUrls,
        ISet<string> syndicatedDescriptions)
    {
        if (post == null || string.IsNullOrWhiteSpace(post.Id))
        {
            return false;
        }

        if (syndicatedUrls != null)
        {
            if (syndicatedUrls.Contains($"https://redd.it/{post.Id}") ||
                syndicatedUrls.Contains($"https://redd.it/{post.Id}#1"))
            {
                return true;
            }
        }

        var decodedTitle = WebUtility.HtmlDecode(post.Title ?? string.Empty).Trim();
        if (!string.IsNullOrEmpty(decodedTitle) && syndicatedDescriptions != null)
        {
            if (syndicatedDescriptions.Contains(decodedTitle))
            {
                return true;
            }

            var galleryPrefix = $"{decodedTitle} 1/";
            foreach (var description in syndicatedDescriptions)
            {
                if (!string.IsNullOrEmpty(description) &&
                    description.StartsWith(galleryPrefix, StringComparison.Ordinal) &&
                    description.Length > galleryPrefix.Length &&
                    int.TryParse(description[galleryPrefix.Length..], out _))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public static bool IsMediaUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        var cleanUrl = url.Split('?')[0];
        return cleanUrl.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
               cleanUrl.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
               cleanUrl.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
               cleanUrl.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) ||
               cleanUrl.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) ||
               cleanUrl.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase);
    }

    public static string GetHighResImageUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        var isExternalPreview = url.Contains("external-preview.redd.it", StringComparison.OrdinalIgnoreCase);
        var isPreview = !isExternalPreview && url.Contains("preview.redd.it", StringComparison.OrdinalIgnoreCase);
        var isDirectReddIt = url.Contains("i.redd.it", StringComparison.OrdinalIgnoreCase);

        if (isPreview || isDirectReddIt)
        {
            try
            {
                var cleanUrl = url.Split('?')[0];
                var uri = new Uri(cleanUrl);
                var normalizedPath = uri.AbsolutePath;
                var slugMatch = SluggedMediaPathRegex.Match(normalizedPath);
                if (slugMatch.Success)
                {
                    normalizedPath = $"/{slugMatch.Groups[1].Value}";
                }

                var highResUrl = $"https://i.redd.it{normalizedPath}";
                if (isPreview &&
                    url.Contains('?') &&
                    !string.Equals(highResUrl, url, StringComparison.OrdinalIgnoreCase))
                {
                    SignedPreviewFallbacks[highResUrl] = url;
                }

                return highResUrl;
            }
            catch
            {
                if (isDirectReddIt)
                {
                    return url.Split('?')[0];
                }
            }
        }

        return url;
    }

    public static string GetSignedPreviewFallbackUrl(string highResOrRawUrl)
    {
        if (string.IsNullOrWhiteSpace(highResOrRawUrl))
        {
            return string.Empty;
        }

        var trimmed = highResOrRawUrl.Trim();
        if (SignedPreviewFallbacks.TryGetValue(trimmed, out var signedUrl))
        {
            return signedUrl;
        }

        var normalized = GetHighResImageUrl(trimmed);
        return !string.IsNullOrWhiteSpace(normalized) && SignedPreviewFallbacks.TryGetValue(normalized, out signedUrl)
            ? signedUrl
            : string.Empty;
    }

    public async ValueTask DisposeAsync()
    {
        if (_ownsBrowserClient)
        {
            await _browserClient.DisposeAsync().ConfigureAwait(false);
        }

        GC.SuppressFinalize(this);
    }

    private static List<string> NormalizeAndDeduplicateMedia(IEnumerable<string> rawUrls)
    {
        var list = new List<string>();
        HashSet<string> seen = [with(StringComparer.OrdinalIgnoreCase)];

        if (rawUrls == null)
        {
            return list;
        }

        foreach (var raw in rawUrls)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var decoded = WebUtility.HtmlDecode(raw.Trim());
            var highRes = GetHighResImageUrl(decoded);
            if (IsMediaUrl(highRes) && seen.Add(highRes))
            {
                list.Add(highRes);
            }
        }

        return list;
    }

    private static string ResolveSinglePostMediaUrl(RedditBrowserPost post, List<string> normalizedMedia)
    {
        // 1. Direct media URL on contentHref
        var contentHref = WebUtility.HtmlDecode(post.ContentHref ?? string.Empty).Trim();
        if (IsMediaUrl(contentHref))
        {
            return GetHighResImageUrl(contentHref);
        }

        // 2. Video or animated GIF variants in extracted media URLs
        var animatedUrl = normalizedMedia.FirstOrDefault(u =>
        {
            var clean = u.Split('?')[0];
            return clean.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
                   clean.EndsWith(".gif", StringComparison.OrdinalIgnoreCase);
        });
        if (!string.IsNullOrWhiteSpace(animatedUrl))
        {
            return animatedUrl;
        }

        // 3. First extracted image media URL
        if (normalizedMedia.Count > 0)
        {
            return normalizedMedia[0];
        }

        // 4. Non-self post thumbnail fallback
        if (!post.IsSelf && !string.IsNullOrWhiteSpace(post.ThumbnailUrl))
        {
            var decodedThumb = WebUtility.HtmlDecode(post.ThumbnailUrl.Trim());
            var highResThumb = GetHighResImageUrl(decodedThumb);
            if (IsMediaUrl(highResThumb))
            {
                return highResThumb;
            }
        }

        return string.Empty;
    }
}
