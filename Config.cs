using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SimpleSyndicator;

public record Config
{
    public List<ConfigSource> Sources { get; set; }
    public RedditConfig Reddit { get; set; }
    public string TelegramKey { get; set; }
    public long[] TelegramChannelIDs { get; set; }
    public string[] IFTTTWebHooks { get; set; }
    public string TwitterConsumerKey { get; set; }
    public string TwitterConsumerSecret { get; set; }
    public string TwitterAccessToken { get; set; }
    public string TwitterTokenSecret { get; set; }
    public List<ConfigSyndicatedUrl> SyndicatedURLs { get; set; }
}

public record RedditConfig
{
    public string Browser
    {
        get;
        set => field = string.IsNullOrWhiteSpace(value) ? "firefox" : value.Trim();
    } = "firefox";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string BrowserChannel { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string ExecutablePath { get; set; }

    public bool Headless { get; set; } = false;

    public string StorageStatePath
    {
        get;
        set => field = string.IsNullOrWhiteSpace(value) ? "reddit-state.json" : value.Trim();
    } = "reddit-state.json";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string UserDataDir { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string SessionCookie { get; set; }

    public string Locale
    {
        get;
        set => field = string.IsNullOrWhiteSpace(value) ? "en-US" : value.Trim();
    } = "en-US";

    public int NavigationTimeoutSeconds
    {
        get;
        set => field = value > 0 ? Math.Clamp(value, 5, 300) : 45;
    } = 45;

    public int? DefaultLimit { get; set; } = 25;

    public int MaxScrollPasses
    {
        get;
        set => field = Math.Max(0, value);
    } = 4;

    public int MaxPostAgeHours
    {
        get;
        set => field = value > 0 ? value : 24;
    } = 24;

    public bool ExpandPostDetails { get; set; } = true;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SourceDelaySeconds { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MediaDelaySeconds { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[] ExtraLaunchArgs { get; set; }
}

public record ConfigSource
{
    public string Type { get; set; }
    public string URL { get; set; }
    public string Tags { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Limit { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string LastSeenFullname { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? LastSeenUtc { get; set; }
}

public record ConfigSyndicatedUrl
{
    public DateTime Syndicated { get; set; }
    public string URL { get; set; }
    public string Description { get; set; }
}
