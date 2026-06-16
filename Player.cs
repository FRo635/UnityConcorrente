using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace GameServer
{
    public class Player
    {
        public int Id { get; private set; }
        public string Name { get; set; }
        public float PosX { get; set; }
        public float PosY { get; set; }
        public int Matches { get; set; }
        public bool IsConnected { get; private set; }
        public int SessionId { get; set; } = -1;
        public TcpClient TcpClient => _tcpClient;

        private TcpClient _tcpClient;
        private NetworkStream _stream;
        private System.IO.StreamReader _reader;
        private IPEndPoint _udpEndPoint;

        public Player(int id, string name, TcpClient tcpClient)
        {
            Id = id;
            Name = name;
            _tcpClient = tcpClient;
            _stream = tcpClient.GetStream();
            _reader = new System.IO.StreamReader(_stream, Encoding.UTF8);
            IsConnected = true;
            PosX = 0f;
            PosY = 0f;
            Matches = 0;
        }

        public void SetUdpEndPoint(IPEndPoint endPoint)
        {
            _udpEndPoint = endPoint;
        }

        public IPEndPoint GetUdpEndPoint() => _udpEndPoint;

        public async Task SendTcpAsync(string message)
        {
            if (!IsConnected) return;
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(message + "\n");
                await _stream.WriteAsync(data, 0, data.Length);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Player {Id}] Erro ao enviar TCP: {ex.Message}");
                Disconnect();
            }
        }

        public async Task<string> ReadTcpAsync()
        {
            if (!IsConnected) return null;
            try
            {
                return await _reader.ReadLineAsync();
            }
            catch
            {
                Disconnect();
                return null;
            }
        }

        public void Disconnect()
        {
            IsConnected = false;
            _stream?.Close();
            _tcpClient?.Close();
        }

        public override string ToString() =>
            $"Player[Id={Id}, Name={Name}, Pos=({PosX:F1},{PosY:F1}), Connected={IsConnected}]";
    }
}
