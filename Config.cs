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
    public string ClientId { get; set; }
    public string ClientSecret { get; set; }
    public string RedirectUri { get; set; } = "http://localhost:8080/callback";
    public string Username { get; set; }
    public string Password { get; set; }
    public string UserAgent { get; set; }
    public string RefreshToken { get; set; }
    public string AccessToken { get; set; }
    public DateTime? AccessTokenExpiresUtc { get; set; }
    public string SessionCookie { get; set; }
    public int? DefaultLimit { get; set; } = 25;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SourceDelaySeconds { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MediaDelaySeconds { get; set; }
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
