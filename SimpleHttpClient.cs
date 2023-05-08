using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace SimpleSyndicator
{
    public class SimpleHttpClient : HttpClient
    {
        public SimpleHttpClient()
        {
            Timeout = new TimeSpan(0, 0, 30);
            ReferrerUri = "https://duckduckgo.com";
            AuthorizationHeader = string.Empty;
            DefaultRequestHeaders.TryAddWithoutValidation("ACCEPT", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
            DefaultRequestHeaders.TryAddWithoutValidation("ACCEPT-ENCODING", "gzip, deflate");
            DefaultRequestHeaders.TryAddWithoutValidation("ACCEPT-LANGUAGE", "en-US,en;q=0.5");
            DefaultRequestHeaders.TryAddWithoutValidation("CONNECTION", "keep-alive");
            DefaultRequestHeaders.TryAddWithoutValidation("DNT", "1");
            DefaultRequestHeaders.TryAddWithoutValidation("SEC-FETCH-DEST", "document");
            DefaultRequestHeaders.TryAddWithoutValidation("SEC-FETCH-MODE", "navigate");
            DefaultRequestHeaders.TryAddWithoutValidation("SEC-FETCH-SITE", "none");
            DefaultRequestHeaders.TryAddWithoutValidation("SEC-FETCH-USER", "?1");
            DefaultRequestHeaders.TryAddWithoutValidation("SEC-GPC", "1");
            DefaultRequestHeaders.TryAddWithoutValidation("TE", "trailers");
            DefaultRequestHeaders.TryAddWithoutValidation("UPGRADE-INSECURE-REQUESTS", "1");
            DefaultRequestHeaders.TryAddWithoutValidation("USER-AGENT", "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:109.0) Gecko/20100101 Firefox/113.0");
            CustomHeaders = new Dictionary<string, string>();
        }

        public string AuthorizationHeader { get; set; }

        public string BingHeader { get; set; }

        public string ReferrerUri { get; set; }

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
                Console.Write(ex.ToString());
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
                Console.Write(ex.ToString());
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
                Console.WriteLine(header.Key + ":" + header.Value);
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
    }
}
