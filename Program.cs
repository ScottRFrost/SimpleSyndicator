using System;
using System.Threading;

namespace SimpleSyndicator
{
    public static class Program
    {
        public static void Main(string[] args)
        {
            Console.WriteLine(DateTime.Now.ToString("M/d HH:mm") + " Starting Up");
            while (true)
            {
                try
                {
                    Console.WriteLine(DateTime.Now.ToString("M/d HH:mm") + " MAIN LOOP STARTING...");
                    var simpleSyndicator = new SimpleSyndicator();
                    simpleSyndicator.Start().Wait();
                    Console.WriteLine(DateTime.Now.ToString("M/d HH:mm") + " MAIN LOOP CLEAN EXIT");
                }
                catch (Exception ex)
                {
                    Console.WriteLine(DateTime.Now.ToString("M/d HH:mm") + " MAIN LOOP EXIT ERROR - " + ex);
                }

                Console.WriteLine(DateTime.Now.ToString("M/d HH:mm") + " Sleeping until " + DateTime.Now.AddMinutes(15).ToString("M/d HH:mm"));
                Thread.Sleep(900000); // 15 Minutes
            }
        }
    }
}
