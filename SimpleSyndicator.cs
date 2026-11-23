using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;
using static SimpleSyndicator.BaseSource;

namespace SimpleSyndicator;

static class SimpleSyndicator
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static string GetConfigPath() => Path.Combine(Directory.GetCurrentDirectory(), "config.json");

    public static async Task InstallBrowsersAsync(bool withDeps = false)
    {
        var browserName = "chromium";
        var configPath = GetConfigPath();
        if (File.Exists(configPath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(configPath).ConfigureAwait(false);
                var config = JsonSerializer.Deserialize<Config>(json, _jsonOptions);
                if (!string.IsNullOrWhiteSpace(config?.Reddit?.Browser))
                {
                    browserName = config.Reddit.Browser;
                }
            }
            catch
            {
                // Fall back to default browser when config.json cannot be read yet
            }
        }

        Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Installing Playwright {browserName} browser{(withDeps ? " with OS dependencies" : string.Empty)}...");
        RedditBrowserClient.InstallBrowsers(browserName, withDeps);
        Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Playwright browser installation complete.");
    }

    public static async Task RunRedditLoginAsync()
    {
        RedditConfig redditConfig = null;
        var configPath = GetConfigPath();
        if (File.Exists(configPath))
        {
            Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Reading config...");
            var json = await File.ReadAllTextAsync(configPath).ConfigureAwait(false);
            var config = JsonSerializer.Deserialize<Config>(json, _jsonOptions);
            redditConfig = config?.Reddit;
        }

        redditConfig ??= new RedditConfig();

        await using var browserClient = new RedditBrowserClient(redditConfig);
        await browserClient.RunInteractiveLoginAsync().ConfigureAwait(false);
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

        var syndicatedUrls = new HashSet<string>(StringComparer.Ordinal);
        var syndicatedDescriptions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in config.SyndicatedURLs)
        {
            if (!string.IsNullOrEmpty(item.URL))
            {
                syndicatedUrls.Add(item.URL);
            }

            if (!string.IsNullOrEmpty(item.Description))
            {
                syndicatedDescriptions.Add(item.Description);
            }
        }

        // Telegram Setup
        var useTelegram = !string.IsNullOrWhiteSpace(config.TelegramKey);
        TelegramBotClient telegram = null;

        // Parse Reddit Sources via Playwright
        await using var redditSource = new RedditSource(config.Reddit);
        var foundContent = new List<SyndicatorContent>();
        var redditCircuitBroken = false;
        var sourceDelayBaseSeconds = config.Reddit?.SourceDelaySeconds ?? 30;

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
                    foundContent.AddRange(await redditSource.Parse(source, syndicatedUrls, syndicatedDescriptions).ConfigureAwait(false));
                }
            }
            catch (RedditBrowserException ex) when (ex.ShouldAbortRun)
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
                var pause = RedditBrowserClient.ComputeJitteredDelay(sourceDelayBaseSeconds);
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
            if (useTelegram)
            {
                telegram = new TelegramBotClient(config.TelegramKey);
            }

            SimpleHttpClient fallbackHttp = null;
            var userAgentInitialized = false;
            var mediaDelayBaseSeconds = config.Reddit?.MediaDelaySeconds ?? 5;
            var processedNewItems = 0;

            try
            {
                foreach (var content in foundContent)
                {
                    var previouslySyndicated =
                        (!string.IsNullOrEmpty(content.LinkURL) && syndicatedUrls.Contains(content.LinkURL)) ||
                        (!string.IsNullOrEmpty(content.Description) && syndicatedDescriptions.Contains(content.Description));

                    if (previouslySyndicated)
                    {
                        continue;
                    }

                    if (processedNewItems > 0)
                    {
                        var mediaDelay = RedditBrowserClient.ComputeJitteredDelay(mediaDelayBaseSeconds);
                        if (mediaDelay > TimeSpan.Zero)
                        {
                            await Task.Delay(mediaDelay).ConfigureAwait(false);
                        }
                    }
                    processedNewItems++;

                    // Write to Console
                    Console.WriteLine($"{content.Description} {content.LinkURL} -- {content.ImageURL}");

                    // Save to config
                    config.SyndicatedURLs.Add(
                        new ConfigSyndicatedUrl
                        {
                            Syndicated = DateTime.Now,
                            URL = content.LinkURL,
                            Description = content.Description
                        });

                    if (!string.IsNullOrEmpty(content.LinkURL))
                    {
                        syndicatedUrls.Add(content.LinkURL);
                    }

                    if (!string.IsNullOrEmpty(content.Description))
                    {
                        syndicatedDescriptions.Add(content.Description);
                    }

                    // Download media via Playwright browser session first, falling back to SimpleHttpClient if needed
                    var imageStream = new MemoryStream();
                    try
                    {
                        var downloaded = false;
                        try
                        {
                            await redditSource.DownloadMediaAsync(content.ImageURL, imageStream).ConfigureAwait(false);
                            downloaded = imageStream.Length > 0;
                        }
                        catch (Exception playwrightDownloadEx)
                        {
                            Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Playwright media download failed ({playwrightDownloadEx.Message}); trying HTTP fallback...");
                            imageStream.SetLength(0);
                        }

                        if (!downloaded)
                        {
                            if (!userAgentInitialized)
                            {
                                await SimpleHttpClient.InitializeUserAgent().ConfigureAwait(false);
                                userAgentInitialized = true;
                            }

                            if (fallbackHttp == null)
                            {
                                #pragma warning disable S4830 // Server certificates should be verified during SSL/TLS connections
                                var handler = new HttpClientHandler
                                {
                                    ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
                                    AutomaticDecompression = System.Net.DecompressionMethods.All
                                };
                                #pragma warning restore S4830 // Server certificates should be verified during SSL/TLS connections
                                fallbackHttp = new SimpleHttpClient(handler);
                            }

                            try
                            {
                                // Reddit has some annoying anti-downloader crap, change the Accept header to bypass it
                                if (content.ImageURL.Contains("redd.it") || content.ImageURL.Contains("reddit"))
                                {
                                    fallbackHttp.DefaultRequestHeaders.Accept.Clear();
                                    fallbackHttp.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
                                }

                                await using var downloadStream = await fallbackHttp.DownloadData(content.ImageURL).ConfigureAwait(false);
                                await downloadStream.CopyToAsync(imageStream).ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Error while downloading - {ex}");
                                continue;
                            }
                        }

                        // Check if image dimensions are too large for Telegram and resize if necessary
                        bool isPhoto = !content.ImageURL.Contains(".gif", StringComparison.InvariantCultureIgnoreCase) &&
                                       !content.ImageURL.Contains(".mp4", StringComparison.InvariantCultureIgnoreCase);
                        if (isPhoto)
                        {
                            imageStream.Seek(0, SeekOrigin.Begin);
                            try
                            {
                                using var image = await Image.LoadAsync(imageStream).ConfigureAwait(false);
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

                                    await imageStream.DisposeAsync().ConfigureAwait(false);
                                    imageStream = resizedStream;
                                }

                                // Check if the stream size still exceeds 10 MB (10,485,760 bytes)
                                if (imageStream.Length > 10485760)
                                {
                                    var format = image.Metadata.DecodedImageFormat;
                                    int quality = 80;
                                    double scale = 1.0;
                                    bool sizeFits = false;

                                    while (!sizeFits && scale >= 0.05)
                                    {
                                        var compressedStream = new MemoryStream();

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
                                            await imageStream.DisposeAsync().ConfigureAwait(false);
                                            imageStream = compressedStream;
                                        }
                                        else
                                        {
                                            await compressedStream.DisposeAsync().ConfigureAwait(false);

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
                    finally
                    {
                        await imageStream.DisposeAsync().ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                fallbackHttp?.Dispose();
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

    private static async Task SaveWithQualityAsync(Image image, Stream stream, IImageFormat format, int quality)
    {
        if (format?.Name.Equals("JPEG", StringComparison.OrdinalIgnoreCase) == true)
        {
            await image.SaveAsync(stream, new JpegEncoder { Quality = quality }).ConfigureAwait(false);
        }
        else if (format?.Name.Equals("WEBP", StringComparison.OrdinalIgnoreCase) == true)
        {
            await image.SaveAsync(stream, new WebpEncoder { Quality = quality }).ConfigureAwait(false);
        }
        else
        {
            // Fallback: save other formats (like high-size PNGs) as JPEG to reduce file size significantly
            await image.SaveAsync(stream, new JpegEncoder { Quality = quality }).ConfigureAwait(false);
        }
    }
}
