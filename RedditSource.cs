using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;

namespace SimpleSyndicator;

public class RedditSource : BaseSource, IDisposable
{
    private static readonly string[] GalleryFallbackExtensions = [".jpg", ".jpeg", ".png", ".gif", ".webp"];
    private static readonly TimeSpan CursorReanchorInterval = TimeSpan.FromHours(24);

    private readonly RedditConfig _redditConfig;
    private readonly RedditAuthClient _authClient;
    private readonly HttpClient _mediaProbeClient;
    private readonly bool _ownsAuthClient;

    public RedditSource(RedditConfig redditConfig)
        : this(new RedditAuthClient(redditConfig), mediaProbeHandler: null, redditConfig: redditConfig, ownsAuthClient: true)
    {
    }

    internal RedditSource(RedditAuthClient authClient, HttpMessageHandler mediaProbeHandler = null, RedditConfig redditConfig = null)
        : this(authClient, mediaProbeHandler, redditConfig, ownsAuthClient: false)
    {
    }

    private RedditSource(RedditAuthClient authClient, HttpMessageHandler mediaProbeHandler, RedditConfig redditConfig, bool ownsAuthClient)
    {
        _authClient = authClient ?? throw new ArgumentNullException(nameof(authClient));
        _redditConfig = redditConfig;
        _ownsAuthClient = ownsAuthClient;

        var handler = mediaProbeHandler ?? new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All
        };

        _mediaProbeClient = new HttpClient(handler, disposeHandler: mediaProbeHandler == null)
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        _mediaProbeClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", SimpleHttpClient.DefaultUserAgent);
        _mediaProbeClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
    }

    public RedditAuthMode AuthMode => _authClient.AuthMode;

    public override async Task<SyndicatorContent[]> Parse(string Url, string Tags = "")
    {
        Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} -=[ Start RedditSource ({_authClient.AuthMode}) ]=-");

        var response = await _authClient.GetJsonAsync(Url, _redditConfig?.DefaultLimit, beforeFullname: null).ConfigureAwait(false);
        var syndicatorContents = await ParseListingJsonAsync(response, Tags).ConfigureAwait(false);

        Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} -=[ End RedditSource ]=-");
        Console.WriteLine();
        return syndicatorContents;
    }

    public async Task<SyndicatorContent[]> Parse(ConfigSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} -=[ Start RedditSource ({_authClient.AuthMode}) ]=-");

        var effectiveLimit = source.Limit ?? _redditConfig?.DefaultLimit;
        var supportsBefore = RedditAuthClient.SupportsBeforeCursor(source.URL);
        var cursorIsFresh = !source.LastSeenUtc.HasValue || (DateTime.UtcNow - ToUtc(source.LastSeenUtc.Value)) <= CursorReanchorInterval;
        var beforeFullname = supportsBefore && !string.IsNullOrWhiteSpace(source.LastSeenFullname) && cursorIsFresh
            ? source.LastSeenFullname
            : null;

        if (beforeFullname != null)
        {
            Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Fetching posts newer than {beforeFullname} (limit={effectiveLimit?.ToString() ?? "default"})...");
        }
        else if (supportsBefore && !string.IsNullOrWhiteSpace(source.LastSeenFullname) && !cursorIsFresh)
        {
            Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Re-anchoring 24h+ cursor for {source.URL} (limit={effectiveLimit?.ToString() ?? "default"})...");
        }

        var response = await _authClient.GetJsonAsync(source.URL, effectiveLimit, beforeFullname).ConfigureAwait(false);
        var (items, newestFullname) = await ParseListingJsonWithCursorAsync(response, source.Tags ?? string.Empty).ConfigureAwait(false);

        if (supportsBefore)
        {
            if (!string.IsNullOrWhiteSpace(newestFullname))
            {
                source.LastSeenFullname = newestFullname;
                source.LastSeenUtc = DateTime.UtcNow;
            }
            else if (beforeFullname == null || !source.LastSeenUtc.HasValue)
            {
                source.LastSeenUtc = DateTime.UtcNow;
            }
        }

        Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} -=[ End RedditSource ]=-");
        Console.WriteLine();
        return items;
    }

    internal async Task<SyndicatorContent[]> ParseListingJsonAsync(string responseJson, string tags = "")
    {
        var (items, _) = await ParseListingJsonWithCursorAsync(responseJson, tags).ConfigureAwait(false);
        return items;
    }

    internal async Task<(SyndicatorContent[] Items, string NewestFullname)> ParseListingJsonWithCursorAsync(string responseJson, string tags = "")
    {
        if (string.IsNullOrWhiteSpace(responseJson))
        {
            return ([], null);
        }

        var listing = JsonSerializer.Deserialize<RedditListing>(responseJson);
        if (listing?.data?.children == null || listing.data.children.Length == 0)
        {
            return ([], null);
        }

        var newestPost = listing.data.children
            .Select(c => c?.data)
            .FirstOrDefault(p => p != null && !p.stickied && (!string.IsNullOrWhiteSpace(p.name) || !string.IsNullOrWhiteSpace(p.id)))
            ?? listing.data.children
                .Select(c => c?.data)
                .FirstOrDefault(p => p != null && (!string.IsNullOrWhiteSpace(p.name) || !string.IsNullOrWhiteSpace(p.id)));

        var newestFullname = newestPost == null
            ? null
            : !string.IsNullOrWhiteSpace(newestPost.name)
                ? newestPost.name
                : $"t3_{newestPost.id}";

        var syndicatorContents = new List<SyndicatorContent>();

        foreach (var child in listing.data.children)
        {
            var item = child?.data;
            if (item == null || string.IsNullOrWhiteSpace(item.id))
            {
                continue;
            }

            var title = WebUtility.HtmlDecode(item.title ?? string.Empty).Trim();

            var gallerySource = item.is_gallery && item.gallery_data?.items?.Length > 0
                ? item
                : item.crosspost_parent_list?.FirstOrDefault(p => p?.is_gallery == true && p.gallery_data?.items?.Length > 0);

            if (gallerySource != null)
            {
                var galleryItems = gallerySource.gallery_data.items;
                var total = galleryItems.Length;
                var index = 0;

                foreach (var galleryItem in galleryItems)
                {
                    index++;
                    if (string.IsNullOrWhiteSpace(galleryItem?.media_id))
                    {
                        continue;
                    }

                    var imageUrl = ResolveMediaMetadataUrl(gallerySource.media_metadata, galleryItem.media_id);
                    if (string.IsNullOrWhiteSpace(imageUrl))
                    {
                        imageUrl = await ProbeGalleryImageUrlAsync(galleryItem.media_id).ConfigureAwait(false);
                    }

                    if (!string.IsNullOrWhiteSpace(imageUrl))
                    {
                        var content = new SyndicatorContent
                        {
                            Description = $"{title} {index}/{total}",
                            LinkURL = $"https://redd.it/{item.id}#{index}",
                            ImageURL = imageUrl,
                            Tags = tags
                        };
                        Console.WriteLine($"{content.Description} | {content.LinkURL} | {content.ImageURL}");
                        syndicatorContents.Add(content);
                    }
                }

                continue;
            }

            var candidateUrl = ResolveSinglePostMediaUrl(item);
            if (!string.IsNullOrWhiteSpace(candidateUrl))
            {
                var content = new SyndicatorContent
                {
                    Description = title,
                    LinkURL = $"https://redd.it/{item.id}",
                    ImageURL = candidateUrl,
                    Tags = tags
                };
                Console.WriteLine($"{content.Description} | {content.LinkURL} | {content.ImageURL}");
                syndicatorContents.Add(content);
            }
        }

        return ([.. syndicatorContents], newestFullname);
    }

    private static DateTime ToUtc(DateTime value)
    {
        return value.Kind == DateTimeKind.Local
            ? value.ToUniversalTime()
            : DateTime.SpecifyKind(value, DateTimeKind.Utc);
    }

    public void Dispose()
    {
        if (_ownsAuthClient)
        {
            _authClient.Dispose();
        }

        _mediaProbeClient.Dispose();
        GC.SuppressFinalize(this);
    }

    private static string ResolveMediaMetadataUrl(Dictionary<string, RedditMediaMetadataItem> mediaMetadata, string mediaId)
    {
        if (mediaMetadata == null ||
            !mediaMetadata.TryGetValue(mediaId, out var meta) ||
            meta == null)
        {
            return string.Empty;
        }

        if (!string.IsNullOrEmpty(meta.status) &&
            !meta.status.Equals("valid", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        var rawCandidate = !string.IsNullOrWhiteSpace(meta.s?.mp4)
            ? meta.s.mp4
            : !string.IsNullOrWhiteSpace(meta.s?.gif)
                ? meta.s.gif
                : meta.s?.u;

        if (string.IsNullOrWhiteSpace(rawCandidate))
        {
            return string.Empty;
        }

        var decoded = WebUtility.HtmlDecode(rawCandidate);
        var highRes = GetHighResImageUrl(decoded);
        return IsMediaUrl(highRes) ? highRes : string.Empty;
    }

    private async Task<string> ProbeGalleryImageUrlAsync(string mediaId)
    {
        foreach (var extension in GalleryFallbackExtensions)
        {
            var probeUrl = $"https://i.redd.it/{mediaId}{extension}";
            try
            {
                using var extRequest = new HttpRequestMessage(HttpMethod.Head, probeUrl);
                using var extResponse = await _mediaProbeClient.SendAsync(extRequest).ConfigureAwait(false);
                if (extResponse.IsSuccessStatusCode)
                {
                    return probeUrl;
                }
            }
            catch
            {
                // Try next extension
            }
        }

        return string.Empty;
    }

    private static string ResolveSinglePostMediaUrl(RedditPost item)
    {
        // 1. Direct media URL
        var itemUrl = WebUtility.HtmlDecode(item.url ?? string.Empty);
        if (IsMediaUrl(itemUrl))
        {
            return GetHighResImageUrl(itemUrl);
        }

        // 2. Animated preview variants (mp4 / gif)
        var firstPreviewImage = item.preview?.images?.FirstOrDefault();
        var mp4Variant = WebUtility.HtmlDecode(firstPreviewImage?.variants?.mp4?.source?.url ?? string.Empty);
        if (IsMediaUrl(mp4Variant))
        {
            return mp4Variant;
        }

        var gifVariant = WebUtility.HtmlDecode(firstPreviewImage?.variants?.gif?.source?.url ?? string.Empty);
        if (IsMediaUrl(gifVariant))
        {
            return gifVariant;
        }

        // 3. Reddit-hosted video fallback URL
        var videoUrl = WebUtility.HtmlDecode(
            item.secure_media?.reddit_video?.fallback_url ??
            item.media?.reddit_video?.fallback_url ??
            string.Empty);
        if (IsMediaUrl(videoUrl))
        {
            return videoUrl;
        }

        // 4. Non-self post preview source
        if (!item.is_self)
        {
            var previewSourceUrl = WebUtility.HtmlDecode(firstPreviewImage?.source?.url ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(previewSourceUrl))
            {
                var highResPreview = GetHighResImageUrl(previewSourceUrl);
                if (IsMediaUrl(highResPreview))
                {
                    return highResPreview;
                }
            }
        }

        return string.Empty;
    }

    private static bool IsMediaUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        var cleanUrl = url.Split('?')[0]; // Strip query parameters
        return cleanUrl.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
               cleanUrl.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
               cleanUrl.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
               cleanUrl.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) ||
               cleanUrl.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) ||
               cleanUrl.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetHighResImageUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;

        // If it's preview.redd.it (but not external-preview.redd.it), convert it to i.redd.it
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
                // fallback
            }
        }

        // If it's already an i.redd.it link, just strip query parameters
        if (url.Contains("i.redd.it", StringComparison.OrdinalIgnoreCase))
        {
            return url.Split('?')[0];
        }

        // For external-preview, v.redd.it, or other domains, preserve query parameters (like signature keys)
        return url;
    }
}

// Reddit JSON format objects
#pragma warning disable IDE1006 // Names must begin in upper case
public record RedditListing
{
    public string kind { get; set; }
    public RedditListingData data { get; set; }
}

public record RedditListingData
{
    public string after { get; set; }
    public string before { get; set; }
    public RedditChild[] children { get; set; }
}

public record RedditChild
{
    public string kind { get; set; }
    public RedditPost data { get; set; }
}

public record RedditPost
{
    public string id { get; set; }
    public string name { get; set; }
    public string title { get; set; }
    public string url { get; set; }
    public string url_overridden_by_dest { get; set; }
    public string permalink { get; set; }
    public string post_hint { get; set; }
    public string domain { get; set; }
    public bool is_self { get; set; }
    public bool is_gallery { get; set; }
    public bool is_video { get; set; }
    public bool stickied { get; set; }
    public RedditGalleryData gallery_data { get; set; }
    public Dictionary<string, RedditMediaMetadataItem> media_metadata { get; set; }
    public RedditPreview preview { get; set; }
    public RedditMedia secure_media { get; set; }
    public RedditMedia media { get; set; }
    public RedditPost[] crosspost_parent_list { get; set; }
}

public record RedditGalleryData
{
    public RedditGalleryItem[] items { get; set; }
}

public record RedditGalleryItem
{
    public string caption { get; set; }
    public string media_id { get; set; }
    public long? id { get; set; }
}

public record RedditMediaMetadataItem
{
    public string status { get; set; }
    public string e { get; set; }
    public string m { get; set; }
    public RedditMediaSource s { get; set; }
}

public record RedditMediaSource
{
    public string u { get; set; }
    public string gif { get; set; }
    public string mp4 { get; set; }
}

public record RedditPreview
{
    public RedditPreviewImage[] images { get; set; }
    public bool enabled { get; set; }
}

public record RedditPreviewImage
{
    public RedditImageSource source { get; set; }
    public RedditPreviewVariants variants { get; set; }
}

public record RedditPreviewVariants
{
    public RedditPreviewVariant gif { get; set; }
    public RedditPreviewVariant mp4 { get; set; }
}

public record RedditPreviewVariant
{
    public RedditImageSource source { get; set; }
}

public record RedditImageSource
{
    public string url { get; set; }
    public int width { get; set; }
    public int height { get; set; }
}

public record RedditMedia
{
    public RedditVideo reddit_video { get; set; }
}

public record RedditVideo
{
    public string fallback_url { get; set; }
    public bool is_gif { get; set; }
}
#pragma warning restore IDE1006 // Names must begin in upper case
