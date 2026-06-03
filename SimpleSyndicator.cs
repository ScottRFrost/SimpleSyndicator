using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System;
using Telegram.Bot.Types.Enums;
using Telegram.Bot;
using static SimpleSyndicator.BaseSource;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Formats;

namespace SimpleSyndicator;

static class SimpleSyndicator
{
    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public static async Task<bool> Start()
    {
        // Fetch latest Chrome + Windows User-Agent on startup
        await SimpleHttpClient.InitializeUserAgent().ConfigureAwait(false);

        // Read Config
        Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Reading config...");
        var json = await File.ReadAllTextAsync($"{Directory.GetCurrentDirectory()}{Path.DirectorySeparatorChar}config.json").ConfigureAwait(false);
        var config = JsonSerializer.Deserialize<Config>(json, _jsonOptions);
        config.SyndicatedURLs ??= [];
        var startingCount = config.SyndicatedURLs.Count;
        Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Read config.  {config.Sources.Count} sources, {startingCount} previously syndicated images.");

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
        for (int i = 0; i < config.Sources.Count; i++)
        {
            var source = config.Sources[i];
            Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Getting {source.Type} - {source.URL}");
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
                Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Error on {source.Type} - {source.URL}\r\n{ex}");
            }

            if (i < config.Sources.Count - 1)
            {
                Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Pausing 30 seconds before next source...");
                await Task.Delay(30000).ConfigureAwait(false);
            }
        }

        // Check content to see if it's been syndicated before, then syndicate it if it hasn't
        Console.WriteLine();
        if (foundContent.Count > 0)
        {
            Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} -=[ Parsing {foundContent.Count} Results ]=-");
            #pragma warning disable S4830 // Server certificates should be verified during SSL/TLS connections
            var handler = new HttpClientHandler()
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
                AutomaticDecompression = System.Net.DecompressionMethods.All
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
                    Console.WriteLine($"{content.Description} {content.LinkURL} -- {content.ImageURL}");

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
                        if (content.ImageURL.Contains("redd.it") || content.ImageURL.Contains("reddit"))
                        {
                            http.DefaultRequestHeaders.Accept.Clear();
                            http.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("*/*"));
                        }
                        await using var downloadStream = await http.DownloadData(content.ImageURL).ConfigureAwait(false);
                        await downloadStream.CopyToAsync(imageStream).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error while downloading - {ex}");
                        continue;
                    }

                    // Check if image dimensions are too large for Telegram and resize if necessary
                    bool isPhoto = !content.ImageURL.Contains(".gif", StringComparison.InvariantCultureIgnoreCase) &&
                                   !content.ImageURL.Contains(".mp4", StringComparison.InvariantCultureIgnoreCase);
                    if (isPhoto)
                    {
                        imageStream.Seek(0, SeekOrigin.Begin);
                        try
                        {
                            using var image = await SixLabors.ImageSharp.Image.LoadAsync(imageStream);
                            var originalWidth = image.Width;
                            var originalHeight = image.Height;

                            var newWidth = originalWidth;
                            var newHeight = originalHeight;

                            var resized = false;

                            // Check max dimensions (5120x5120)
                            if (newWidth > 5120 || newHeight > 5120)
                            {
                                var ratio = Math.Min(5120.0 / newWidth, 5120.0 / newHeight);
                                newWidth = (int)Math.Floor(newWidth * ratio);
                                newHeight = (int)Math.Floor(newHeight * ratio);
                                resized = true;
                            }

                            // Check total sum of width + height <= 10000
                            if (newWidth + newHeight > 10000)
                            {
                                var ratio = 10000.0 / (newWidth + newHeight);
                                newWidth = (int)Math.Floor(newWidth * ratio);
                                newHeight = (int)Math.Floor(newHeight * ratio);
                                resized = true;
                            }

                            // Check aspect ratio <= 20:1
                            if ((double)newWidth / newHeight > 20.0)
                            {
                                newWidth = newHeight * 20;
                                resized = true;
                            }
                            else if ((double)newHeight / newWidth > 20.0)
                            {
                                newHeight = newWidth * 20;
                                resized = true;
                            }

                            // Save the resized image to the stream
                            if (resized)
                            {
                                var format = image.Metadata.DecodedImageFormat;
                                image.Mutate(x => x.Resize(newWidth, newHeight));
                                var resizedStream = new MemoryStream();
                                if (format != null)
                                {
                                    await image.SaveAsync(resizedStream, format).ConfigureAwait(false);
                                }
                                else
                                {
                                    await image.SaveAsJpegAsync(resizedStream).ConfigureAwait(false);
                                }

                                await imageStream.DisposeAsync();
                                imageStream = resizedStream;
                            }

                            // Check if the stream size still exceeds 10 MB (10,485,760 bytes)
                            if (imageStream.Length > 10485760)
                            {
                                var format = image.Metadata.DecodedImageFormat;
                                int quality = 80;
                                double scale = 1.0;
                                bool sizeFits = false;
                                MemoryStream compressedStream = null;

                                while (!sizeFits && scale >= 0.05)
                                {
                                    compressedStream = new MemoryStream();

                                    // Resize if scale is reduced
                                    if (scale < 1.0)
                                    {
                                        int targetWidth = Math.Max(1, (int)Math.Floor(image.Width * scale));
                                        int targetHeight = Math.Max(1, (int)Math.Floor(image.Height * scale));
                                        using var clonedImage = image.Clone(x => x.Resize(targetWidth, targetHeight));
                                        await SaveWithQualityAsync(clonedImage, compressedStream, format, quality).ConfigureAwait(false);
                                    }
                                    else
                                    {
                                        await SaveWithQualityAsync(image, compressedStream, format, quality).ConfigureAwait(false);
                                    }

                                    if (compressedStream.Length <= 10485760)
                                    {
                                        sizeFits = true;
                                        await imageStream.DisposeAsync();
                                        imageStream = compressedStream;
                                    }
                                    else
                                    {
                                        await compressedStream.DisposeAsync();

                                        // Step 1: Reduce quality
                                        if (quality > 50)
                                        {
                                            quality -= 15;
                                        }
                                        // Step 2: Scale down dimensions
                                        else
                                        {
                                            scale *= 0.8;
                                            quality = 75; // reset quality for smaller dimensions
                                        }
                                    }
                                }
                            }
                        }
                        #pragma warning disable RCS1075 // Avoid empty catch clause that catches System.Exception
                        catch (Exception)
                        {
                            // Resizing skipped (e.g. if it's an MP4, unsupported format, or parsing error)
                        }
                        #pragma warning restore RCS1075
                        finally
                        {
                            imageStream.Seek(0, SeekOrigin.Begin);
                        }
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

                                await Task.Delay(2000).ConfigureAwait(false);
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error while posting to Telegram - {ex}");
                        }
                    }

					// Pause for 5 seconds between each image to look like human browsing
					await Task.Delay(5000).ConfigureAwait(false);
                }
            }
        }
        else
        {
            Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} -=[ NO Results ]=-");
        }

        // Clean up entries SyndicatedURLs that are more than 30 days
        foreach (var item in config.SyndicatedURLs.FindAll(s => s.Syndicated < DateTime.Today.AddDays(-30)).ToArray())
        {
            config.SyndicatedURLs.Remove(item);
        }

        // Save Config
        await File.WriteAllTextAsync($"{Directory.GetCurrentDirectory()}{Path.DirectorySeparatorChar}config.json", JsonSerializer.Serialize(config, _jsonOptions), System.Text.Encoding.UTF8).ConfigureAwait(false);
        Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Wrote config.  {config.Sources.Count} sources, {startingCount} >> {config.SyndicatedURLs.Count} syndicated images.");

        return true;
    }

    private static async Task SaveWithQualityAsync(SixLabors.ImageSharp.Image image, Stream stream, IImageFormat format, int quality)
    {
        if (format?.Name.Equals("JPEG", StringComparison.OrdinalIgnoreCase) == true)
        {
            await image.SaveAsync(stream, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = quality }).ConfigureAwait(false);
        }
        else if (format?.Name.Equals("WEBP", StringComparison.OrdinalIgnoreCase) == true)
        {
            await image.SaveAsync(stream, new SixLabors.ImageSharp.Formats.Webp.WebpEncoder { Quality = quality }).ConfigureAwait(false);
        }
        else
        {
            // Fallback: save other formats (like high-size PNGs) as JPEG to reduce file size significantly
            await image.SaveAsync(stream, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = quality }).ConfigureAwait(false);
        }
    }
}
