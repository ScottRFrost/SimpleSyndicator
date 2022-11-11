using System;
using System.Threading;

namespace SimpleSyndicator
{
    public static class Program
    {
        public static void Main(string[] args)
        {
            Console.WriteLine(DateTime.Now.ToString("M/d HH:mm") + " Starting Up");
            try
            {
                Console.WriteLine(DateTime.Now.ToString("M/d HH:mm") + " MAIN LOOP STARTING...");
                SimpleSyndicator.Start().Wait();
                Console.WriteLine(DateTime.Now.ToString("M/d HH:mm") + " MAIN LOOP CLEAN EXIT");
            }
            catch (Exception ex)
            {
                Console.WriteLine(DateTime.Now.ToString("M/d HH:mm") + " MAIN LOOP EXIT ERROR - " + ex);
            }
        }
    }
}
