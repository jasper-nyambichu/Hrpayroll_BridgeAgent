using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using System.IO;

namespace WorkerService1.OfflineQueue;

public class OfflineQueueStore
{
    private readonly string _connectionString;
    private readonly ILogger<OfflineQueueStore> _logger;

    // Backoff schedule: 15s, 30s, 60s, 2m, 5m — then holds at 5m forever.
    // After MaxAttempts, the row is left in place (never lost) but stops
    // being retried automatically; it needs manual attention (surfaced in
    // the UI later) rather than hammering the backend indefinitely for
    // something that will never succeed, like an unmapped terminal user.
    private static readonly TimeSpan[] BackoffSchedule =
    {
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5),
    };
    private const int MaxAttempts = 20;

    public OfflineQueueStore(ILogger<OfflineQueueStore> logger)
    {
        _logger = logger;
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
                CreatedAt TEXT NOT NULL,
                NextAttemptAt TEXT NOT NULL DEFAULT (datetime('now'))
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
                (EventId, TerminalUserId, Timestamp, VerifyMethod, EventType, CreatedAt, NextAttemptAt)
            VALUES
                ($eventId, $terminalUserId, $timestamp, $verifyMethod, $eventType, $createdAt, $nextAttemptAt);
            """;
        command.Parameters.AddWithValue("$eventId", evt.EventId);
        command.Parameters.AddWithValue("$terminalUserId", evt.TerminalUserId);
        command.Parameters.AddWithValue("$timestamp", evt.Timestamp.ToString("O"));
        command.Parameters.AddWithValue("$verifyMethod", evt.VerifyMethod);
        command.Parameters.AddWithValue("$eventType", evt.EventType);
        command.Parameters.AddWithValue("$createdAt", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$nextAttemptAt", DateTime.UtcNow.ToString("O")); // eligible immediately
        command.ExecuteNonQuery();
    }

    // Only returns rows whose backoff window has actually elapsed, and
    // excludes rows that have exhausted MaxAttempts (they stay in the
    // table for visibility but are no longer auto-retried).
    public List<QueuedEvent> GetPending(int maxBatchSize = 20)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, EventId, TerminalUserId, Timestamp, VerifyMethod, EventType, Attempts, LastError, CreatedAt
            FROM queued_events
            WHERE NextAttemptAt <= $now AND Attempts < $maxAttempts
            ORDER BY CreatedAt ASC
            LIMIT $maxBatchSize;
            """;
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$maxAttempts", MaxAttempts);
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

    // Increments Attempts and schedules the next retry using the backoff
    // table above — replaces the old "retry every cycle forever" behavior.
    public void MarkFailedAttempt(long id, string error)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var selectCommand = connection.CreateCommand();
        selectCommand.CommandText = "SELECT Attempts FROM queued_events WHERE Id = $id;";
        selectCommand.Parameters.AddWithValue("$id", id);
        var currentAttempts = Convert.ToInt32(selectCommand.ExecuteScalar());

        var newAttempts = currentAttempts + 1;
        var delayIndex = Math.Min(newAttempts - 1, BackoffSchedule.Length - 1);
        var nextAttemptAt = DateTime.UtcNow.Add(BackoffSchedule[delayIndex]);

        var updateCommand = connection.CreateCommand();
        updateCommand.CommandText = """
            UPDATE queued_events
            SET Attempts = $attempts, LastError = $error, NextAttemptAt = $nextAttemptAt
            WHERE Id = $id;
            """;
        updateCommand.Parameters.AddWithValue("$attempts", newAttempts);
        updateCommand.Parameters.AddWithValue("$error", error);
        updateCommand.Parameters.AddWithValue("$nextAttemptAt", nextAttemptAt.ToString("O"));
        updateCommand.Parameters.AddWithValue("$id", id);
        updateCommand.ExecuteNonQuery();
    }

    // Rows that have exhausted retries — surfaced in the UI as needing
    // manual attention rather than silently retried forever.
    public List<QueuedEvent> GetStuck()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, EventId, TerminalUserId, Timestamp, VerifyMethod, EventType, Attempts, LastError, CreatedAt
            FROM queued_events
            WHERE Attempts >= $maxAttempts
            ORDER BY CreatedAt ASC;
            """;
        command.Parameters.AddWithValue("$maxAttempts", MaxAttempts);

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
}