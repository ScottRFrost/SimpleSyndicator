using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.ServiceModel.Syndication;
using System.Xml;

namespace SimpleSyndicator
{
    public class RedditSource : BaseSource
    {
        public override async Task<SyndicatorContent[]> Parse(string Url, string Tags)
        {
            Console.WriteLine(DateTime.Now.ToString("M/d HH:mm") + " -=[ Start RedditSource ]=-");
            using var xmlReader = XmlReader.Create(Url);
            var rss = SyndicationFeed.Load(xmlReader);
            var syndicatorContents = new List<SyndicatorContent>();
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
                    imageURL = imageURL[(imageURL.LastIndexOf("\"") + 1)..];
                    if(imageURL.EndsWith(".jpg", StringComparison.InvariantCultureIgnoreCase) || imageURL.EndsWith(".png", StringComparison.InvariantCultureIgnoreCase))
                    {
                        syndicatorContent.ImageURL = imageURL;
                    }
                }

                // Shorten URL
                if (syndicatorContent.LinkURL.Contains("/comments/"))
                {
                    syndicatorContent.LinkURL =  syndicatorContent.LinkURL[(syndicatorContent.LinkURL.IndexOf("/comments/") + 10)..];
                    syndicatorContent.LinkURL = string.Concat("https://redd.it/", syndicatorContent.LinkURL.AsSpan(0 , syndicatorContent.LinkURL.IndexOf("/")));
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
