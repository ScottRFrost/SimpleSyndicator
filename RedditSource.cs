using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.ServiceModel.Syndication;
using System.Xml;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Linq;

namespace SimpleSyndicator;

public class RedditSource : BaseSource
{
    public override async Task<SyndicatorContent[]> Parse(string Url, string Tags = "")
    {
        Console.WriteLine(DateTime.Now.ToString("M/d HH:mm") + " -=[ Start RedditSource ]=-");
        #pragma warning disable S4830 // Server certificates should be verified during SSL/TLS connections
        var handler = new HttpClientHandler()
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        #pragma warning restore S4830 // Server certificates should be verified during SSL/TLS connections
        var syndicatorContents = new List<SyndicatorContent>();
        using var http = new SimpleHttpClient(handler);
        var response = await http.DownloadStringAsync(Url).ConfigureAwait(false);
        if (Url.Contains(".rss"))
        {
            // Parse RSS
            using var xmlReader = XmlReader.Create(new StringReader(response));
            var rss = SyndicationFeed.Load(xmlReader);

            foreach (var rssItem in rss.Items)
            {
                if (rssItem.Content is not TextSyndicationContent)
                {
                    continue;
                }

                var content = ((TextSyndicationContent)rssItem.Content).Text;
                var syndicatorContent = new SyndicatorContent
                {
                    Description = rssItem.Title.Text.Trim(),
                    LinkURL = rssItem.Links[0].Uri.ToString(),
                    ImageURL = string.Empty,
                    Tags = Tags
                };

                // Find image URL via [link] in Content
                if (content.Contains(">[link]<"))
                {
                    var imageURL = content[..(content.IndexOf(">[link]<") - 1)];
                    imageURL = imageURL[(imageURL.LastIndexOf('\"') + 1)..];
                    if (imageURL.EndsWith(".jpg", StringComparison.InvariantCultureIgnoreCase) || imageURL.EndsWith(".png", StringComparison.InvariantCultureIgnoreCase))
                    {
                        syndicatorContent.ImageURL = imageURL;
                    }
                }

                // Shorten URL
                if (syndicatorContent.LinkURL.Contains("/comments/"))
                {
                    syndicatorContent.LinkURL = syndicatorContent.LinkURL[(syndicatorContent.LinkURL.IndexOf("/comments/") + 10)..];
                    syndicatorContent.LinkURL = $"https://redd.it/{syndicatorContent.LinkURL.AsSpan(0, syndicatorContent.LinkURL.IndexOf('/'))}";
                }

                // If we found an image, return this item
                if (syndicatorContent.ImageURL != string.Empty)
                {
                    Console.WriteLine(syndicatorContent.Description + " | " + syndicatorContent.LinkURL + " | " + syndicatorContent.ImageURL);
                    syndicatorContents.Add(syndicatorContent);
                }
            }
        }
        else
        {
            // Parse JSON
            var jsonItems = JsonSerializer.Deserialize<RedditJson>(response);
            foreach (var item in jsonItems.data.children.Select(i => i.data))
            {
                if (item.is_gallery)
                {
                    var index = 0;
                    foreach (var mediaID in item.gallery_data.items.Select(i => i.media_id))
                    {
                        index++;

                        // The gallery json is a disaster with randomly named keys, work around it by trying different extensions
                        foreach (var extension in new string[] { ".jpeg", ".jpg", ".png", ".gif", ".webp" })
                        {
                            using var extRequest = new HttpRequestMessage(HttpMethod.Head, $"https://i.redd.it/{mediaID}{extension}");
                            extRequest.Headers.Accept.Clear();
                            extRequest.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("*/*"));
                            using var extResponse = await http.SendAsync(extRequest);
                            if (extResponse.IsSuccessStatusCode)
                            {
                                syndicatorContents.Add(new SyndicatorContent
                                {
                                    Description = $"{item.title.Trim()} {index}/{item.gallery_data.items.Length}",
                                    LinkURL = $"https://redd.it/{item.id}#{index}",
                                    ImageURL = $"https://i.redd.it/{mediaID}{extension}",
                                    Tags = Tags
                                });
                                break;
                            }
                        }
                    }
                }
                else if (!string.IsNullOrWhiteSpace(item.url) && (item.url.EndsWith(".jpeg") || item.url.EndsWith(".jpg") || item.url.EndsWith(".png") || item.url.EndsWith(".gif") || item.url.EndsWith(".webp")))
                {
                    syndicatorContents.Add(new SyndicatorContent
                    {
                        Description = item.title.Trim(),
                        LinkURL = $"https://redd.it/{item.id}",
                        ImageURL = item.url,
                        Tags = Tags
                    });
                }
            }
        }

        Console.WriteLine(DateTime.Now.ToString("M/d HH:mm") + " -=[ End RedditSource ]=-");
        Console.WriteLine();
        return [.. syndicatorContents];
    }
}

// Reddit JSON format objects
#pragma warning disable S2094 // Classes should not be empty
#pragma warning disable S101 // Types should be named in PascalCase
#pragma warning disable IDE1006 // Names must begin in upper case
public record RedditJson
{
    public string kind { get; set; }
    public Data data { get; set; }
}

public record Data
{
    public string after { get; set; }
    public int dist { get; set; }
    public string modhash { get; set; }
    public string geo_filter { get; set; }
    public Child[] children { get; set; }
    public object before { get; set; }
}

public record Child
{
    public string kind { get; set; }
    public Data1 data { get; set; }
}

public record Data1
{
    public object approved_at_utc { get; set; }
    public string subreddit { get; set; }
    public string selftext { get; set; }
    public string author_fullname { get; set; }
    public bool saved { get; set; }
    public object mod_reason_title { get; set; }
    public int gilded { get; set; }
    public bool clicked { get; set; }
    public string title { get; set; }
    public object[] link_flair_richtext { get; set; }
    public string subreddit_name_prefixed { get; set; }
    public bool hidden { get; set; }
    public object pwls { get; set; }
    public string link_flair_css_record { get; set; }
    public int downs { get; set; }
    public int? thumbnail_height { get; set; }
    public object top_awarded_type { get; set; }
    public bool hide_score { get; set; }
    public string name { get; set; }
    public bool quarantine { get; set; }
    public string link_flair_text_color { get; set; }
    public float upvote_ratio { get; set; }
    public object author_flair_background_color { get; set; }
    public int ups { get; set; }
    public int total_awards_received { get; set; }
    public Media_Embed media_embed { get; set; }
    public int? thumbnail_width { get; set; }
    public object author_flair_template_id { get; set; }
    public bool is_original_content { get; set; }
    public object[] user_reports { get; set; }
    public object secure_media { get; set; }
    public bool is_reddit_media_domain { get; set; }
    public bool is_meta { get; set; }
    public object category { get; set; }
    public Secure_Media_Embed secure_media_embed { get; set; }
    public string link_flair_text { get; set; }
    public bool can_mod_post { get; set; }
    public int score { get; set; }
    public object approved_by { get; set; }
    public bool is_created_from_ads_ui { get; set; }
    public bool author_premium { get; set; }
    public string thumbnail { get; set; }
    public bool edited { get; set; }
    public object author_flair_css_record { get; set; }
    public object[] author_flair_richtext { get; set; }
    public Gildings gildings { get; set; }
    public string post_hint { get; set; }
    public object content_categories { get; set; }
    public bool is_self { get; set; }
    public string subreddit_type { get; set; }
    public float created { get; set; }
    public string link_flair_type { get; set; }
    public object wls { get; set; }
    public object removed_by_category { get; set; }
    public object banned_by { get; set; }
    public string author_flair_type { get; set; }
    public string domain { get; set; }
    public bool allow_live_comments { get; set; }
    public string selftext_html { get; set; }
    public object likes { get; set; }
    public object suggested_sort { get; set; }
    public object banned_at_utc { get; set; }
    public string url_overridden_by_dest { get; set; }
    public object view_count { get; set; }
    public bool archived { get; set; }
    public bool no_follow { get; set; }
    public bool is_crosspostable { get; set; }
    public bool pinned { get; set; }
    public bool over_18 { get; set; }
    public Preview preview { get; set; }
    public object[] all_awardings { get; set; }
    public object[] awarders { get; set; }
    public bool media_only { get; set; }
    public string link_flair_template_id { get; set; }
    public bool can_gild { get; set; }
    public bool spoiler { get; set; }
    public bool locked { get; set; }
    public object author_flair_text { get; set; }
    public object[] treatment_tags { get; set; }
    public bool visited { get; set; }
    public object removed_by { get; set; }
    public object mod_note { get; set; }
    public object distinguished { get; set; }
    public string subreddit_id { get; set; }
    public bool author_is_blocked { get; set; }
    public object mod_reason_by { get; set; }
    public object num_reports { get; set; }
    public object removal_reason { get; set; }
    public string link_flair_background_color { get; set; }
    public string id { get; set; }
    public bool is_robot_indexable { get; set; }
    public object report_reasons { get; set; }
    public string author { get; set; }
    public object discussion_type { get; set; }
    public int num_comments { get; set; }
    public bool send_replies { get; set; }
    public bool contest_mode { get; set; }
    public object[] mod_reports { get; set; }
    public bool author_patreon_flair { get; set; }
    public object author_flair_text_color { get; set; }
    public string permalink { get; set; }
    public bool stickied { get; set; }
    public string url { get; set; }
    public int subreddit_subscribers { get; set; }
    public float created_utc { get; set; }
    public int num_crossposts { get; set; }
    public object media { get; set; }
    public bool is_video { get; set; }
    public Crosspost_Parent_List[] crosspost_parent_list { get; set; }
    public string crosspost_parent { get; set; }
    public bool is_gallery { get; set; }
    public Media_Metadata media_metadata { get; set; }
    public Gallery_Data gallery_data { get; set; }
}

public record Media_Embed
{
}

public record Secure_Media_Embed
{
}

public record Gildings
{
}

public record Preview
{
    public Image[] images { get; set; }
    public bool enabled { get; set; }
}

public record Image
{
    public Source source { get; set; }
    public Resolution4[] resolutions { get; set; }
    public Variants variants { get; set; }
    public string id { get; set; }
}

public record Source
{
    public string url { get; set; }
    public int width { get; set; }
    public int height { get; set; }
}

public record Variants
{
    public Obfuscated obfuscated { get; set; }
    public Nsfw nsfw { get; set; }
    public Gif gif { get; set; }
    public Mp4 mp4 { get; set; }
}

public record Obfuscated
{
    public Source1 source { get; set; }
    public Resolution[] resolutions { get; set; }
}

public record Source1
{
    public string url { get; set; }
    public int width { get; set; }
    public int height { get; set; }
}

public record Resolution
{
    public string url { get; set; }
    public int width { get; set; }
    public int height { get; set; }
}

public record Nsfw
{
    public Source2 source { get; set; }
    public Resolution1[] resolutions { get; set; }
}

public record Source2
{
    public string url { get; set; }
    public int width { get; set; }
    public int height { get; set; }
}

public record Resolution1
{
    public string url { get; set; }
    public int width { get; set; }
    public int height { get; set; }
}

public record Gif
{
    public Source3 source { get; set; }
    public Resolution2[] resolutions { get; set; }
}

public record Source3
{
    public string url { get; set; }
    public int width { get; set; }
    public int height { get; set; }
}

public record Resolution2
{
    public string url { get; set; }
    public int width { get; set; }
    public int height { get; set; }
}

public record Mp4
{
    public Source4 source { get; set; }
    public Resolution3[] resolutions { get; set; }
}

public record Source4
{
    public string url { get; set; }
    public int width { get; set; }
    public int height { get; set; }
}

public record Resolution3
{
    public string url { get; set; }
    public int width { get; set; }
    public int height { get; set; }
}

public record Resolution4
{
    public string url { get; set; }
    public int width { get; set; }
    public int height { get; set; }
}

public record Media_Metadata
{
    // This is all garbage names since they don't know what they're doing with JSON data
}

public record Gallery_Data
{
    public Item[] items { get; set; }
}

public record Item
{
    public string caption { get; set; }
    public string media_id { get; set; }
    public int id { get; set; }
}

public record Crosspost_Parent_List
{
    public object approved_at_utc { get; set; }
    public string subreddit { get; set; }
    public string selftext { get; set; }
    public string author_fullname { get; set; }
    public bool saved { get; set; }
    public object mod_reason_title { get; set; }
    public int gilded { get; set; }
    public bool clicked { get; set; }
    public string title { get; set; }
    public object[] link_flair_richtext { get; set; }
    public string subreddit_name_prefixed { get; set; }
    public bool hidden { get; set; }
    public object pwls { get; set; }
    public string link_flair_css_record { get; set; }
    public int downs { get; set; }
    public int? thumbnail_height { get; set; }
    public object top_awarded_type { get; set; }
    public bool hide_score { get; set; }
    public string name { get; set; }
    public bool quarantine { get; set; }
    public string link_flair_text_color { get; set; }
    public float upvote_ratio { get; set; }
    public string author_flair_background_color { get; set; }
    public int ups { get; set; }
    public int total_awards_received { get; set; }
    public Media_Embed1 media_embed { get; set; }
    public int? thumbnail_width { get; set; }
    public string author_flair_template_id { get; set; }
    public bool is_original_content { get; set; }
    public object[] user_reports { get; set; }
    public object secure_media { get; set; }
    public bool is_reddit_media_domain { get; set; }
    public bool is_meta { get; set; }
    public object category { get; set; }
    public Secure_Media_Embed1 secure_media_embed { get; set; }
    public string link_flair_text { get; set; }
    public bool can_mod_post { get; set; }
    public int score { get; set; }
    public object approved_by { get; set; }
    public bool is_created_from_ads_ui { get; set; }
    public bool author_premium { get; set; }
    public string thumbnail { get; set; }
    public bool edited { get; set; }
    public object author_flair_css_record { get; set; }
    public object[] author_flair_richtext { get; set; }
    public Gildings1 gildings { get; set; }
    public string post_hint { get; set; }
    public object content_categories { get; set; }
    public bool is_self { get; set; }
    public string subreddit_type { get; set; }
    public float created { get; set; }
    public string link_flair_type { get; set; }
    public object wls { get; set; }
    public object removed_by_category { get; set; }
    public object banned_by { get; set; }
    public string author_flair_type { get; set; }
    public string domain { get; set; }
    public bool allow_live_comments { get; set; }
    public object selftext_html { get; set; }
    public object likes { get; set; }
    public object suggested_sort { get; set; }
    public object banned_at_utc { get; set; }
    public string url_overridden_by_dest { get; set; }
    public object view_count { get; set; }
    public bool archived { get; set; }
    public bool no_follow { get; set; }
    public bool is_crosspostable { get; set; }
    public bool pinned { get; set; }
    public bool over_18 { get; set; }
    public Preview1 preview { get; set; }
    public object[] all_awardings { get; set; }
    public object[] awarders { get; set; }
    public bool media_only { get; set; }
    public string link_flair_template_id { get; set; }
    public bool can_gild { get; set; }
    public bool spoiler { get; set; }
    public bool locked { get; set; }
    public string author_flair_text { get; set; }
    public object[] treatment_tags { get; set; }
    public bool visited { get; set; }
    public object removed_by { get; set; }
    public object mod_note { get; set; }
    public object distinguished { get; set; }
    public string subreddit_id { get; set; }
    public bool author_is_blocked { get; set; }
    public object mod_reason_by { get; set; }
    public object num_reports { get; set; }
    public object removal_reason { get; set; }
    public string link_flair_background_color { get; set; }
    public string id { get; set; }
    public bool is_robot_indexable { get; set; }
    public object report_reasons { get; set; }
    public string author { get; set; }
    public object discussion_type { get; set; }
    public int num_comments { get; set; }
    public bool send_replies { get; set; }
    public bool contest_mode { get; set; }
    public object[] mod_reports { get; set; }
    public bool author_patreon_flair { get; set; }
    public string author_flair_text_color { get; set; }
    public string permalink { get; set; }
    public bool stickied { get; set; }
    public string url { get; set; }
    public int subreddit_subscribers { get; set; }
    public float created_utc { get; set; }
    public int num_crossposts { get; set; }
    public object media { get; set; }
    public bool is_video { get; set; }
}

public record Media_Embed1
{
}

public record Secure_Media_Embed1
{
}

public record Gildings1
{
}

public record Preview1
{
    public Image1[] images { get; set; }
    public bool enabled { get; set; }
}

public record Image1
{
    public Source5 source { get; set; }
    public Resolution7[] resolutions { get; set; }
    public Variants1 variants { get; set; }
    public string id { get; set; }
}

public record Source5
{
    public string url { get; set; }
    public int width { get; set; }
    public int height { get; set; }
}

public record Variants1
{
    public Obfuscated1 obfuscated { get; set; }
    public Nsfw1 nsfw { get; set; }
}

public record Obfuscated1
{
    public Source6 source { get; set; }
    public Resolution5[] resolutions { get; set; }
}

public record Source6
{
    public string url { get; set; }
    public int width { get; set; }
    public int height { get; set; }
}

public record Resolution5
{
    public string url { get; set; }
    public int width { get; set; }
    public int height { get; set; }
}

public record Nsfw1
{
    public Source7 source { get; set; }
    public Resolution6[] resolutions { get; set; }
}

public record Source7
{
    public string url { get; set; }
    public int width { get; set; }
    public int height { get; set; }
}

public record Resolution6
{
    public string url { get; set; }
    public int width { get; set; }
    public int height { get; set; }
}

public record Resolution7
{
    public string url { get; set; }
    public int width { get; set; }
    public int height { get; set; }
}
#pragma warning restore S2094 // Classes should not be empty
#pragma warning restore S101 // Types should be named in PascalCase
#pragma warning restore IDE1006 // Names must begin in upper case

