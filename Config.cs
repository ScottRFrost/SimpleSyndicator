using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

#pragma warning disable CS0649 // Field 'Config.Sources' is never assigned to, and will always have its default value null
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
    public string Browser { get; set; } = "chromium";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string BrowserChannel { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string ExecutablePath { get; set; }

    public bool Headless { get; set; } = true;

    public string StorageStatePath { get; set; } = "reddit-state.json";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string UserDataDir { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string SessionCookie { get; set; }

    public string Locale { get; set; } = "en-US";

    public int NavigationTimeoutSeconds { get; set; } = 45;

    public int? DefaultLimit { get; set; } = 25;

    public int MaxScrollPasses { get; set; } = 4;

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
#pragma warning restore CS0649 // Field 'Config.Sources' is never assigned to, and will always have its default value null
