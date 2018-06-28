using System;
using System.Globalization;
using System.IO;
using System.Threading;

namespace UDP_File_Transfer
{
    class Program
    {
        static void Main(string[] args)
        {
            if (args.Length == 0)
            {
                Console.WriteLine("[Sender]");
                var sh = new SocketHandler(9001, 1000, 500);
                sh.Open();

                string fileName = "test.zip";
                do
                {
                    Console.WriteLine("Please enter file name: ");
                    //fileName = Console.ReadLine();
                } while (null == fileName || !File.Exists(fileName));
                
                sh.Send("127.0.0.1", 9000, File.ReadAllBytes(fileName));
            }
            else
            {
                Console.WriteLine("[Receiver]");
                var sh = new SocketHandler(9000, 2000, 500);
                sh.Open();
            }

            while (true)
            {
               Thread.Sleep(100);
            }
        }
    }
}
