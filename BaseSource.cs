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
        public string Tags;
    }
}
