using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GameServer
{
    public class PlayerAction
    {
        public int PlayerId { get; set; }
        public float DeltaX  { get; set; }
        public float DeltaY  { get; set; }
        public string ActionType { get; set; }
        public long Timestamp { get; set; }
    }

    public class GameState
    {
        public int SessionId { get; set; }
        public Dictionary<int, PlayerSnapshot> Players { get; set; } = new();
        public long Tick { get; set; }

        public string Serialize() => JsonSerializer.Serialize(this);
    }

    public class PlayerSnapshot
    {
        public string Name { get; set; }
        public float PosX  { get; set; }
        public float PosY  { get; set; }
    }

    public class GameSession
    {
        public int Id { get; private set; }
        public bool IsActive { get; private set; }

        private readonly ConcurrentDictionary<int, Player> _players = new();
        private readonly GameState _state;
        private readonly SemaphoreSlim _stateLock = new SemaphoreSlim(1, 1);
        private long _tick = 0;
        private readonly Action<byte[], System.Net.IPEndPoint> _udpSendCallback;

        public GameSession(int id, Action<byte[], System.Net.IPEndPoint> udpSendCallback)
        {
            Id = id;
            IsActive = true;
            _state = new GameState { SessionId = id };
            _udpSendCallback = udpSendCallback;
            Console.WriteLine($"[Session {Id}] Criada.");
        }

        public void AddPlayer(Player player)
        {
            _players[player.Id] = player;
            _state.Players[player.Id] = new PlayerSnapshot
            {
                Name = player.Name, PosX = player.PosX, PosY = player.PosY
            };
            Console.WriteLine($"[Session {Id}] Jogador '{player.Name}' (Id={player.Id}) adicionado.");
        }

        public void RemovePlayer(int playerId)
        {
            _players.TryRemove(playerId, out _);
            _state.Players.Remove(playerId);
            Console.WriteLine($"[Session {Id}] Jogador Id={playerId} removido.");
        }

        public int PlayerCount => _players.Count;

        public async Task ApplyActionAsync(PlayerAction action)
        {
            await _stateLock.WaitAsync();
            try
            {
                if (!_state.Players.TryGetValue(action.PlayerId, out var snap)) return;

                const float maxDelta = 5.0f;
                snap.PosX += Math.Clamp(action.DeltaX, -maxDelta, maxDelta);
                snap.PosY += Math.Clamp(action.DeltaY, -maxDelta, maxDelta);

                if (_players.TryGetValue(action.PlayerId, out var player))
                {
                    player.PosX = snap.PosX;
                    player.PosY = snap.PosY;
                }

                _state.Tick = Interlocked.Increment(ref _tick);
            }
            finally
            {
                _stateLock.Release();
            }

            await BroadcastStateAsync();
        }

        private async Task BroadcastStateAsync()
        {
            string json;
            await _stateLock.WaitAsync();
            try { json = _state.Serialize(); }
            finally { _stateLock.Release(); }
            byte[] data = System.Text.Encoding.UTF8.GetBytes("STATE:" + json);

            var tasks = _players.Values
                .Where(p => p.IsConnected && p.GetUdpEndPoint() != null)
                .Select(p => Task.Run(() => _udpSendCallback(data, p.GetUdpEndPoint())));

            await Task.WhenAll(tasks);
        }

        public async Task BroadcastTcpAsync(string message)
        {
            var tasks = _players.Values
                .Where(p => p.IsConnected)
                .Select(p => p.SendTcpAsync(message));
            await Task.WhenAll(tasks);
        }

        public async Task EndSessionAsync()
        {
            IsActive = false;
            await BroadcastTcpAsync($"SESSION_END:{Id}");
            foreach (var p in _players.Values) p.Disconnect();
            _players.Clear();
            Console.WriteLine($"[Session {Id}] Encerrada.");
        }

        public List<Player> GetPlayers() => _players.Values.ToList();
    }
}
