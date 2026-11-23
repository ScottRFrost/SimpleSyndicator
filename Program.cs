using System;
using System.Linq;
using System.Threading.Tasks;

namespace SimpleSyndicator;

public sealed record InstallBrowsersCommand(bool WithDeps);
public sealed record RedditLoginCommand;
public sealed record JustStartBrowserCommand(string InitialUrl = null);
public sealed record LegacyRedditAuthCommand;
public sealed record RunSyndicatorCommand;

public union CliCommand(
    InstallBrowsersCommand,
    RedditLoginCommand,
    JustStartBrowserCommand,
    LegacyRedditAuthCommand,
    RunSyndicatorCommand);

public static class Program
{
    public static CliCommand ParseCommand(string[] args)
    {
        args ??= [];

        if (args.Any(a => a.Equals("--install-browsers", StringComparison.OrdinalIgnoreCase)))
        {
            var withDeps = args.Any(a => a.Equals("--with-deps", StringComparison.OrdinalIgnoreCase));
            return new InstallBrowsersCommand(withDeps);
        }

        if (args.Any(a => a.Equals("--reddit-login", StringComparison.OrdinalIgnoreCase)))
        {
            return new RedditLoginCommand();
        }

        if (args.Any(a =>
                a.Equals("-JustStartBrowser", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("--JustStartBrowser", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("--just-start-browser", StringComparison.OrdinalIgnoreCase)))
        {
            var initialUrl = args.FirstOrDefault(a =>
                a.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                a.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
            return new JustStartBrowserCommand(initialUrl);
        }

        if (args.Any(a => a.Equals("--reddit-auth", StringComparison.OrdinalIgnoreCase)))
        {
            return new LegacyRedditAuthCommand();
        }

        return new RunSyndicatorCommand();
    }

    public static async Task Main(string[] args)
    {
        Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Starting Up");
        try
        {
            switch (ParseCommand(args))
            {
                case InstallBrowsersCommand cmd:
                    Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} PLAYWRIGHT BROWSER SETUP STARTING...");
                    await SimpleSyndicator.InstallBrowsersAsync(cmd.WithDeps).ConfigureAwait(false);
                    Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} PLAYWRIGHT BROWSER SETUP COMPLETE");
                    break;

                case RedditLoginCommand:
                    Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} REDDIT BROWSER LOGIN STARTING...");
                    await SimpleSyndicator.RunRedditLoginAsync().ConfigureAwait(false);
                    Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} REDDIT BROWSER LOGIN COMPLETE");
                    break;

                case JustStartBrowserCommand cmd:
                    Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} INTERACTIVE BROWSER TEST STARTING...");
                    await SimpleSyndicator.RunInteractiveBrowserAsync(cmd.InitialUrl).ConfigureAwait(false);
                    Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} INTERACTIVE BROWSER TEST COMPLETE");
                    break;

                case LegacyRedditAuthCommand:
                    Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Note: Reddit OAuth2 support has been replaced with Playwright browser automation because Reddit declined OAuth app issuance.");
                    Console.WriteLine("Run '--install-browsers' once to install the browser binaries, then run '--reddit-login' if you want to sign in and save a persistent session (reddit-state.json).");
                    break;

                case RunSyndicatorCommand:
                    Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} MAIN LOOP STARTING...");
                    await SimpleSyndicator.Start().ConfigureAwait(false);
                    Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} MAIN LOOP CLEAN EXIT");
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} MAIN LOOP EXIT ERROR - {ex}");
        }
    }
}
