using System;
using System.Threading.Tasks;

namespace SimpleSyndicator;

public abstract class BaseSource
{
    abstract public Task<SyndicatorContent[]> Parse(string Url, string Tags = "");

    public struct SyndicatorContent
    {
        public string Description;
        public string LinkURL;
        public string ImageURL;
        public string FallbackImageURL;
        public string ReferrerURL;
        public string Tags;
    }
}

public static class SyndicatorExtensions
{
    extension(BaseSource.SyndicatorContent content)
    {
        public bool IsAnimation =>
            !string.IsNullOrEmpty(content.ImageURL) &&
            (content.ImageURL.Contains(".gif", StringComparison.OrdinalIgnoreCase) ||
             content.ImageURL.Contains(".mp4", StringComparison.OrdinalIgnoreCase));

        public bool IsPhoto => !content.IsAnimation;

        public string FileName
        {
            get
            {
                if (string.IsNullOrEmpty(content.ImageURL))
                {
                    return "file";
                }

                var cleanUrl = content.ImageURL.Split('?')[0];
                var fileName = cleanUrl[(cleanUrl.LastIndexOf('/') + 1)..];
                return string.IsNullOrEmpty(fileName) ? "file" : fileName;
            }
        }
    }

    extension(DateTime value)
    {
        public DateTime EnsureUtc() => value.Kind == DateTimeKind.Local
            ? value.ToUniversalTime()
            : DateTime.SpecifyKind(value, DateTimeKind.Utc);
    }
}
