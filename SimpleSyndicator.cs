using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System;
using Telegram.Bot.Types.Enums;
using Telegram.Bot;
using static SimpleSyndicator.BaseSource;

namespace SimpleSyndicator;

static class SimpleSyndicator
{
    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public static async Task<bool> Start()
    {
        // Read Config
        Console.WriteLine($"{DateTime.Now:M/d HH:mm} Reading config...");
        var json = await File.ReadAllTextAsync($"{Directory.GetCurrentDirectory()}{Path.DirectorySeparatorChar}config.json").ConfigureAwait(false);
        var config = JsonSerializer.Deserialize<Config>(json, _jsonOptions);
        config.SyndicatedURLs ??= [];
        var startingCount = config.SyndicatedURLs.Count;
        Console.WriteLine($"{DateTime.Now:M/d HH:mm} Read config.  {config.Sources.Count} sources, {startingCount} previously syndicated images.");

        // Telegram Setup
        var useTelegram = false;
        if(config.TelegramKey.Length > 0)
        {
            useTelegram = true;
        }
        TelegramBotClient telegram = null;

        // Parse Reddit Sources
        var redditSource = new RedditSource();
        var foundContent = new List<SyndicatorContent>();
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
                Console.WriteLine(DateTime.Now.ToString("M/d HH:mm") + " Error on " + source.Type + " - " + source.URL + "\r\n" + ex);
            }
        }

        // Check content to see if it's been syndicated before, then syndicate it if it hasn't
        Console.WriteLine();
        if (foundContent.Count > 0)
        {
            Console.WriteLine(DateTime.Now.ToString("M/d HH:mm") + "-=[ " + foundContent.Count.ToString() + " Results ]=-");
            #pragma warning disable S4830 // Server certificates should be verified during SSL/TLS connections
            var handler = new HttpClientHandler()
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            };
            #pragma warning restore S4830 // Server certificates should be verified during SSL/TLS connections
            using var http = new SimpleHttpClient(handler);
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
                        new ConfigSyndicatedUrl
                        {
                            Syndicated = DateTime.Now,
                            URL = content.LinkURL,
                            Description = content.Description
                        }
                    );

                    // Get image data to stream and move it into a byte array
                    var imageStream = new MemoryStream();
                    try
                    {
                        // Reddit has some annoying anti-downloader crap, change the Accept header to bypass it
                        if (content.ImageURL.Contains("i.redd.it"))
                        {
                            http.DefaultRequestHeaders.Accept.Clear();
                            http.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("*/*"));
                        }
                        await using var downloadStream = await http.DownloadData(content.ImageURL).ConfigureAwait(false);
                        await downloadStream.CopyToAsync(imageStream).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("Error while downloading - " + ex);
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
                                await telegram.SendChatAction(channel, ChatAction.Typing).ConfigureAwait(false);
                                imageStream.Seek(0, SeekOrigin.Begin);
                                var file = Telegram.Bot.Types.InputFile.FromStream(imageStream, content.ImageURL[(content.ImageURL.LastIndexOf('/') + 1)..]);
                                if (content.ImageURL.Contains(".gif", StringComparison.InvariantCultureIgnoreCase) || content.ImageURL.Contains(".mp4", StringComparison.InvariantCultureIgnoreCase))
                                {
                                    await telegram.SendAnimation(channel, file, caption: content.Description + " " + content.LinkURL).ConfigureAwait(false);
                                }
                                else
                                {
                                    await telegram.SendPhoto(channel, file, caption: content.Description + " " + content.LinkURL).ConfigureAwait(false);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine("Error while posting to Telegram - " + ex);
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
        await File.WriteAllTextAsync($"{Directory.GetCurrentDirectory()}{Path.DirectorySeparatorChar}config.json", JsonSerializer.Serialize(config, _jsonOptions), System.Text.Encoding.UTF8).ConfigureAwait(false);
        Console.WriteLine($"{DateTime.Now:M/d HH:mm} Wrote config.  {config.Sources.Count} sources, {startingCount} >> {config.SyndicatedURLs.Count} syndicated images.");

        return true;
    }
}
