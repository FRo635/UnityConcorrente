using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GameServer
{
    public class GameServer
    {
        private const int TCP_PORT = 7777;
        private const int UDP_PORT = 8888;
        private const int MAX_RECONNECT_ATTEMPTS = 5;
        private const int RECONNECT_DELAY_MS = 2000;

        private TcpListener _tcpListener;
        private UdpClient _udpServer;
        private CancellationTokenSource _cts;

        private readonly ConcurrentDictionary<int, GameSession> _sessions = new();
        private readonly ConcurrentDictionary<int, Player> _players = new();
        private int _nextPlayerId = 0;
        private int _nextSessionId = 0;

        private readonly DatabaseManager _db;

        public GameServer()
        {
            _db = new DatabaseManager();
        }

        public async Task StartAsync()
        {
            _db.Initialize();
            _cts = new CancellationTokenSource();

            _tcpListener = new TcpListener(IPAddress.Any, TCP_PORT);
            _tcpListener.Start();
            Console.WriteLine($"TCP ouvindo na porta {TCP_PORT}");

            _udpServer = new UdpClient(UDP_PORT);
            Console.WriteLine($"UDP ouvindo na porta {UDP_PORT}");

            _ = Task.Run(() => UdpReceiveLoopAsync(_cts.Token));

            Console.WriteLine("Aguardando conexões");
            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    TcpClient tcpClient = await _tcpListener.AcceptTcpClientAsync(_cts.Token);
                    _ = Task.Run(() => HandleClientAsync(tcpClient, _cts.Token));
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Erro ao aceitar conexão: {ex.Message}");
                }
            }
        }

        private async Task HandleClientAsync(TcpClient tcpClient, CancellationToken ct)
        {
            Player player = null;
            Console.WriteLine($"Nova conexão de {tcpClient.Client.RemoteEndPoint}");

            try
            {
                var stream = tcpClient.GetStream();
                byte[] buffer = new byte[1024];
                int n = await stream.ReadAsync(buffer, 0, buffer.Length, ct);
                string authMsg = Encoding.UTF8.GetString(buffer, 0, n).Trim();

                if (!authMsg.StartsWith("AUTH:"))
                {
                    await SendTcpRawAsync(stream, "ERROR:Protocolo inválido\n");
                    tcpClient.Close();
                    return;
                }

                string playerName = authMsg.Substring(5).Trim();
                if(string.IsNullOrWhiteSpace(playerName)){ await SendTcpRawAsync(stream,"ERROR:Nome inválido\n"); tcpClient.Close(); return; }
                int dbId = _db.UpsertPlayer(playerName);
                int playerId = Interlocked.Increment(ref _nextPlayerId);

                player = new Player(playerId, playerName, tcpClient);
                _players[playerId] = player;

                await player.SendTcpAsync($"AUTH_OK:{playerId}:{playerName}");
                Console.WriteLine($"Jogador autenticado: {player}");

                while (!ct.IsCancellationRequested && player.IsConnected)
                {
                    string msg = await player.ReadTcpAsync();
                    if (msg == null) break;

                    await ProcessCommandAsync(player, msg);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Erro no cliente {player?.Name ?? "?"}: {ex.Message}");
            }
            finally
            {
                await HandleDisconnectAsync(player);
            }
        }

        private async Task ProcessCommandAsync(Player player, string message)
        {
            string[] parts = message.Split(':', 2);
            string cmd = parts[0];
            string payload = parts.Length > 1 ? parts[1] : "";

            switch (cmd)
            {
                case "JOIN_SESSION":
                    await HandleJoinSessionAsync(player, payload);
                    break;

                case "CREATE_SESSION":
                    await HandleCreateSessionAsync(player);
                    break;

                case "LEAVE_SESSION":
                    await HandleLeaveSessionAsync(player, payload);
                    break;

                case "UDP_PORT":
                    if (int.TryParse(payload, out int udpPort))
                    {
                        var remoteIp = ((IPEndPoint)player.TcpClient.Client.RemoteEndPoint).Address;

                        player.SetUdpEndPoint(new IPEndPoint(remoteIp, udpPort));
                        Console.WriteLine($"UDP endpoint de {player.Name}: {player.GetUdpEndPoint()}");
                    }
                    break;

                case "PING":
                    await player.SendTcpAsync("PONG");
                    break;

                default:
                    await player.SendTcpAsync($"ERROR:Comando desconhecido '{cmd}'");
                    break;
            }
        }

        private async Task HandleCreateSessionAsync(Player player)
        {
            int sessionId = Interlocked.Increment(ref _nextSessionId);
            var session = new GameSession(sessionId, UdpSendTo);
            _sessions[sessionId] = session;
            session.AddPlayer(player);
            player.SessionId = sessionId;
            await player.SendTcpAsync($"SESSION_CREATED:{sessionId}");
        }

        private async Task HandleJoinSessionAsync(Player player, string payload)
        {
            if (!int.TryParse(payload, out int sessionId) || !_sessions.TryGetValue(sessionId, out var session))
            {
                await player.SendTcpAsync("ERROR:Sessão não encontrada");
                return;
            }
            session.AddPlayer(player);
            player.SessionId = sessionId;
            await player.SendTcpAsync($"SESSION_JOINED:{sessionId}");
            await session.BroadcastTcpAsync($"PLAYER_JOINED:{player.Id}:{player.Name}");
        }

        private async Task HandleLeaveSessionAsync(Player player, string payload)
        {
            if (int.TryParse(payload, out int sessionId) && _sessions.TryGetValue(sessionId, out var session))
            {
                session.RemovePlayer(player.Id);
                player.SessionId = -1;
                await player.SendTcpAsync($"SESSION_LEFT:{sessionId}");
                await session.BroadcastTcpAsync($"PLAYER_LEFT:{player.Id}:{player.Name}");
            }
        }

        private async Task HandleDisconnectAsync(Player player)
        {
            if (player == null) return;
            Console.WriteLine($"Jogador desconectado: {player.Name}");

            foreach (var session in _sessions.Values)
            {
                if (session.IsActive)
                {
                    session.RemovePlayer(player.Id);
                    await session.BroadcastTcpAsync($"PLAYER_DISCONNECTED:{player.Id}:{player.Name}");
                }
            }
            _players.TryRemove(player.Id, out _);
            player.Disconnect();
        }

        private async Task UdpReceiveLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    UdpReceiveResult result = await _udpServer.ReceiveAsync(ct);
                    string raw = Encoding.UTF8.GetString(result.Buffer);

                    if (raw.StartsWith("ACTION:"))
                    {
                        string json = raw.Substring(7);
                        var action = JsonSerializer.Deserialize<PlayerAction>(json);

                        if (_players.TryGetValue(action.PlayerId, out var actPlayer) && actPlayer.SessionId >= 0 && _sessions.TryGetValue(actPlayer.SessionId, out var session) && session.IsActive)
                        {
                            _ = session.ApplyActionAsync(action);
                        }
                    }
                    else if (raw.StartsWith("REGISTER_UDP:"))
                    {
                        string[] parts = raw.Substring(13).Split(':');
                        if (parts.Length == 2 && int.TryParse(parts[0], out int pid) &&
                            int.TryParse(parts[1], out int port) &&
                            _players.TryGetValue(pid, out var player))
                        {
                            player.SetUdpEndPoint(new IPEndPoint(result.RemoteEndPoint.Address, port));
                        }
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Console.WriteLine($"[UDP] Erro: {ex.Message}");
                }
            }
        }

        private void UdpSendTo(byte[] data, IPEndPoint endpoint)
        {
            try { _udpServer.Send(data, data.Length, endpoint); }
            catch (Exception ex) { Console.WriteLine($"[UDP] Erro ao enviar: {ex.Message}"); }
        }

        private static async Task SendTcpRawAsync(NetworkStream stream, string message)
        {
            byte[] data = Encoding.UTF8.GetBytes(message);
            await stream.WriteAsync(data, 0, data.Length);
        }

        public void Stop()
        {
            _cts?.Cancel();
            _tcpListener?.Stop();
            _udpServer?.Close();
            _db?.Dispose();
            Console.WriteLine("Servidor encerrado.");
        }
    }
}
