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
            if (args.Any(a => a.Equals("--install-browsers", StringComparison.OrdinalIgnoreCase)))
            {
                var withDeps = args.Any(a => a.Equals("--with-deps", StringComparison.OrdinalIgnoreCase));
                Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} PLAYWRIGHT BROWSER SETUP STARTING...");
                await SimpleSyndicator.InstallBrowsersAsync(withDeps).ConfigureAwait(false);
                Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} PLAYWRIGHT BROWSER SETUP COMPLETE");
                return;
            }

            if (args.Any(a => a.Equals("--reddit-login", StringComparison.OrdinalIgnoreCase)))
            {
                Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} REDDIT BROWSER LOGIN STARTING...");
                await SimpleSyndicator.RunRedditLoginAsync().ConfigureAwait(false);
                Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} REDDIT BROWSER LOGIN COMPLETE");
                return;
            }

            if (args.Any(a => a.Equals("--reddit-auth", StringComparison.OrdinalIgnoreCase)))
            {
                Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Note: Reddit OAuth2 support has been replaced with Playwright browser automation because Reddit declined OAuth app issuance.");
                Console.WriteLine("Run '--install-browsers' once to install the browser binaries, then run '--reddit-login' if you want to sign in and save a persistent session (reddit-state.json).");
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
