using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using System.IO;

namespace WorkerService1.OfflineQueue;

public class AttendanceLogStore
{
    private readonly string _connectionString;
    private readonly ILogger<AttendanceLogStore> _logger;

    public AttendanceLogStore(ILogger<AttendanceLogStore> logger)
    {
        _logger = logger;
        var dbPath = Path.Combine(AppContext.BaseDirectory, "attendance-log.db");
        _connectionString = $"Data Source={dbPath}";
    }

    public void Initialize()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS attendance_log (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                EventId TEXT NOT NULL UNIQUE,
                TerminalUserId TEXT NOT NULL,
                EmployeeName TEXT,
                Timestamp TEXT NOT NULL,
                EventType TEXT NOT NULL,
                SyncStatus TEXT NOT NULL DEFAULT 'Pending',
                LastError TEXT
            );
            """;
        command.ExecuteNonQuery();

        _logger.LogInformation("Attendance log initialized at {DbPath}", _connectionString);
    }

    public void RecordPunch(AttendanceLogEntry entry)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO attendance_log
                (EventId, TerminalUserId, EmployeeName, Timestamp, EventType, SyncStatus)
            VALUES
                ($eventId, $terminalUserId, $employeeName, $timestamp, $eventType, $syncStatus);
            """;
        command.Parameters.AddWithValue("$eventId", entry.EventId);
        command.Parameters.AddWithValue("$terminalUserId", entry.TerminalUserId);
        command.Parameters.AddWithValue("$employeeName", (object?)entry.EmployeeName ?? DBNull.Value);
        command.Parameters.AddWithValue("$timestamp", entry.Timestamp.ToString("O"));
        command.Parameters.AddWithValue("$eventType", entry.EventType);
        command.Parameters.AddWithValue("$syncStatus", entry.SyncStatus);
        command.ExecuteNonQuery();
    }

    public void UpdateStatus(string eventId, string status, string? error = null)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE attendance_log
            SET SyncStatus = $status, LastError = $error
            WHERE EventId = $eventId;
            """;
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("$eventId", eventId);
        command.ExecuteNonQuery();
    }

    public List<AttendanceLogEntry> GetRecent(int limit = 100)
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            var command = connection.CreateCommand();
            command.CommandText = """
            SELECT Id, EventId, TerminalUserId, EmployeeName, Timestamp, EventType, SyncStatus, LastError
            FROM attendance_log
            ORDER BY Timestamp DESC
            LIMIT $limit;
            """;
            command.Parameters.AddWithValue("$limit", limit);

            var results = new List<AttendanceLogEntry>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new AttendanceLogEntry
                {
                    Id = reader.GetInt64(0),
                    EventId = reader.GetString(1),
                    TerminalUserId = reader.GetString(2),
                    EmployeeName = reader.IsDBNull(3) ? null : reader.GetString(3),
                    Timestamp = DateTime.Parse(reader.GetString(4)),
                    EventType = reader.GetString(5),
                    SyncStatus = reader.GetString(6),
                    LastError = reader.IsDBNull(7) ? null : reader.GetString(7),
                });
            }
            return results;
        }
        catch (SqliteException)
        {
            // Table doesn't exist yet — the engine hasn't finished Initialize()
            // on its own thread. The UI will simply show an empty list this
            // tick and pick up real data on the next refresh once it's ready.
            return new List<AttendanceLogEntry>();
        }
    }

}