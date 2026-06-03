using System;
using System.Threading;

namespace SimpleSyndicator;

public static class Program
{
    public static void Main(string[] args)
    {
        Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} Starting Up");
        try
        {
            Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} MAIN LOOP STARTING...");
            SimpleSyndicator.Start().Wait();
            Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} MAIN LOOP CLEAN EXIT");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{DateTime.Now:M/d HH:mm:ss} MAIN LOOP EXIT ERROR - {ex}");
        }
    }
}
