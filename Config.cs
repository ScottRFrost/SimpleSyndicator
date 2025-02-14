using System;
using System.Collections.Generic;

#pragma warning disable CS0649 // Field 'Config.Sources' is never assigned to, and will always have its default value null
namespace SimpleSyndicator;

public record Config
{
    public List<ConfigSource> Sources { get; set; }
    public string TelegramKey { get; set; }
    public long[] TelegramChannelIDs { get; set; }
    public string[] IFTTTWebHooks { get; set; }
    public string TwitterConsumerKey { get; set; }
    public string TwitterConsumerSecret { get; set; }
    public string TwitterAccessToken { get; set; }
    public string TwitterTokenSecret { get; set; }
    public List<ConfigSyndicatedUrl> SyndicatedURLs { get; set; }
}

public record ConfigSource
{
    public string Type { get; set; }
    public string URL { get; set; }
    public string Tags { get; set; }
}

public record ConfigSyndicatedUrl
{
    public DateTime Syndicated { get; set; }
    public string URL { get; set; }
    public string Description { get; set; }
}
#pragma warning restore CS0649 // Field 'Config.Sources' is never assigned to, and will always have its default value null
