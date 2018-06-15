using System;
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
                var sh = new SocketHandler("127.0.0.1", 9001, 1000);
                sh.Open();

                var data = File.ReadAllBytes("test.zip");
                sh.Send("127.0.0.1", 9000, data);
            }
            else
            {
                Console.WriteLine("[Receiver]");
                var sh = new SocketHandler("127.0.0.1", 9000, 2000);
                sh.Open();
            }
            

            while (true)
            {
               Thread.Sleep(100);
            }
        }
    }
}
