using Microsoft.Toolkit.Parsers.Rss;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SimpleSyndicator
{
    public class RedditSource : BaseSource
    {
        public override async Task<SyndicatorContent[]> Parse(string Url, string Tags)
        {
            Console.WriteLine(DateTime.Now.ToString("M/d HH:mm") + " -=[ Start RedditSource ]=-");
            var httpClient = new SimpleHttpClient();
            var rssString = await httpClient.DownloadString(Url);
            var rssParser = new RssParser();
            var syndicatorContents = new List<SyndicatorContent>();
            foreach (var item in rssParser.Parse(rssString))
            {
                /* DEBUG
                Console.WriteLine("Author:" + item.Author);
                Console.WriteLine("Categories:" + item.Categories);
                Console.WriteLine("Content:" + item.Content);
                Console.WriteLine("ExtraImageUrl:" + item.ExtraImageUrl);
                Console.WriteLine("FeedUrl:" + item.FeedUrl);
                Console.WriteLine("ImageUrl:" + item.ImageUrl);
                Console.WriteLine("MediaUrl:" + item.MediaUrl);
                Console.WriteLine("PublishDate:" + item.PublishDate);
                Console.WriteLine("Summary:" + item.Summary);
                Console.WriteLine("Title:" + item.Title);
                Console.WriteLine("----");
                */
                var syndicatorContent = new SyndicatorContent
                {
                    Description = (item.Title + " " + Tags).Trim(),
                    LinkURL = item.FeedUrl,
                    ImageURL = string.Empty
                };

                // Find image URL via [link] in Content
                if (item.Content.Contains(">[link]<"))
                {
                    var imageURL = item.Content.Substring(0, item.Content.IndexOf(">[link]<") - 1);
                    imageURL = imageURL.Substring(imageURL.LastIndexOf("\"") + 1);
                    if(imageURL.EndsWith(".jpg", StringComparison.InvariantCultureIgnoreCase) || imageURL.EndsWith(".png", StringComparison.InvariantCultureIgnoreCase))
                    {
                        syndicatorContent.ImageURL = imageURL;
                    }
                }
                ////else if(item.Content.Contains("<a href=\"https://i.redd.it/"))
                ////{
                ////    syndicatorContent.ImageURL = item.Content.Substring(item.Content.IndexOf("<a href=\"https://i.redd.it/")).Replace("<a href=\"","");
                ////    syndicatorContent.ImageURL = syndicatorContent.ImageURL.Substring(0, syndicatorContent.ImageURL.IndexOf("\""));
                ////}
                ////else if (item.Content.Contains("<a href=\"https://i.imgur.com/"))
                ////{
                ////    syndicatorContent.ImageURL = item.Content.Substring(item.Content.IndexOf("<a href=\"https://i.imgur.com/")).Replace("<a href=\"", "");
                ////    syndicatorContent.ImageURL = syndicatorContent.ImageURL.Substring(0, syndicatorContent.ImageURL.IndexOf("\""));
                ////}
                ////else if (item.Content.Contains("<a href=\"https://static1.e621.net/"))
                ////{
                ////    syndicatorContent.ImageURL = item.Content.Substring(item.Content.IndexOf("<a href=\"https://static1.e621.net/")).Replace("<a href=\"", "");
                ////    syndicatorContent.ImageURL = syndicatorContent.ImageURL.Substring(0, syndicatorContent.ImageURL.IndexOf("\""));
                ////}

                // Shorten URL
                if (syndicatorContent.LinkURL.Contains("/comments/"))
                {
                    syndicatorContent.LinkURL =  syndicatorContent.LinkURL.Substring(syndicatorContent.LinkURL.IndexOf("/comments/") + 10);
                    syndicatorContent.LinkURL = "https://redd.it/" + syndicatorContent.LinkURL.Substring(0 , syndicatorContent.LinkURL.IndexOf("/"));
                }

                // If we found an image, return this item
                if(syndicatorContent.ImageURL != string.Empty)
                {
                    Console.WriteLine(syndicatorContent.Description + " | " + syndicatorContent.LinkURL + " | " + syndicatorContent.ImageURL);
                    syndicatorContents.Add(syndicatorContent);
                }
            }
            Console.WriteLine(DateTime.Now.ToString("M/d HH:mm") + " -=[ End RedditSource ]=-");
            Console.WriteLine();
            return syndicatorContents.ToArray();
        }
    }
}
