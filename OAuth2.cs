using System;


namespace SimpleSyndicator;

public static class OAuth2
{
    public struct OAuth2Token
    {
        public string refresh_token;
        public string access_token;
        public long expires_in;
        public string token_type;
        public DateTime last_updated;
    }
}
