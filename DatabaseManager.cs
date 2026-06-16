using System;
using Microsoft.Data.Sqlite;

namespace GameServer
{
    public class DatabaseManager : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly string _connectionString;

        public DatabaseManager(string dbPath = "gameserver.db")
        {
            _connectionString = $"Data Source={dbPath}";
            _connection = new SqliteConnection(_connectionString);
        }

        public void Initialize()
        {
            _connection.Open();
            var cmd = _connection.CreateCommand();

            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS Players (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Name TEXT NOT NULL UNIQUE,
                    MatchesPlayed INTEGER DEFAULT 0,
                    Wins INTEGER DEFAULT 0,
                    CreatedAt TEXT DEFAULT (datetime('now'))
                );";
            cmd.ExecuteNonQuery();

            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS Matches (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    SessionId INTEGER NOT NULL,
                    StartedAt TEXT,
                    EndedAt TEXT,
                    WinnerId INTEGER,
                    FOREIGN KEY (WinnerId) REFERENCES Players(Id)
                );";
            cmd.ExecuteNonQuery();

            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS MatchParticipants (
                    MatchId INTEGER,
                    PlayerId INTEGER,
                    PRIMARY KEY (MatchId, PlayerId),
                    FOREIGN KEY (MatchId) REFERENCES Matches(Id),
                    FOREIGN KEY (PlayerId) REFERENCES Players(Id)
                );";
            cmd.ExecuteNonQuery();

            Console.WriteLine("Banco de dados online");
        }

        public int UpsertPlayer(string name)
        {
            var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO Players (Name) VALUES (@name)
                ON CONFLICT(Name) DO UPDATE SET Name=Name
                RETURNING Id;";
            cmd.Parameters.AddWithValue("@name", name);
            var result = cmd.ExecuteScalar();
            return Convert.ToInt32(result);
        }

        public int StartMatch(int sessionId)
        {
            var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO Matches (SessionId, StartedAt) VALUES (@sid, datetime('now'))
                RETURNING Id;";
            cmd.Parameters.AddWithValue("@sid", sessionId);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }

        public void EndMatch(int matchId, int winnerId, int[] participantIds)
        {
            using var tx = _connection.BeginTransaction();
            try
            {
                var cmd = _connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"
                    UPDATE Matches SET EndedAt=datetime('now'), WinnerId=@wid WHERE Id=@mid;";
                cmd.Parameters.AddWithValue("@wid", winnerId);
                cmd.Parameters.AddWithValue("@mid", matchId);
                cmd.ExecuteNonQuery();

                foreach (var pid in participantIds)
                {
                    cmd = _connection.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = @"
                        INSERT OR IGNORE INTO MatchParticipants (MatchId, PlayerId) VALUES (@mid, @pid);
                        UPDATE Players SET MatchesPlayed = MatchesPlayed + 1 WHERE Id = @pid;";
                    cmd.Parameters.AddWithValue("@mid", matchId);
                    cmd.Parameters.AddWithValue("@pid", pid);
                    cmd.ExecuteNonQuery();
                }

                cmd = _connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE Players SET Wins = Wins + 1 WHERE Id = @wid;";
                cmd.Parameters.AddWithValue("@wid", winnerId);
                cmd.ExecuteNonQuery();

                tx.Commit();
                Console.WriteLine($"Partida {matchId} encerrada. Vencedor: Id={winnerId}");
            }
            catch (Exception ex)
            {
                tx.Rollback();
                Console.WriteLine(ex.Message);
            }
        }

        public void Dispose()
        {
            _connection?.Close();
            _connection?.Dispose();
        }
    }
}