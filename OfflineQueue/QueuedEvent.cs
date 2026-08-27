namespace WorkerService1.OfflineQueue;

// A terminal event sitting in the local SQLite queue, waiting to be synced
// to the backend. Mirrors the fields BiometricSyncRequest needs on the
// backend side, plus queue bookkeeping (Id, Attempts, LastError) that never
// leaves this agent.
public class QueuedEvent
{
    public long Id { get; set; }
    public string EventId { get; set; } = string.Empty;
    public string TerminalUserId { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public string VerifyMethod { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;

    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedAt { get; set; }
}