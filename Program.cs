using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
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

    public static TimeSpan ComputeNextRunDelay(int minMinutes = 75, int maxMinutes = 90)
    {
        if (maxMinutes <= 0)
        {
            return TimeSpan.Zero;
        }

        var effectiveMin = Math.Max(0, minMinutes);
        var effectiveMax = Math.Max(effectiveMin, maxMinutes);
        var minMs = effectiveMin * 60_000.0;
        var maxMs = effectiveMax * 60_000.0;
        var delayMs = minMs + (Random.Shared.NextDouble() * (maxMs - minMs));
        return TimeSpan.FromMilliseconds(Math.Round(delayMs));
    }

    public static IDisposable TryAcquireSingleInstanceLock(string lockFilePath = null, bool checkProcessesByName = true)
    {
        if (checkProcessesByName)
        {
            using var currentProcess = Process.GetCurrentProcess();
            if (currentProcess.ProcessName.StartsWith("SimpleSyndicator", StringComparison.OrdinalIgnoreCase))
            {
                var matchingProcesses = Process.GetProcessesByName(currentProcess.ProcessName);
                try
                {
                    if (matchingProcesses.Any(p => p.Id != currentProcess.Id))
                    {
                        return null;
                    }
                }
                finally
                {
                    foreach (var p in matchingProcesses)
                    {
                        p.Dispose();
                    }
                }
            }
        }

        var effectivePath = string.IsNullOrWhiteSpace(lockFilePath)
            ? Path.Combine(Path.GetTempPath(), "SimpleSyndicator.instance.lock")
            : lockFilePath;

        var lockDirectory = Path.GetDirectoryName(effectivePath);
        if (!string.IsNullOrEmpty(lockDirectory))
        {
            Directory.CreateDirectory(lockDirectory);
        }

        FileStream lockStream = null;
        try
        {
            lockStream = new FileStream(
                effectivePath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
            lockStream.SetLength(0);
            var pidBytes = Encoding.UTF8.GetBytes(Environment.ProcessId.ToString());
            lockStream.Write(pidBytes, 0, pidBytes.Length);
            lockStream.Flush();
            return lockStream;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            lockStream?.Dispose();
            return null;
        }
    }

    public static async Task Main(string[] args)
    {
        Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Starting Up");

        using var instanceLock = TryAcquireSingleInstanceLock();
        if (instanceLock == null)
        {
            Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Another instance of SimpleSyndicator is already running. Terminating immediately.");
            return;
        }

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
                    while (true)
                    {
                        Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} MAIN LOOP STARTING...");
                        try
                        {
                            await SimpleSyndicator.Start().ConfigureAwait(false);
                            Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} MAIN LOOP COMPLETE");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} MAIN LOOP ERROR - {ex}");
                        }

                        var waitDelay = ComputeNextRunDelay(75, 90);
                        var nextRunAt = DateTime.Now.Add(waitDelay);
                        Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Waiting {waitDelay.TotalMinutes:F1} minutes until next run (at {nextRunAt:M/d HH:mm:ss})...");
                        await Task.Delay(waitDelay).ConfigureAwait(false);
                    }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} MAIN LOOP EXIT ERROR - {ex}");
        }
    }
}
