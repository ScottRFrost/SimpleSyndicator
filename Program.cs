using System;
using System.Linq;
using System.Threading.Tasks;

namespace SimpleSyndicator;

public static class Program
{
    public static async Task Main(string[] args)
    {
        Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Starting Up");
        try
        {
            if (args.Any(a => a.Equals("--reddit-auth", StringComparison.OrdinalIgnoreCase)))
            {
                Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} REDDIT OAUTH SETUP STARTING...");
                await SimpleSyndicator.RunRedditAuthAsync().ConfigureAwait(false);
                Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} REDDIT OAUTH SETUP COMPLETE");
                return;
            }

            Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} MAIN LOOP STARTING...");
            await SimpleSyndicator.Start().ConfigureAwait(false);
            Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} MAIN LOOP CLEAN EXIT");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} MAIN LOOP EXIT ERROR - {ex}");
        }
    }
}
