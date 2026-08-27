using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace WorkerService1.OfflineQueue;

// Local durable queue for terminal events. Every punch read off the
// terminal gets written here first, then the sync engine attempts to push
// each row to the backend and only deletes it on confirmed success — so a
// dropped network connection never loses an attendance event, it just
// waits here until connectivity returns.
public class OfflineQueueStore
{
    private readonly string _connectionString;
    private readonly ILogger<OfflineQueueStore> _logger;

    public OfflineQueueStore(ILogger<OfflineQueueStore> logger)
    {
        _logger = logger;

        // Stored alongside the agent's executable — fine for now; once we
        // package this as an installed service we'll move this to a proper
        // ProgramData path so it survives updates and isn't tied to the
        // install folder.
        var dbPath = Path.Combine(AppContext.BaseDirectory, "offline-queue.db");
        _connectionString = $"Data Source={dbPath}";
    }

    public void Initialize()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS queued_events (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                EventId TEXT NOT NULL UNIQUE,
                TerminalUserId TEXT NOT NULL,
                Timestamp TEXT NOT NULL,
                VerifyMethod TEXT NOT NULL,
                EventType TEXT NOT NULL,
                Attempts INTEGER NOT NULL DEFAULT 0,
                LastError TEXT,
                CreatedAt TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();

        _logger.LogInformation("Offline queue initialized at {DbPath}", _connectionString);
    }

    public void Enqueue(QueuedEvent evt)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO queued_events
                (EventId, TerminalUserId, Timestamp, VerifyMethod, EventType, CreatedAt)
            VALUES
                ($eventId, $terminalUserId, $timestamp, $verifyMethod, $eventType, $createdAt);
            """;
        command.Parameters.AddWithValue("$eventId", evt.EventId);
        command.Parameters.AddWithValue("$terminalUserId", evt.TerminalUserId);
        command.Parameters.AddWithValue("$timestamp", evt.Timestamp.ToString("O"));
        command.Parameters.AddWithValue("$verifyMethod", evt.VerifyMethod);
        command.Parameters.AddWithValue("$eventType", evt.EventType);
        command.Parameters.AddWithValue("$createdAt", DateTime.UtcNow.ToString("O"));

        command.ExecuteNonQuery();

        // INSERT OR IGNORE means a duplicate EventId silently does nothing —
        // this is the agent-side half of idempotency; the backend's own
        // findByEventId check is the other half.
    }

    public List<QueuedEvent> GetPending(int maxBatchSize = 20)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, EventId, TerminalUserId, Timestamp, VerifyMethod, EventType, Attempts, LastError, CreatedAt
            FROM queued_events
            ORDER BY CreatedAt ASC
            LIMIT $maxBatchSize;
            """;
        command.Parameters.AddWithValue("$maxBatchSize", maxBatchSize);

        var results = new List<QueuedEvent>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new QueuedEvent
            {
                Id = reader.GetInt64(0),
                EventId = reader.GetString(1),
                TerminalUserId = reader.GetString(2),
                Timestamp = DateTime.Parse(reader.GetString(3)),
                VerifyMethod = reader.GetString(4),
                EventType = reader.GetString(5),
                Attempts = reader.GetInt32(6),
                LastError = reader.IsDBNull(7) ? null : reader.GetString(7),
                CreatedAt = DateTime.Parse(reader.GetString(8)),
            });
        }

        return results;
    }

    public void MarkSynced(long id)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM queued_events WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void MarkFailedAttempt(long id, string error)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE queued_events
            SET Attempts = Attempts + 1, LastError = $error
            WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$error", error);
        command.ExecuteNonQuery();
    }
}