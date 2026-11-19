using System;
using System.Text.Json;

namespace SimpleSyndicator;

public static class OAuth2
{
#pragma warning disable IDE1006 // Names must begin in upper case
    public record OAuth2Token
    {
        public string access_token { get; set; }
        public string token_type { get; set; }
        public long expires_in { get; set; }
        public string refresh_token { get; set; }
        public string scope { get; set; }
        public JsonElement? error { get; set; }
        public string error_description { get; set; }
        public string message { get; set; }
    }
#pragma warning restore IDE1006 // Names must begin in upper case
}
