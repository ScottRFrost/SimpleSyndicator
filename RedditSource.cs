using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

namespace SimpleSyndicator;

public class RedditSource : BaseSource, IAsyncDisposable
{
    private static readonly TimeSpan CursorReanchorInterval = TimeSpan.FromHours(24);

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
        ISet<string> alreadySyndicatedDescriptions = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} -=[ Start RedditSource (Playwright) ]=-");

        var effectiveLimit = source.Limit ?? _redditConfig?.DefaultLimit ?? 25;
        var supportsCursor = RedditBrowserClient.SupportsIncrementalCursor(source.URL);
        var cursorIsFresh = !source.LastSeenUtc.HasValue || (DateTime.UtcNow - ToUtc(source.LastSeenUtc.Value)) <= CursorReanchorInterval;
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

        var (rawPosts, newestFullname) = await _browserClient
            .BrowseListingAsync(source.URL, effectiveLimit, stopBeforeFullname)
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

        var expandDetails = _redditConfig?.ExpandPostDetails ?? true;
        var detailDelayBaseSeconds = _redditConfig?.MediaDelaySeconds ?? 4;
        var resolvedPosts = new List<RedditBrowserPost>(rawPosts.Count);

        foreach (var post in rawPosts)
        {
            if (post == null || string.IsNullOrWhiteSpace(post.Id))
            {
                continue;
            }

            if (expandDetails &&
                post.NeedsDetailPage &&
                !string.IsNullOrWhiteSpace(post.Permalink) &&
                !IsPostAlreadySyndicated(post, alreadySyndicatedUrls, alreadySyndicatedDescriptions))
            {
                var delay = RedditBrowserClient.ComputeJitteredDelay(detailDelayBaseSeconds);
                if (delay > TimeSpan.Zero)
                {
                    await _delayAsync(delay).ConfigureAwait(false);
                }

                try
                {
                    var detailPost = await _browserClient.BrowsePostDetailAsync(post.Permalink).ConfigureAwait(false);
                    if (detailPost != null)
                    {
                        resolvedPosts.Add(MergePostDetails(post, detailPost));
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

    public Task DownloadMediaAsync(string mediaUrl, Stream destination)
    {
        return _browserClient.DownloadMediaAsync(mediaUrl, destination);
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
                    results.Add(new SyndicatorContent
                    {
                        Description = $"{title} {index}/{total}",
                        LinkURL = $"https://redd.it/{post.Id}#{index}",
                        ImageURL = normalizedGalleryMedia[i],
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

        if (url.Contains("preview.redd.it", StringComparison.OrdinalIgnoreCase) &&
            !url.Contains("external-preview.redd.it", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var cleanUrl = url.Split('?')[0];
                var uri = new Uri(cleanUrl);
                return $"https://i.redd.it{uri.AbsolutePath}";
            }
            catch
            {
                // Fallback to original URL
            }
        }

        if (url.Contains("i.redd.it", StringComparison.OrdinalIgnoreCase))
        {
            return url.Split('?')[0];
        }

        return url;
    }

    public async ValueTask DisposeAsync()
    {
        if (_ownsBrowserClient)
        {
            await _browserClient.DisposeAsync().ConfigureAwait(false);
        }

        GC.SuppressFinalize(this);
    }

    private static RedditBrowserPost MergePostDetails(RedditBrowserPost summary, RedditBrowserPost detail)
    {
        return summary with
        {
            Title = !string.IsNullOrWhiteSpace(detail.Title) ? detail.Title : summary.Title,
            ContentHref = !string.IsNullOrWhiteSpace(detail.ContentHref) ? detail.ContentHref : summary.ContentHref,
            IsGallery = summary.IsGallery || detail.IsGallery,
            IsSelf = detail.IsSelf,
            MediaUrls = detail.MediaUrls is { Length: > 0 } ? detail.MediaUrls : summary.MediaUrls,
            ThumbnailUrl = !string.IsNullOrWhiteSpace(detail.ThumbnailUrl) ? detail.ThumbnailUrl : summary.ThumbnailUrl,
            NeedsDetailPage = false
        };
    }

    private static List<string> NormalizeAndDeduplicateMedia(IEnumerable<string> rawUrls)
    {
        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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

    private static DateTime ToUtc(DateTime value)
    {
        return value.Kind == DateTimeKind.Local
            ? value.ToUniversalTime()
            : DateTime.SpecifyKind(value, DateTimeKind.Utc);
    }
}
