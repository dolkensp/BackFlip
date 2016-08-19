using System;

namespace BackFlip
{
    class Program
    {
        static void Main(String[] args)
        {
            var spotter = new Spotter(AmazonSecret.ClientId, AmazonSecret.ClientSecret);

            var finished = spotter.Sync(@"D:\Photos", @"Pictures").Result;
        }
    }
}
