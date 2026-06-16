using System;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace GameServer
{
    public class ReconnectClient
    {
        private const int MAX_ATTEMPTS = 5;
        private const int DELAY_MS = 2000;
        private const string SERVER_HOST = "127.0.0.1";
        private const int SERVER_TCP_PORT = 7777;

        private TcpClient _client;
        private NetworkStream _stream;
        private bool _isConnected = false;
        private string _playerName;
        private int _playerId = -1;

        public ReconnectClient(string playerName)
        {
            _playerName = playerName;
        }

        public async Task<bool> ConnectAsync()
        {
            return await TryReconnectAsync();
        }

        private async Task<bool> TryReconnectAsync()
        {
            int attempts = 0;
            while (attempts < MAX_ATTEMPTS)
            {
                try
                {
                    Console.WriteLine($"Tentativa {attempts + 1}/{MAX_ATTEMPTS}...");
                    _client = new TcpClient();
                    await _client.ConnectAsync(SERVER_HOST, SERVER_TCP_PORT);
                    _stream = _client.GetStream();
                    _isConnected = true;

                    await SendAsync($"AUTH:{_playerName}");
                    string response = await ReceiveAsync();
                    if (response != null && response.StartsWith("AUTH_OK:"))
                    {
                        _playerId = int.Parse(response.Split(':')[1]);
                        Console.WriteLine($"Conectado! PlayerId={_playerId}");
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Falha na tentativa {attempts + 1}: {ex.Message}");
                }

                attempts++;
                if (attempts < MAX_ATTEMPTS)
                    await Task.Delay(DELAY_MS);
            }

            Console.WriteLine("Limite de tentativas atingido. Desconectado.");
            DisconnectPlayer();
            return false;
        }

        public async Task SendAsync(string message)
        {
            if (!_isConnected) return;
            byte[] data = Encoding.UTF8.GetBytes(message + "\n");
            await _stream.WriteAsync(data, 0, data.Length);
        }

        public async Task<string> ReceiveAsync()
        {
            byte[] buffer = new byte[4096];
            int n = await _stream.ReadAsync(buffer, 0, buffer.Length);
            return n > 0 ? Encoding.UTF8.GetString(buffer, 0, n).Trim() : null;
        }

        private void DisconnectPlayer()
        {
            _isConnected = false;
            _stream?.Close();
            _client?.Close();
        }
    }
}
