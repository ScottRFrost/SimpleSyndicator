using System;
using System.Collections.Generic;

namespace SimpleSyndicator
{
    class Config
    {
        // Sources
        public List<Source> Sources; // RSS URLs for Reddit

        // Telegram
        public string TelegramKey;
        public List<long> TelegramChannelIDs;

        ////public string DiscordToken;
        ////public List<long> DiscordChannelIDs;

        public List<string> IFTTTWebHooks; // https://maker.ifttt.com/trigger/{Trigger}/with/key/{YourKeyHere}, Text is sent as value1 and image is sent as value2 and will be added automatically

        // Twitter
        public string TwitterConsumerKey;
        public string TwitterConsumerSecret;
        public string TwitterAccessToken;
        public string TwitterTokenSecret;

        // Content that's already been syndicated (to prevent duplication)
        public List<SyndicatedURL> SyndicatedURLs;
    }

    struct Source
    {
        public string Type;
        public string URL;
        public string Tags;
    }

    struct SyndicatedURL
    {
        public DateTime Syndicated;
        public string URL;
    }
}
