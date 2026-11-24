using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;
using static SimpleSyndicator.BaseSource;

namespace SimpleSyndicator;

static class SimpleSyndicator
{
    private static readonly JsonSerializerOptions _jsonOptions = Config.JsonOptions;

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

    public static async Task RunInteractiveBrowserAsync(string initialUrl = null)
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
        await browserClient.RunInteractiveBrowserAsync(initialUrl).ConfigureAwait(false);
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

        HashSet<string> syndicatedUrls = [with(StringComparer.OrdinalIgnoreCase), .. config.SyndicatedURLs.Select(i => i.URL).Where(u => !string.IsNullOrWhiteSpace(u))];
        HashSet<string> syndicatedDescriptions = [with(StringComparer.OrdinalIgnoreCase), .. config.SyndicatedURLs.Select(i => i.Description).Where(d => !string.IsNullOrWhiteSpace(d))];
        HashSet<string> syndicatedImageUrls = [with(StringComparer.OrdinalIgnoreCase)];

        void TrackSyndicatedImage(string candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return;
            }

            var trimmed = candidate.Trim();
            syndicatedImageUrls.Add(trimmed);

            var highRes = RedditSource.GetHighResImageUrl(trimmed);
            if (!string.IsNullOrWhiteSpace(highRes))
            {
                syndicatedImageUrls.Add(highRes);
            }

            var fileName = RedditSource.ExtractMediaFileName(trimmed);
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                syndicatedImageUrls.Add(fileName);
            }
        }

        foreach (var entry in config.SyndicatedURLs)
        {
            TrackSyndicatedImage(entry.ImageURL);
            if (RedditSource.IsMediaUrl(entry.URL))
            {
                TrackSyndicatedImage(entry.URL);
            }
        }

        async Task SaveConfigAsync()
        {
            await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(config, _jsonOptions), System.Text.Encoding.UTF8).ConfigureAwait(false);
        }

        // Telegram Setup
        var useTelegram = !string.IsNullOrWhiteSpace(config.TelegramKey);
        var telegram = useTelegram ? new TelegramBotClient(config.TelegramKey) : null;

        // Parse Reddit Sources via Playwright (single persistent browser/tab, interactive post-by-post flow)
        await using var redditSource = new RedditSource(config.Reddit);
        var foundContent = new List<SyndicatorContent>();
        var redditCircuitBroken = false;
        var sourceDelayBaseSeconds = config.Reddit?.SourceDelaySeconds ?? 30;
        SimpleHttpClient fallbackHttp = null;
        var userAgentInitialized = false;
        var processedNewItems = 0;
        DateTimeOffset? lastDownloadAt = null;

        async Task SyndicateItemAsync(SyndicatorContent content)
        {
            var highResImageUrl = RedditSource.GetHighResImageUrl(content.ImageURL);
            var imageFileName = RedditSource.ExtractMediaFileName(content.ImageURL);
            var previouslySyndicated =
                (!string.IsNullOrEmpty(content.LinkURL) && syndicatedUrls.Contains(content.LinkURL)) ||
                (!string.IsNullOrEmpty(content.Description) && syndicatedDescriptions.Contains(content.Description)) ||
                (!string.IsNullOrEmpty(content.ImageURL) && syndicatedImageUrls.Contains(content.ImageURL)) ||
                (!string.IsNullOrEmpty(highResImageUrl) && syndicatedImageUrls.Contains(highResImageUrl)) ||
                (!string.IsNullOrEmpty(imageFileName) && syndicatedImageUrls.Contains(imageFileName));

            if (previouslySyndicated)
            {
                return;
            }

            if (lastDownloadAt.HasValue)
            {
                var downloadDelay = RedditBrowserClient.ComputeRandomDelay(2, 5);
                var elapsed = DateTimeOffset.UtcNow - lastDownloadAt.Value;
                var remainingDelay = downloadDelay - elapsed;
                if (remainingDelay > TimeSpan.Zero)
                {
                    Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Pausing {remainingDelay.TotalSeconds:F1}s before next download...");
                    await Task.Delay(remainingDelay).ConfigureAwait(false);
                }
            }
            processedNewItems++;

            // Write to Console
            Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Downloading media #{processedNewItems}: {content.Description} | {content.LinkURL} -- {content.ImageURL}");

            // Download media via Playwright browser session first, falling back to SimpleHttpClient if needed
            var imageStream = new MemoryStream();
            try
            {
                var downloaded = false;
                try
                {
                    await redditSource.DownloadMediaAsync(
                        content.ImageURL,
                        imageStream,
                        content.ReferrerURL,
                        content.FallbackImageURL).ConfigureAwait(false);
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

                    var referrerUri = !string.IsNullOrWhiteSpace(content.ReferrerURL)
                        ? content.ReferrerURL
                        : (!string.IsNullOrWhiteSpace(content.LinkURL) ? content.LinkURL : "https://www.reddit.com/");

                    var httpCandidates = RedditBrowserClient.GetDownloadCandidateUrls(content.ImageURL, content.FallbackImageURL);
                    Exception lastHttpEx = null;
                    for (int candidateIdx = 0; candidateIdx < httpCandidates.Count; candidateIdx++)
                    {
                        var candidateUrl = httpCandidates[candidateIdx];
                        try
                        {
                            fallbackHttp.ReferrerUri = referrerUri;
                            if (candidateUrl.Contains("redd.it") || candidateUrl.Contains("reddit"))
                            {
                                fallbackHttp.DefaultRequestHeaders.Accept.Clear();
                                fallbackHttp.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("image/avif"));
                                fallbackHttp.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("image/webp"));
                                fallbackHttp.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("image/apng"));
                                fallbackHttp.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*", 0.8));
                            }

                            await using var downloadStream = await fallbackHttp.DownloadData(candidateUrl).ConfigureAwait(false);
                            await downloadStream.CopyToAsync(imageStream).ConfigureAwait(false);
                            downloaded = imageStream.Length > 0;
                            if (downloaded)
                            {
                                break;
                            }
                        }
                        catch (Exception ex)
                        {
                            lastHttpEx = ex;
                            imageStream.SetLength(0);
                            if (candidateIdx + 1 < httpCandidates.Count)
                            {
                                Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} HTTP fallback failed for '{candidateUrl}' ({ex.Message}); trying next URL...");
                            }
                        }
                    }

                    if (!downloaded)
                    {
                        Console.WriteLine($"Error while downloading - {lastHttpEx}");
                        lastDownloadAt = DateTimeOffset.UtcNow;
                        return;
                    }
                }

                lastDownloadAt = DateTimeOffset.UtcNow;

                // Record and immediately persist downloaded item so subsequent runs skip clicking it
                config.SyndicatedURLs.Add(
                    new ConfigSyndicatedUrl
                    {
                        Syndicated = DateTime.Now,
                        URL = content.LinkURL,
                        Description = content.Description,
                        ImageURL = !string.IsNullOrEmpty(highResImageUrl) ? highResImageUrl : content.ImageURL
                    });

                if (!string.IsNullOrEmpty(content.LinkURL))
                {
                    syndicatedUrls.Add(content.LinkURL);
                }

                if (!string.IsNullOrEmpty(content.Description))
                {
                    syndicatedDescriptions.Add(content.Description);
                }

                TrackSyndicatedImage(content.ImageURL);
                await SaveConfigAsync().ConfigureAwait(false);

                // Check if image dimensions are too large for Telegram and resize if necessary
                if (content.IsPhoto)
                {
                    imageStream.Seek(0, SeekOrigin.Begin);
                    try
                    {
                        imageStream = ImageProcessor.ProcessPhotoForTelegram(imageStream);
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
                            var file = Telegram.Bot.Types.InputFile.FromStream(imageStream, content.FileName);
                            if (content.IsAnimation)
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

        try
        {
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
                        foundContent.AddRange(await redditSource.Parse(
                            source,
                            syndicatedUrls,
                            syndicatedDescriptions,
                            SyndicateItemAsync,
                            syndicatedImageUrls).ConfigureAwait(false));
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

                var hasMoreRunnableSources = config.Sources
                    .Skip(i + 1)
                    .Any(next => !redditCircuitBroken || !string.Equals(next.Type, "reddit", StringComparison.OrdinalIgnoreCase));

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

            // Syndicate any remaining items that were returned without live callback streaming
            Console.WriteLine();
            if (foundContent.Count > 0)
            {
                foreach (var content in foundContent)
                {
                    await SyndicateItemAsync(content).ConfigureAwait(false);
                }
            }
            else if (processedNewItems == 0)
            {
                Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} -=[ NO Results ]=-");
            }
        }
        finally
        {
            fallbackHttp?.Dispose();
        }

        // Clean up entries SyndicatedURLs that are more than 30 days
        config.SyndicatedURLs.RemoveAll(s => s.Syndicated < DateTime.Today.AddDays(-30));

        // Save Config
        await SaveConfigAsync().ConfigureAwait(false);
        Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Wrote config.  {config.Sources.Count} sources, {startingCount} >> {config.SyndicatedURLs.Count} syndicated images.");

        return true;
    }
}
