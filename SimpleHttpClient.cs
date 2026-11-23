using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Text.Json;
using System.Linq;

namespace SimpleSyndicator;

#pragma warning disable S1075 // Don't use hardcoded URIs
public class SimpleHttpClient : HttpClient
{
    public static string DefaultUserAgent { get; set; } = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36";
    public static string DefaultSecChUa { get; set; } = "\"Not(A:Brand\";v=\"99\", \"Google Chrome\";v=\"133\", \"Chromium\";v=\"133\"";

    public SimpleHttpClient(HttpClientHandler handler) : base(handler)
    {
        Timeout = new TimeSpan(0, 0, 30);
        ReferrerUri = "https://duckduckgo.com";
        AuthorizationHeader = string.Empty;

        // Use HTTP/2.0 to look like a modern browser
        DefaultRequestVersion = System.Net.HttpVersion.Version20;
        DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;

        DefaultRequestHeaders.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.7");
        DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        DefaultRequestHeaders.TryAddWithoutValidation("Connection", "keep-alive");
        DefaultRequestHeaders.TryAddWithoutValidation("DNT", "1");
        DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Dest", "document");
        DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
        DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Site", "none");
        DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-User", "?1");
        DefaultRequestHeaders.TryAddWithoutValidation("Sec-GPC", "1");
        DefaultRequestHeaders.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");
        DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", DefaultUserAgent);

        // Add Chrome client hints
        DefaultRequestHeaders.TryAddWithoutValidation("sec-ch-ua", DefaultSecChUa);
        DefaultRequestHeaders.TryAddWithoutValidation("sec-ch-ua-mobile", "?0");
        DefaultRequestHeaders.TryAddWithoutValidation("sec-ch-ua-platform", "\"Windows\"");

        CustomHeaders = [];
    }

    public string AuthorizationHeader
    {
        get;
        set => field = value ?? string.Empty;
    } = string.Empty;

    public string BingHeader
    {
        get;
        set => field = value ?? string.Empty;
    } = string.Empty;

    public string ReferrerUri
    {
        get;
        set => field = value ?? string.Empty;
    } = "https://duckduckgo.com";

    public Dictionary<string, string> CustomHeaders { get; set; }

    public void AddHeader(string name, string value)
    {
        CustomHeaders.Add(name, value);
        DefaultRequestHeaders.TryAddWithoutValidation(name, value);
    }

    public async Task<string> DownloadStringAsync(string uri)
    {
        BuildHeaders();
        try
        {
            var response = await GetStringAsync(uri).ConfigureAwait(false);
            return response;
        }
        catch (System.Net.Http.HttpRequestException ex)
        {
            Console.Write($"{ex}");
            return ex.Message;
        }
        finally
        {
            CleanHeaders();
        }
    }

    public async Task<Stream> DownloadData(string uri)
    {
        BuildHeaders();
        try
        {
            var response = await GetStreamAsync(uri);
            return response;
        }
        catch (System.Net.Http.HttpRequestException ex)
        {
            Console.Write($"{ex}");
            throw;
        }
        finally
        {
            CleanHeaders();
        }
    }

    void BuildHeaders()
    {
        if (ReferrerUri != string.Empty)
        {
            DefaultRequestHeaders.Referrer = new Uri(ReferrerUri);
        }
        if (AuthorizationHeader != string.Empty)
        {
            DefaultRequestHeaders.TryAddWithoutValidation("Authorization", AuthorizationHeader);
        }
        if (BingHeader != string.Empty)
        {
            DefaultRequestHeaders.TryAddWithoutValidation("Ocp-Apim-Subscription-Key", BingHeader);
        }
        foreach (var header in CustomHeaders)
        {
            DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
            Console.WriteLine($"{header.Key}:{header.Value}");
        }
    }

    void CleanHeaders()
    {
        ReferrerUri = "https://duckduckgo.com";
        AuthorizationHeader = string.Empty;
        DefaultRequestHeaders.Remove("Authorization");
        foreach (var header in CustomHeaders)
        {
            DefaultRequestHeaders.Remove(header.Key);
        }
        CustomHeaders.Clear();
    }

    public static async Task InitializeUserAgent()
    {
        try
        {
            using var handler = new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All };
            using var client = new HttpClient(handler);
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36");

			// Fetch Chrome User-Agent string from the latest-user-agent output.json
			var json = await client.GetStringAsync("https://raw.githubusercontent.com/fa0311/latest-user-agent/main/output.json").ConfigureAwait(false);
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (dict != null && dict.TryGetValue("chrome", out var rawUa) && !string.IsNullOrWhiteSpace(rawUa))
            {
                var chromeIdx = rawUa.IndexOf("Chrome/");
                if (chromeIdx >= 0)
                {
                    var versionPart = rawUa[(chromeIdx + 7)..];
                    var spaceIdx = versionPart.IndexOf(' ');
                    var version = spaceIdx > 0 ? versionPart[..spaceIdx] : versionPart;
                    DefaultUserAgent = $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{version} Safari/537.36";
                    var dotIdx = version.IndexOf('.');
                    if (dotIdx > 0)
                    {
                        var majorVersion = version[..dotIdx];
                        DefaultSecChUa = $"\"Not(A:Brand\";v=\"99\", \"Google Chrome\";v=\"{majorVersion}\", \"Chromium\";v=\"{majorVersion}\"";
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Failed to fetch latest User-Agent, using default. Error: {ex.Message}");
        }
    }
}
#pragma warning restore S1075