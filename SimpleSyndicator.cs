using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Text.Json;
using System.Text.Json.Serialization;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;
using static SimpleSyndicator.BaseSource;

namespace SimpleSyndicator
{
    static class SimpleSyndicator
    {
        public static async Task<bool> Start()
        {
            // Read Config
            Console.WriteLine(DateTime.Now.ToString("M/d HH:mm") + " Reading config...");
            var json = File.ReadAllText(Directory.GetCurrentDirectory() + Path.DirectorySeparatorChar + "config.json");
            var config = JsonSerializer.Deserialize<Config>(json);
            config.SyndicatedURLs ??= new List<SyndicatedURL>();
            var foundContent = new List<SyndicatorContent>();
            Console.WriteLine(DateTime.Now.ToString("M/d HH:mm") + " Read config");

            // Telegram Setup
            var useTelegram = false;
            if(config.TelegramKey.Length > 0)
            {
                useTelegram = true;
            }
            TelegramBotClient telegram = null;

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
            if(foundContent.Count > 0)
            {
                Console.WriteLine(DateTime.Now.ToString("M/d HH:mm") + "-=[ " + foundContent.Count.ToString() + " Results ]=-");

                using var httpClient = new SimpleHttpClient();
                if (useTelegram)
                {
                    telegram = new TelegramBotClient(config.TelegramKey);
                }
                foreach (var content in foundContent)
                {
                    var previouslySyndicated = false;
                    foreach (var syndicatedURL in config.SyndicatedURLs)
                    {
                        // If the URL or description is an exact match, don't syndicate it again
                        if (syndicatedURL.URL == content.LinkURL || (syndicatedURL.Description != null && syndicatedURL.Description == content.Description))
                        {
                            previouslySyndicated = true;
                            break;
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
                                URL = content.LinkURL,
                                Description = content.Description
                            }
                        );

                        // Get image data to stream and move it into a byte array
                        Stream downloadStream;
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

                        // Telegram (Don't send tags along with it, because we don't need search)
                        if (useTelegram)
                        {
                            try
                            {
                                foreach (var channel in config.TelegramChannelIDs)
                                {
                                    _ = telegram.SendChatActionAsync(channel, ChatAction.Typing);
                                    imageStream.Seek(0, SeekOrigin.Begin);
                                    var file = Telegram.Bot.Types.InputFile.FromStream(imageStream);
                                    if (content.ImageURL.Contains(".gif", StringComparison.InvariantCultureIgnoreCase) || content.ImageURL.Contains(".mp4", StringComparison.InvariantCultureIgnoreCase))
                                    {
                                        await telegram.SendAnimationAsync(channel, file, caption: content.Description + " " + content.LinkURL);
                                    }
                                    else
                                    {
                                        await telegram.SendPhotoAsync(channel, file, caption: content.Description + " " + content.LinkURL);
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine("Error while posting to Telegram - " + ex.ToString());
                            }
                        }
                    }
                }
            }
            else
            {
                Console.WriteLine(DateTime.Now.ToString("M/d HH:mm") + "-=[ NO Results ]=-");
            }

            // Clean up entries SyndicatedURLs that are more than 30 days
            foreach (var item in config.SyndicatedURLs.FindAll(s => s.Syndicated < DateTime.Today.AddDays(-30)).ToArray())
            {
                config.SyndicatedURLs.Remove(item);
            }

            // Save Config
            File.WriteAllText("config.json", JsonSerializer.Serialize(config,new JsonSerializerOptions { WriteIndented = true }), System.Text.Encoding.UTF8);

            return true;
        }
    }
}
