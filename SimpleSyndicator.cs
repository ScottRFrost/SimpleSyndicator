using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Tweetinvi;
using Tweetinvi.Models;
using Tweetinvi.Parameters;
using static SimpleSyndicator.BaseSource;

namespace SimpleSyndicator
{
    class SimpleSyndicator
    {
        public async Task<bool> Start()
        {
            // Read Config
            Console.WriteLine(DateTime.Now.ToString("M/d HH:mm") + " Reading config...");
            var config = JsonConvert.DeserializeObject<Config>(File.ReadAllText("config.json"));
            if (config.SyndicatedURLs == null)
            {
                config.SyndicatedURLs = new List<SyndicatedURL>();
            }
            var foundContent = new List<SyndicatorContent>();
            Console.WriteLine(DateTime.Now.ToString("M/d HH:mm") + " Read config");

            // Authentication
            Auth.SetUserCredentials(config.TwitterConsumerKey, config.TwitterConsumerSecret, config.TwitterAccessToken, config.TwitterTokenSecret);
            var twitterUser = User.GetAuthenticatedUser();

            // Parse Reddit Sources
            var redditSource = new RedditSource();
            foreach (var source in config.Sources)
            {
                Console.WriteLine(DateTime.Now.ToString("M/d HH:mm") + " Getting " + source.Type + " - " + source.URL);
                try
                {
                    switch (source.Type.ToLowerInvariant())
                    {
                        case "reddit":
                            foundContent.AddRange(await redditSource.Parse(source.URL, source.Tags).ConfigureAwait(false));
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine(DateTime.Now.ToString("M/d HH:mm") + " Error on " + source.Type + " - " + source.URL + "\r\n" + ex.ToString());
                }
            }

            // Check content to see if it's been syndicated before, then syndicate it if it hasn't
            Console.WriteLine();
            Console.WriteLine(DateTime.Now.ToString("M/d HH:mm") + "-=[ New Results ]=-");
            using var httpClient = new SimpleHttpClient();
            foreach (var content in foundContent)
            {
                var previouslySyndicated = false;
                foreach (var syndicatedURL in config.SyndicatedURLs)
                {
                    if (syndicatedURL.URL == content.LinkURL)
                    {
                        previouslySyndicated = true;
                        continue;
                    }
                }
                if (!previouslySyndicated)
                {
                    // Write to Console
                    Console.WriteLine(content.Description + " " + content.LinkURL);
                    Console.WriteLine(content.ImageURL);

                    // Save to config
                    config.SyndicatedURLs.Add
                    (
                        new SyndicatedURL
                        {
                            Syndicated = DateTime.Now,
                            URL = content.LinkURL
                        }
                    );

                    // Get image data to stream and move it into a byte array
                    System.IO.Stream downloadStream;
                    var imageStream = new MemoryStream();
                    byte[] imageBytes = null;
                    try
                    {
                        downloadStream = await httpClient.DownloadData(content.ImageURL).ConfigureAwait(false);
                        downloadStream.CopyTo(imageStream);
                        imageBytes = imageStream.ToArray();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("Error while downloading - " + ex.ToString());
                        continue;
                    }

                    // Send to each syndication output

                    ////// IFTTT Web Hooks
                    ////if (config.IFTTTWebHooks != null)
                    ////{
                    ////    foreach (var webhook in config.IFTTTWebHooks)
                    ////    {
                    ////        Console.WriteLine(DateTime.Now.ToString("M/d HH:mm") + " Sending " + content.LinkURL + " to IFTTT Webhook");
                    ////        _ = httpClient.DownloadString(webhook + "?value1=" + WebUtility.UrlEncode(content.Description + " " + content.LinkURL) + "&value2=" + WebUtility.UrlEncode(content.ImageURL));
                    ////    }
                    ////}
                    ///

                    // Telegram
                    //// TODO 

                    // Twitter
                    try
                    {
                        // Twitter doesn't allow images over 5MB to be uploaded via API
                        if(imageBytes.Length > 5000000)
                        {
                            var originalImage = Image.FromStream(imageStream);
                            using var newImage = new Bitmap(originalImage.Width, originalImage.Height);
                            using var graphics = Graphics.FromImage(originalImage);
                            graphics.SmoothingMode = SmoothingMode.AntiAlias;
                            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                            graphics.DrawImage(originalImage, new Rectangle(0, 0, originalImage.Width, originalImage.Height));

                            // Keep trying compression quality settings until we get to where we need to be
                            var over5MB = true;
                            var quality = 90L;
                            while (over5MB)
                            {
                                var encoder = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
                                var encParams = new EncoderParameters() { Param = new[] { new EncoderParameter(Encoder.Quality, quality) } };
                                var resampleStream = new MemoryStream();
                                newImage.Save(resampleStream, encoder, encParams);
                                if (resampleStream.Length <= 5000000)
                                {
                                    over5MB = false;
                                    imageBytes = resampleStream.ToArray();
                                }
                                quality -= 5L;
                                resampleStream.Dispose();
                            }
                        }

                        var publishedTweet = Auth.ExecuteOperationWithCredentials(twitterUser.Credentials, () =>
                        {
                            var uploadedImage = Upload.UploadBinary(imageBytes);
                            var publishOptions = new PublishTweetOptionalParameters
                            {
                                Medias = new List<IMedia>() { uploadedImage }
                            };

                            return Tweet.PublishTweet(content.Description + " " + content.LinkURL, publishOptions);
                        });
                    }
                    catch(Exception ex)
                    {
                        Console.WriteLine("Error while posting to twitter - " + ex.ToString());
                    }
                }
            }

            // Clean up entries SyndicatedURLs that are more than 30 days
            foreach (var item in config.SyndicatedURLs.FindAll(s => s.Syndicated < DateTime.Today.AddDays(-30)).ToArray())
            {
                config.SyndicatedURLs.Remove(item);
            }

            // Save Config
            File.WriteAllText("config.json", JsonConvert.SerializeObject(config, Formatting.Indented), System.Text.Encoding.UTF8);

            return true;
        }
    }
}
