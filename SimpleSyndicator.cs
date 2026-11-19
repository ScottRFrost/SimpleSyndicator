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
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static string GetConfigPath() => Path.Combine(Directory.GetCurrentDirectory(), "config.json");

    public static async Task RunRedditAuthAsync()
    {
        var configPath = GetConfigPath();
        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException(
                $"config.json not found at '{configPath}'. Create it first with a \"Reddit\" section containing ClientId, ClientSecret, and RedirectUri.",
                configPath);
        }

        Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Reading config...");
        var json = await File.ReadAllTextAsync(configPath).ConfigureAwait(false);
        var config = JsonSerializer.Deserialize<Config>(json, _jsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize config.json.");

        if (config.Reddit == null)
        {
            throw new InvalidOperationException(
                "config.json is missing the \"Reddit\" section. Add \"Reddit\": { \"ClientId\": \"...\", \"ClientSecret\": \"...\", \"RedirectUri\": \"http://localhost:8080/callback\" } and re-run with --reddit-auth.");
        }

        using var authClient = new RedditAuthClient(config.Reddit);
        await authClient.RunInteractiveOAuthAsync().ConfigureAwait(false);

        await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(config, _jsonOptions), System.Text.Encoding.UTF8).ConfigureAwait(false);
        Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Saved Reddit RefreshToken and AccessToken to config.json.");
    }

    public static async Task<bool> Start()
    {
        // Read Config
        var configPath = GetConfigPath();
        Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Reading config...");
        var json = await File.ReadAllTextAsync(configPath).ConfigureAwait(false);
        var config = JsonSerializer.Deserialize<Config>(json, _jsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize config.json.");
        config.Sources ??= [];
        config.SyndicatedURLs ??= [];
        var startingCount = config.SyndicatedURLs.Count;
        Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Read config.  {config.Sources.Count} sources, {startingCount} previously syndicated images.");

        // Only fetch Chrome User-Agent upfront if Reddit is running in browser-cookie mode
        var userAgentInitialized = false;
        if (RedditAuthClient.ResolveAuthMode(config.Reddit) == RedditAuthMode.SessionCookie)
        {
            await SimpleHttpClient.InitializeUserAgent().ConfigureAwait(false);
            userAgentInitialized = true;
        }

        // Telegram Setup
        var useTelegram = !string.IsNullOrWhiteSpace(config.TelegramKey);
        TelegramBotClient telegram = null;

        // Parse Reddit Sources
        using var redditSource = new RedditSource(config.Reddit);
        var foundContent = new List<SyndicatorContent>();
        var redditCircuitBroken = false;
        var sourceDelayBaseSeconds = config.Reddit?.SourceDelaySeconds ?? (redditSource.AuthMode == RedditAuthMode.OAuth2 ? 5 : 30);

        for (int i = 0; i < config.Sources.Count; i++)
        {
            var source = config.Sources[i];
            var isReddit = string.Equals(source.Type, "reddit", StringComparison.OrdinalIgnoreCase);
            if (isReddit && redditCircuitBroken)
            {
                continue;
            }

            Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Getting {source.Type} - {source.URL}");
            try
            {
                if (isReddit)
                {
                    foundContent.AddRange(await redditSource.Parse(source).ConfigureAwait(false));
                }
            }
            catch (RedditApiException ex) when (ex.ShouldAbortRun)
            {
                Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Circuit breaker tripped on {source.Type} - {source.URL}: {ex.Message}");
                Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Skipping any remaining Reddit sources in this run.");
                redditCircuitBroken = true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Error on {source.Type} - {source.URL}\r\n{ex}");
            }

            var hasMoreRunnableSources = false;
            for (int j = i + 1; j < config.Sources.Count; j++)
            {
                var nextIsReddit = string.Equals(config.Sources[j].Type, "reddit", StringComparison.OrdinalIgnoreCase);
                if (!redditCircuitBroken || !nextIsReddit)
                {
                    hasMoreRunnableSources = true;
                    break;
                }
            }

            if (hasMoreRunnableSources)
            {
                var pause = RedditAuthClient.ComputeJitteredDelay(sourceDelayBaseSeconds);
                if (pause > TimeSpan.Zero)
                {
                    Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Pausing {pause.TotalSeconds:F1} seconds before next source...");
                    await Task.Delay(pause).ConfigureAwait(false);
                }
            }
        }

        // Check content to see if it's been syndicated before, then syndicate it if it hasn't
        Console.WriteLine();
        if (foundContent.Count > 0)
        {
            Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} -=[ Parsing {foundContent.Count} Results ]=-");
            if (!userAgentInitialized)
            {
                await SimpleHttpClient.InitializeUserAgent().ConfigureAwait(false);
            }
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
            var mediaDelayBaseSeconds = config.Reddit?.MediaDelaySeconds ?? 5;
            var processedNewItems = 0;
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
                    if (processedNewItems > 0)
                    {
                        var mediaDelay = RedditAuthClient.ComputeJitteredDelay(mediaDelayBaseSeconds);
                        if (mediaDelay > TimeSpan.Zero)
                        {
                            await Task.Delay(mediaDelay).ConfigureAwait(false);
                        }
                    }
                    processedNewItems++;

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
                                var cleanImageUrl = content.ImageURL.Split('?')[0];
                                var fileName = cleanImageUrl[(cleanImageUrl.LastIndexOf('/') + 1)..];
                                var file = Telegram.Bot.Types.InputFile.FromStream(imageStream, fileName);
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
        await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(config, _jsonOptions), System.Text.Encoding.UTF8).ConfigureAwait(false);
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
