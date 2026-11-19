using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SimpleSyndicator;

public enum RedditAuthMode
{
    None,
    OAuth2,
    SessionCookie
}

public sealed class RedditApiException : InvalidOperationException
{
    public HttpStatusCode? StatusCode { get; }
    public bool ShouldAbortRun { get; }

    public RedditApiException(string message, HttpStatusCode? statusCode = null, bool shouldAbortRun = false, Exception innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        ShouldAbortRun = shouldAbortRun;
    }
}

public sealed class RedditAuthClient : IDisposable
{
    private const string AccessTokenUrl = "https://www.reddit.com/api/v1/access_token";
    private const string AuthorizeUrl = "https://www.reddit.com/api/v1/authorize";
    private const string MeUrl = "https://oauth.reddit.com/api/v1/me";
    private const int MaxTransientRetries = 2;
    private const int MaxRetryDelaySeconds = 60;
    private const double LowRateLimitRemainingThreshold = 5.0;

    private readonly RedditConfig _config;
    private readonly HttpClient _oauthClient;
    private readonly HttpClient _cookieClient;
    private readonly Func<TimeSpan, Task> _delayAsync;

    public RedditAuthClient(RedditConfig config)
        : this(config, oauthHandler: null, cookieHandler: null, delayAsync: null)
    {
    }

    internal RedditAuthClient(
        RedditConfig config,
        HttpMessageHandler oauthHandler,
        HttpMessageHandler cookieHandler = null,
        Func<TimeSpan, Task> delayAsync = null)
    {
        _config = config;
        _delayAsync = delayAsync ?? (static d => Task.Delay(d));
        _oauthClient = CreateOAuthClient(oauthHandler, disposeHandler: oauthHandler == null);
        _cookieClient = CreateCookieClient(cookieHandler, disposeHandler: cookieHandler == null);
    }

    public RedditAuthMode AuthMode => ResolveAuthMode(_config);

    public static RedditAuthMode ResolveAuthMode(RedditConfig config)
    {
        if (config == null)
        {
            return RedditAuthMode.None;
        }

        if (!string.IsNullOrWhiteSpace(config.ClientId))
        {
            var hasRefreshToken = !string.IsNullOrWhiteSpace(config.RefreshToken);
            var hasPasswordCredentials =
                !string.IsNullOrWhiteSpace(config.ClientSecret) &&
                !string.IsNullOrWhiteSpace(config.Username) &&
                !string.IsNullOrWhiteSpace(config.Password);
            var hasValidAccessToken =
                !string.IsNullOrWhiteSpace(config.AccessToken) &&
                config.AccessTokenExpiresUtc.HasValue &&
                ToUtc(config.AccessTokenExpiresUtc.Value) > DateTime.UtcNow.AddMinutes(1);

            if (hasRefreshToken || hasPasswordCredentials || hasValidAccessToken)
            {
                return RedditAuthMode.OAuth2;
            }
        }

        if (!string.IsNullOrWhiteSpace(config.SessionCookie))
        {
            return RedditAuthMode.SessionCookie;
        }

        return RedditAuthMode.None;
    }

    public static string GetOAuthUserAgent(RedditConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config?.UserAgent))
        {
            return config.UserAgent.Trim();
        }

        if (!string.IsNullOrWhiteSpace(config?.Username))
        {
            var username = config.Username.Trim().TrimStart('/');
            if (username.StartsWith("u/", StringComparison.OrdinalIgnoreCase))
            {
                username = username[2..];
            }

            if (!string.IsNullOrWhiteSpace(username))
            {
                return $"dotnet:SimpleSyndicator:v1.0 (by /u/{username})";
            }
        }

        return "dotnet:SimpleSyndicator:v1.0";
    }

    public static string NormalizeCookieHeader(string rawCookie)
    {
        if (string.IsNullOrWhiteSpace(rawCookie))
        {
            return string.Empty;
        }

        var trimmed = rawCookie.Trim();
        if (trimmed.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[7..].Trim();
        }

        if (trimmed.Contains('='))
        {
            return trimmed;
        }

        if (trimmed.StartsWith("eyJ", StringComparison.Ordinal))
        {
            var parts = trimmed.Split('.');
            if (parts.Length == 3 && parts.All(p => !string.IsNullOrEmpty(p)))
            {
                return $"token_v2={trimmed}";
            }
        }

        return $"reddit_session={trimmed}";
    }

    public static bool SupportsBeforeCursor(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var path = uri.AbsolutePath;
        if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^5];
        }
        else if (path.EndsWith(".rss", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^4];
        }

        path = path.TrimEnd('/');

        var rawQuery = uri.Query.TrimStart('?');
        var hasSortNew = false;
        if (!string.IsNullOrEmpty(rawQuery))
        {
            foreach (var segment in rawQuery.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eqIndex = segment.IndexOf('=');
                var key = eqIndex >= 0 ? segment[..eqIndex] : segment;
                if (key.Equals("before", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("after", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (key.Equals("sort", StringComparison.OrdinalIgnoreCase) && eqIndex >= 0)
                {
                    var value = WebUtility.UrlDecode(segment[(eqIndex + 1)..]);
                    if (value.Equals("new", StringComparison.OrdinalIgnoreCase))
                    {
                        hasSortNew = true;
                    }
                }
            }
        }

        if (path.EndsWith("/new", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return path.EndsWith("/search", StringComparison.OrdinalIgnoreCase) && hasSortNew;
    }

    public static string NormalizeRedditUrl(string url, bool useOAuth, int? limit = null, string beforeFullname = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        var uri = new Uri(url);
        var path = uri.AbsolutePath;
        var rawQuery = uri.Query.TrimStart('?');

        string host;
        if (useOAuth)
        {
            host = "oauth.reddit.com";
            if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                path = path[..^5];
            }
            else if (path.EndsWith(".rss", StringComparison.OrdinalIgnoreCase))
            {
                path = path[..^4];
            }

            if (string.IsNullOrEmpty(path))
            {
                path = "/";
            }
        }
        else
        {
            host = uri.Host.Equals("old.reddit.com", StringComparison.OrdinalIgnoreCase)
                ? "old.reddit.com"
                : "www.reddit.com";

            if (path.EndsWith(".rss", StringComparison.OrdinalIgnoreCase))
            {
                path = $"{path[..^4]}.json";
            }
            else if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                path = $"{path}.json";
            }
        }

        var hasRawJson = false;
        var hasLimit = false;
        var hasBefore = false;
        var hasAfter = false;
        if (!string.IsNullOrEmpty(rawQuery))
        {
            foreach (var segment in rawQuery.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eqIndex = segment.IndexOf('=');
                var key = eqIndex >= 0 ? segment[..eqIndex] : segment;
                if (key.Equals("raw_json", StringComparison.OrdinalIgnoreCase))
                {
                    hasRawJson = true;
                }
                else if (key.Equals("limit", StringComparison.OrdinalIgnoreCase))
                {
                    hasLimit = true;
                }
                else if (key.Equals("before", StringComparison.OrdinalIgnoreCase))
                {
                    hasBefore = true;
                }
                else if (key.Equals("after", StringComparison.OrdinalIgnoreCase))
                {
                    hasAfter = true;
                }
            }
        }

        var extraParams = new List<string>(3);
        if (!hasLimit && limit.HasValue && limit.Value > 0)
        {
            extraParams.Add($"limit={Math.Clamp(limit.Value, 1, 100)}");
        }

        if (!hasBefore && !hasAfter && !string.IsNullOrWhiteSpace(beforeFullname) && SupportsBeforeCursor(url))
        {
            extraParams.Add($"before={Uri.EscapeDataString(beforeFullname.Trim())}");
        }

        if (!hasRawJson)
        {
            extraParams.Add("raw_json=1");
        }

        string query;
        if (extraParams.Count == 0)
        {
            query = rawQuery;
        }
        else if (string.IsNullOrEmpty(rawQuery))
        {
            query = string.Join("&", extraParams);
        }
        else
        {
            query = $"{rawQuery}&{string.Join("&", extraParams)}";
        }

        return string.IsNullOrEmpty(query)
            ? $"https://{host}{path}"
            : $"https://{host}{path}?{query}";
    }

    public static TimeSpan ComputeJitteredDelay(int baseSeconds)
    {
        if (baseSeconds <= 0)
        {
            return TimeSpan.Zero;
        }

        var baseMs = baseSeconds * 1000.0;
        var factor = 0.80 + (Random.Shared.NextDouble() * 0.50);
        return TimeSpan.FromMilliseconds(Math.Max(500, Math.Round(baseMs * factor)));
    }

    public static string ExtractCodeFromCallbackInput(string input, string expectedState)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            throw new InvalidOperationException("Authorization input was empty.");
        }

        var trimmed = input.Trim();
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            var uri = new Uri(trimmed);
            var queryParams = ParseQueryParameters(uri.Query);
            return ValidateAndExtractCode(queryParams, expectedState, requireState: true);
        }

        if (trimmed.Contains("code=", StringComparison.OrdinalIgnoreCase))
        {
            var withoutFragment = trimmed.Split('#')[0];
            var queryParams = ParseQueryParameters(withoutFragment);
            var hasState = queryParams.ContainsKey("state");
            return ValidateAndExtractCode(queryParams, expectedState, requireState: hasState);
        }

        var rawCode = trimmed.Split('#')[0].Trim();
        if (string.IsNullOrWhiteSpace(rawCode))
        {
            throw new InvalidOperationException("Authorization code was empty.");
        }

        return rawCode;
    }

    public async Task<string> GetAccessTokenAsync(bool forceRefresh = false)
    {
        if (_config == null || string.IsNullOrWhiteSpace(_config.ClientId))
        {
            throw new InvalidOperationException("Reddit OAuth2 requires Reddit.ClientId to be configured in config.json.");
        }

        if (!forceRefresh &&
            !string.IsNullOrWhiteSpace(_config.AccessToken) &&
            _config.AccessTokenExpiresUtc.HasValue &&
            ToUtc(_config.AccessTokenExpiresUtc.Value) > DateTime.UtcNow.AddMinutes(2))
        {
            return _config.AccessToken;
        }

        string grantType;
        Dictionary<string, string> formFields;

        if (!string.IsNullOrWhiteSpace(_config.RefreshToken))
        {
            grantType = "refresh_token";
            formFields = new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = _config.RefreshToken
            };
        }
        else if (!string.IsNullOrWhiteSpace(_config.Username) && !string.IsNullOrWhiteSpace(_config.Password))
        {
            grantType = "password";
            formFields = new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["username"] = _config.Username,
                ["password"] = _config.Password
            };
        }
        else
        {
            throw new InvalidOperationException(
                "No RefreshToken or Username/Password configured for Reddit OAuth2. Run with --reddit-auth or configure Username and Password in config.json.");
        }

        return await RequestAccessTokenAsync(grantType, formFields).ConfigureAwait(false);
    }

    public async Task<string> GetJsonAsync(string url, int? limit = null, string beforeFullname = null)
    {
        var mode = ResolveAuthMode(_config);
        if (mode == RedditAuthMode.None)
        {
            if (!string.IsNullOrWhiteSpace(_config?.ClientId))
            {
                throw new RedditApiException(
                    "Reddit OAuth2 ClientId is configured, but credentials are incomplete. Run 'SimpleSyndicator --reddit-auth' (recommended for 2FA), configure ClientSecret + Username + Password (script app), or set Reddit.SessionCookie in config.json.",
                    statusCode: null,
                    shouldAbortRun: true);
            }

            throw new RedditApiException(
                "Anonymous Reddit access is no longer supported. Configure Reddit OAuth2 credentials (ClientId/ClientSecret + --reddit-auth or Username/Password) or Reddit.SessionCookie in config.json.",
                statusCode: null,
                shouldAbortRun: true);
        }

        if (mode == RedditAuthMode.OAuth2)
        {
            return await GetOAuthJsonAsync(url, limit, beforeFullname).ConfigureAwait(false);
        }

        return await GetCookieJsonAsync(url, limit, beforeFullname).ConfigureAwait(false);
    }

    public async Task RunInteractiveOAuthAsync()
    {
        if (_config == null || string.IsNullOrWhiteSpace(_config.ClientId))
        {
            throw new InvalidOperationException("Reddit.ClientId must be configured in config.json before running --reddit-auth.");
        }

        var redirectUri = string.IsNullOrWhiteSpace(_config.RedirectUri)
            ? "http://localhost:8080/callback"
            : _config.RedirectUri.Trim();
        _config.RedirectUri = redirectUri;

        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var authorizeUrl =
            $"{AuthorizeUrl}" +
            $"?client_id={Uri.EscapeDataString(_config.ClientId)}" +
            $"&response_type=code" +
            $"&state={Uri.EscapeDataString(state)}" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            $"&duration=permanent" +
            $"&scope={Uri.EscapeDataString("read identity")}";

        Console.WriteLine();
        Console.WriteLine("Open the following URL in your browser and authorize SimpleSyndicator:");
        Console.WriteLine();
        Console.WriteLine(authorizeUrl);
        Console.WriteLine();

        var code = await WaitForAuthorizationCodeAsync(redirectUri, state).ConfigureAwait(false);

        await RequestAccessTokenAsync(
            "authorization_code",
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = redirectUri
            }).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(_config.RefreshToken))
        {
            Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Warning: Reddit did not return a refresh_token. Ensure duration=permanent was authorized.");
        }

        await PopulateAuthenticatedUsernameAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        _oauthClient.Dispose();
        _cookieClient.Dispose();
    }

    private async Task<string> GetOAuthJsonAsync(string url, int? limit, string beforeFullname)
    {
        var targetUrl = NormalizeRedditUrl(url, useOAuth: true, limit: limit, beforeFullname: beforeFullname);
        var token = await GetAccessTokenAsync(forceRefresh: false).ConfigureAwait(false);

        var hasRefreshedToken = false;
        var transientAttempts = 0;

        while (true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, targetUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("bearer", token);

            using var response = await _oauthClient.SendAsync(request).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Unauthorized && !hasRefreshedToken)
            {
                hasRefreshedToken = true;
                Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Reddit OAuth2 token rejected (401); refreshing token and retrying...");
                token = await GetAccessTokenAsync(forceRefresh: true).ConfigureAwait(false);
                continue;
            }

            if (IsTransientStatusCode(response.StatusCode) &&
                TryComputeRetryDelay(response, transientAttempts, max429Retries: MaxTransientRetries, out var retryDelay, out _))
            {
                transientAttempts++;
                Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Reddit OAuth2 returned {(int)response.StatusCode}; backing off {retryDelay.TotalSeconds:F1}s before retry {transientAttempts}/{MaxTransientRetries}...");
                await _delayAsync(retryDelay).ConfigureAwait(false);
                continue;
            }

            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new RedditApiException(
                    $"Reddit OAuth2 authentication failed (401 Unauthorized): {Truncate(body, 300)}",
                    HttpStatusCode.Unauthorized,
                    shouldAbortRun: true);
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                var isSubredditScoped = IsSubredditSpecificForbidden(body);
                throw new RedditApiException(
                    $"Reddit OAuth2 request forbidden (403 Forbidden): {Truncate(body, 300)}",
                    HttpStatusCode.Forbidden,
                    shouldAbortRun: !isSubredditScoped);
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var headerSeconds = GetRequestedRetrySeconds(response);
                var retryNote = headerSeconds.HasValue ? $" Retry after {Math.Ceiling(headerSeconds.Value):F0}s." : string.Empty;
                throw new RedditApiException(
                    $"Reddit OAuth2 rate limit exceeded (429 Too Many Requests).{retryNote}",
                    HttpStatusCode.TooManyRequests,
                    shouldAbortRun: true);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new RedditApiException(
                    $"Reddit OAuth2 request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {Truncate(body, 300)}",
                    response.StatusCode,
                    shouldAbortRun: false);
            }

            await PauseIfRateLimitLowAsync(response).ConfigureAwait(false);
            EnsureJsonPayload(body, response.StatusCode, "Reddit OAuth2");
            return body;
        }
    }

    private async Task<string> GetCookieJsonAsync(string url, int? limit, string beforeFullname)
    {
        var targetUrl = NormalizeRedditUrl(url, useOAuth: false, limit: limit, beforeFullname: beforeFullname);
        var cookieHeader = NormalizeCookieHeader(_config.SessionCookie);
        var transientAttempts = 0;

        while (true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, targetUrl);
            request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);

            using var response = await _cookieClient.SendAsync(request).ConfigureAwait(false);

            if (response.StatusCode != HttpStatusCode.Unauthorized &&
                response.StatusCode != HttpStatusCode.Forbidden &&
                IsTransientStatusCode(response.StatusCode) &&
                TryComputeRetryDelay(response, transientAttempts, max429Retries: 1, out var retryDelay, out _))
            {
                transientAttempts++;
                Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Reddit returned {(int)response.StatusCode}; backing off {retryDelay.TotalSeconds:F1}s before retry {transientAttempts}...");
                await _delayAsync(retryDelay).ConfigureAwait(false);
                continue;
            }

            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
            {
                throw new RedditApiException(
                    $"Reddit rejected the cookie-authenticated request ({(int)response.StatusCode} {response.ReasonPhrase}). Your Reddit.SessionCookie is likely expired or missing token_v2/reddit_session; refresh it from a logged-in browser.",
                    response.StatusCode,
                    shouldAbortRun: true);
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var headerSeconds = GetRequestedRetrySeconds(response);
                var retryNote = headerSeconds.HasValue ? $" Retry after {Math.Ceiling(headerSeconds.Value):F0}s." : string.Empty;
                throw new RedditApiException(
                    $"Reddit rate limit exceeded (429 Too Many Requests).{retryNote}",
                    HttpStatusCode.TooManyRequests,
                    shouldAbortRun: true);
            }

            var contentType = response.Content.Headers.ContentType?.MediaType;
            var finalUri = response.RequestMessage?.RequestUri?.ToString() ?? string.Empty;
            var trimmed = body.AsSpan().TrimStart();
            if ((!string.IsNullOrEmpty(contentType) && contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase)) ||
                (!trimmed.IsEmpty && trimmed[0] == '<') ||
                finalUri.Contains("/login", StringComparison.OrdinalIgnoreCase))
            {
                throw new RedditApiException(
                    "Reddit returned an HTML/login page instead of JSON. Refresh Reddit.SessionCookie from a logged-in browser.",
                    response.StatusCode,
                    shouldAbortRun: true);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new RedditApiException(
                    $"Reddit cookie request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {Truncate(body, 300)}",
                    response.StatusCode,
                    shouldAbortRun: false);
            }

            EnsureJsonPayload(body, response.StatusCode, "Reddit cookie");
            return body;
        }
    }

    private async Task<string> RequestAccessTokenAsync(string grantType, Dictionary<string, string> formFields)
    {
        var transientAttempts = 0;

        while (true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, AccessTokenUrl);
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_config.ClientId}:{_config.ClientSecret ?? string.Empty}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
            request.Content = new FormUrlEncodedContent(formFields);

            using var response = await _oauthClient.SendAsync(request).ConfigureAwait(false);

            if (IsTransientStatusCode(response.StatusCode) &&
                TryComputeRetryDelay(response, transientAttempts, max429Retries: MaxTransientRetries, out var retryDelay, out _))
            {
                transientAttempts++;
                Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Reddit OAuth2 token endpoint returned {(int)response.StatusCode}; backing off {retryDelay.TotalSeconds:F1}s before retry {transientAttempts}/{MaxTransientRetries}...");
                await _delayAsync(retryDelay).ConfigureAwait(false);
                continue;
            }

            var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            OAuth2.OAuth2Token token = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(responseBody))
                {
                    token = JsonSerializer.Deserialize<OAuth2.OAuth2Token>(responseBody);
                }
            }
            catch (JsonException)
            {
                // Handled below via error checks using safe response snippet
            }

            var hasErrorField = token?.error.HasValue == true &&
                token.error.Value.ValueKind != JsonValueKind.Null &&
                token.error.Value.ValueKind != JsonValueKind.Undefined;

            if (!response.IsSuccessStatusCode || hasErrorField || string.IsNullOrWhiteSpace(token?.access_token))
            {
                var details = BuildTokenErrorDetails(token, responseBody);
                throw new RedditApiException(
                    $"Reddit OAuth2 token request ({grantType}) failed ({(int)response.StatusCode} {response.ReasonPhrase}): {details}",
                    response.StatusCode,
                    shouldAbortRun: true);
            }

            var expiresInSeconds = token.expires_in > 0 ? token.expires_in : 3600;
            _config.AccessToken = token.access_token;
            _config.AccessTokenExpiresUtc = DateTime.UtcNow.AddSeconds(expiresInSeconds);
            if (!string.IsNullOrWhiteSpace(token.refresh_token))
            {
                _config.RefreshToken = token.refresh_token;
            }

            Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Acquired Reddit OAuth2 token via {grantType} (expires {_config.AccessTokenExpiresUtc:O}).");
            return _config.AccessToken;
        }
    }

    private async Task PopulateAuthenticatedUsernameAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, MeUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("bearer", _config.AccessToken);

            using var response = await _oauthClient.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return;
            }

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("name", out var nameProp))
            {
                var username = nameProp.GetString();
                if (!string.IsNullOrWhiteSpace(username))
                {
                    if (string.IsNullOrWhiteSpace(_config.Username))
                    {
                        _config.Username = username;
                    }

                    Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Authenticated with Reddit as /u/{username}.");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Note: Could not verify /api/v1/me profile ({ex.Message}). Token was still saved.");
        }
    }

    private static async Task<string> WaitForAuthorizationCodeAsync(string redirectUri, string expectedState)
    {
        HttpListener listener = null;
        Task<string> listenerTask = null;

        if (Uri.TryCreate(redirectUri, UriKind.Absolute, out var parsedRedirect) &&
            parsedRedirect.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) &&
            (parsedRedirect.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
             parsedRedirect.Host.Equals("127.0.0.1", StringComparison.Ordinal)))
        {
            try
            {
                listener = new HttpListener();
                var prefix = $"http://{parsedRedirect.Host}:{parsedRedirect.Port}/";
                listener.Prefixes.Add(prefix);
                listener.Start();
                listenerTask = ListenForCallbackAsync(listener, parsedRedirect.AbsolutePath, expectedState);
                Console.WriteLine($"Listening on {redirectUri} for the OAuth callback...");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Could not start local callback listener ({ex.Message}).");
                listener?.Close();
                listener = null;
                listenerTask = null;
            }
        }

        Console.WriteLine("After approving access, paste the full redirected URL (or the authorization code) and press Enter:");

        try
        {
            while (true)
            {
                var stdinTask = Task.Run(() => Console.ReadLine());
                if (listenerTask == null)
                {
                    var line = await stdinTask.ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        throw new InvalidOperationException("No authorization code or callback URL was provided.");
                    }

                    return ExtractCodeFromCallbackInput(line, expectedState);
                }

                var completed = await Task.WhenAny(listenerTask, stdinTask).ConfigureAwait(false);
                if (completed == listenerTask)
                {
                    return await listenerTask.ConfigureAwait(false);
                }

                var input = await stdinTask.ConfigureAwait(false);
                if (input == null)
                {
                    return await listenerTask.ConfigureAwait(false);
                }

                if (!string.IsNullOrWhiteSpace(input))
                {
                    return ExtractCodeFromCallbackInput(input, expectedState);
                }
            }
        }
        finally
        {
            if (listener != null)
            {
                try
                {
                    listener.Stop();
                    listener.Close();
                }
                catch
                {
                    // Ignore cleanup errors on shutdown
                }
            }
        }
    }

    private static async Task<string> ListenForCallbackAsync(HttpListener listener, string expectedPath, string expectedState)
    {
        var normalizedExpectedPath = expectedPath.TrimEnd('/');
        while (listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (HttpListenerException)
            {
                throw new OperationCanceledException("OAuth callback listener stopped.");
            }
            catch (ObjectDisposedException)
            {
                throw new OperationCanceledException("OAuth callback listener disposed.");
            }

            var requestPath = (context.Request.Url?.AbsolutePath ?? string.Empty).TrimEnd('/');
            if (!string.Equals(requestPath, normalizedExpectedPath, StringComparison.OrdinalIgnoreCase))
            {
                await WriteHtmlResponseAsync(
                    context.Response,
                    HttpStatusCode.NotFound,
                    "<html><body><p>Not found. Waiting for OAuth callback.</p></body></html>").ConfigureAwait(false);
                continue;
            }

            try
            {
                var code = ExtractCodeFromCallbackInput(context.Request.Url!.AbsoluteUri, expectedState);
                await WriteHtmlResponseAsync(
                    context.Response,
                    HttpStatusCode.OK,
                    "<html><body><h2>SimpleSyndicator Reddit authorization complete</h2><p>You can close this browser window and return to the terminal.</p></body></html>").ConfigureAwait(false);
                return code;
            }
            catch (Exception ex)
            {
                await WriteHtmlResponseAsync(
                    context.Response,
                    HttpStatusCode.BadRequest,
                    $"<html><body><h2>Authorization failed</h2><p>{WebUtility.HtmlEncode(ex.Message)}</p></body></html>").ConfigureAwait(false);
                throw;
            }
        }

        throw new OperationCanceledException("OAuth callback listener stopped before receiving authorization code.");
    }

    private static async Task WriteHtmlResponseAsync(HttpListenerResponse response, HttpStatusCode statusCode, string html)
    {
        var bytes = Encoding.UTF8.GetBytes(html);
        response.StatusCode = (int)statusCode;
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        response.Close();
    }

    private async Task PauseIfRateLimitLowAsync(HttpResponseMessage response)
    {
        var remainingText = GetHeaderValue(response, "X-Ratelimit-Remaining");
        var resetText = GetHeaderValue(response, "X-Ratelimit-Reset");

        if (double.TryParse(remainingText, NumberStyles.Float, CultureInfo.InvariantCulture, out var remaining) &&
            remaining < LowRateLimitRemainingThreshold &&
            double.TryParse(resetText, NumberStyles.Float, CultureInfo.InvariantCulture, out var resetSeconds) &&
            resetSeconds > 0 &&
            resetSeconds <= 120)
        {
            var waitSeconds = Math.Min(MaxRetryDelaySeconds, (int)Math.Ceiling(resetSeconds) + 1);
            Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Reddit API rate limit low ({remaining:F0} remaining); pausing {waitSeconds}s...");
            await _delayAsync(TimeSpan.FromSeconds(waitSeconds)).ConfigureAwait(false);
        }
    }

    private static bool IsTransientStatusCode(HttpStatusCode statusCode)
    {
        return statusCode == HttpStatusCode.TooManyRequests ||
               statusCode == HttpStatusCode.InternalServerError ||
               statusCode == HttpStatusCode.BadGateway ||
               statusCode == HttpStatusCode.ServiceUnavailable ||
               statusCode == HttpStatusCode.GatewayTimeout;
    }

    private static double? GetRequestedRetrySeconds(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter != null)
        {
            if (retryAfter.Delta.HasValue)
            {
                return Math.Max(0, retryAfter.Delta.Value.TotalSeconds);
            }

            if (retryAfter.Date.HasValue)
            {
                return Math.Max(0, (retryAfter.Date.Value - DateTimeOffset.UtcNow).TotalSeconds);
            }
        }

        var resetText = GetHeaderValue(response, "X-Ratelimit-Reset");
        if (double.TryParse(resetText, NumberStyles.Float, CultureInfo.InvariantCulture, out var resetSeconds) && resetSeconds >= 0)
        {
            return resetSeconds;
        }

        return null;
    }

    private static bool TryComputeRetryDelay(
        HttpResponseMessage response,
        int transientAttemptIndex,
        int max429Retries,
        out TimeSpan delay,
        out double? requestedHeaderSeconds)
    {
        delay = TimeSpan.Zero;
        requestedHeaderSeconds = GetRequestedRetrySeconds(response);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            if (requestedHeaderSeconds.HasValue && requestedHeaderSeconds.Value > MaxRetryDelaySeconds)
            {
                return false;
            }

            if (transientAttemptIndex >= max429Retries)
            {
                return false;
            }

            if (requestedHeaderSeconds.HasValue)
            {
                delay = TimeSpan.FromSeconds(Math.Ceiling(requestedHeaderSeconds.Value) + 1);
            }
            else
            {
                var backoffSeconds = Math.Pow(2, transientAttemptIndex + 1);
                var jitterMs = Random.Shared.Next(0, 501);
                delay = TimeSpan.FromSeconds(backoffSeconds) + TimeSpan.FromMilliseconds(jitterMs);
            }

            return true;
        }

        if (transientAttemptIndex >= MaxTransientRetries)
        {
            return false;
        }

        if (requestedHeaderSeconds.HasValue && requestedHeaderSeconds.Value <= MaxRetryDelaySeconds)
        {
            delay = TimeSpan.FromSeconds(Math.Ceiling(requestedHeaderSeconds.Value) + 1);
        }
        else
        {
            var backoffSeconds = Math.Pow(2, transientAttemptIndex + 1);
            var jitterMs = Random.Shared.Next(0, 501);
            delay = TimeSpan.FromSeconds(backoffSeconds) + TimeSpan.FromMilliseconds(jitterMs);
        }

        return true;
    }

    private static bool IsSubredditSpecificForbidden(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("reason", out var reasonProp) &&
                reasonProp.ValueKind == JsonValueKind.String)
            {
                var reason = reasonProp.GetString();
                return string.Equals(reason, "private", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(reason, "quarantined", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(reason, "banned", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(reason, "gated", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch (JsonException)
        {
            // Treat unparseable 403 bodies as non-subreddit-specific errors
        }

        return false;
    }

    private HttpClient CreateOAuthClient(HttpMessageHandler handler, bool disposeHandler)
    {
        if (ResolveAuthMode(_config) == RedditAuthMode.OAuth2 &&
            string.IsNullOrWhiteSpace(_config?.UserAgent) &&
            string.IsNullOrWhiteSpace(_config?.Username))
        {
            Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Warning: Reddit.Username and Reddit.UserAgent are both unset; Reddit OAuth2 API rules recommend setting one so requests identify '/u/<username>'.");
        }

        var resolvedHandler = handler ?? new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All
        };

        var client = new HttpClient(resolvedHandler, disposeHandler)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", GetOAuthUserAgent(_config));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    private static HttpClient CreateCookieClient(HttpMessageHandler handler, bool disposeHandler)
    {
        var resolvedHandler = handler ?? new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false
        };

        var client = new HttpClient(resolvedHandler, disposeHandler)
        {
            Timeout = TimeSpan.FromSeconds(30),
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };

        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", SimpleHttpClient.DefaultUserAgent);
        client.DefaultRequestHeaders.TryAddWithoutValidation("sec-ch-ua", SimpleHttpClient.DefaultSecChUa);
        client.DefaultRequestHeaders.TryAddWithoutValidation("sec-ch-ua-mobile", "?0");
        client.DefaultRequestHeaders.TryAddWithoutValidation("sec-ch-ua-platform", "\"Windows\"");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        return client;
    }

    private static Dictionary<string, string> ParseQueryParameters(string rawQuery)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var query = rawQuery.TrimStart('?');
        if (string.IsNullOrEmpty(query))
        {
            return result;
        }

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eqIdx = pair.IndexOf('=');
            if (eqIdx < 0)
            {
                var key = WebUtility.UrlDecode(pair);
                result[key] = string.Empty;
            }
            else
            {
                var key = WebUtility.UrlDecode(pair[..eqIdx]);
                var value = WebUtility.UrlDecode(pair[(eqIdx + 1)..]);
                result[key] = value;
            }
        }

        return result;
    }

    private static string ValidateAndExtractCode(Dictionary<string, string> queryParams, string expectedState, bool requireState)
    {
        if (queryParams.TryGetValue("error", out var error) && !string.IsNullOrWhiteSpace(error))
        {
            throw new InvalidOperationException($"Reddit authorization returned error: {error}");
        }

        if (requireState)
        {
            if (!queryParams.TryGetValue("state", out var state) || !string.Equals(state, expectedState, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("OAuth state mismatch in callback URL.");
            }
        }

        if (!queryParams.TryGetValue("code", out var rawCode))
        {
            throw new InvalidOperationException("Callback URL did not contain an authorization code.");
        }

        var code = rawCode.Split('#')[0].Trim();
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new InvalidOperationException("Callback URL contained an empty authorization code.");
        }

        return code;
    }

    private static void EnsureJsonPayload(string body, HttpStatusCode statusCode, string sourceLabel)
    {
        var trimmed = body.AsSpan().TrimStart();
        if (trimmed.IsEmpty || (trimmed[0] != '{' && trimmed[0] != '['))
        {
            throw new RedditApiException(
                $"{sourceLabel} returned non-JSON response ({(int)statusCode}): {Truncate(body, 300)}",
                statusCode,
                shouldAbortRun: false);
        }
    }

    private static string BuildTokenErrorDetails(OAuth2.OAuth2Token token, string responseBody)
    {
        if (token != null)
        {
            var parts = new List<string>();
            if (token.error.HasValue &&
                token.error.Value.ValueKind != JsonValueKind.Null &&
                token.error.Value.ValueKind != JsonValueKind.Undefined)
            {
                parts.Add($"error={token.error.Value}");
            }

            if (!string.IsNullOrWhiteSpace(token.error_description))
            {
                parts.Add(token.error_description);
            }

            if (!string.IsNullOrWhiteSpace(token.message))
            {
                parts.Add(token.message);
            }

            if (parts.Count > 0)
            {
                return string.Join(" - ", parts);
            }
        }

        return Truncate(responseBody, 300);
    }

    private static string GetHeaderValue(HttpResponseMessage response, string headerName)
    {
        return response.Headers.TryGetValues(headerName, out var values)
            ? values.FirstOrDefault()
            : null;
    }

    private static DateTime ToUtc(DateTime value)
    {
        return value.Kind == DateTimeKind.Local
            ? value.ToUniversalTime()
            : DateTime.SpecifyKind(value, DateTimeKind.Utc);
    }

    private static string Truncate(string text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "<empty response>";
        }

        var singleLine = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return singleLine.Length <= maxLength ? singleLine : $"{singleLine[..maxLength]}...";
    }
}
