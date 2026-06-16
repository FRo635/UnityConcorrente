using System;
using System.Threading;
using System.Threading.Tasks;

namespace GameServer
{
    class Program
    {
        static async Task Main(string[] args)
        {

            var server = new GameServer();

            // Ctrl+C
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                server.Stop();
                Environment.Exit(0);
            };

            try
            {
                await server.StartAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
        }
    }
}
