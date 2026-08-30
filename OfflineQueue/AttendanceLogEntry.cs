namespace WorkerService1.OfflineQueue;

// Permanent record of every punch the agent has ever seen, independent of
// the sync queue (which deletes a row the moment it syncs successfully).
// This is what the desktop UI reads from to show "who clocked in/out
// today" — the queue is transient plumbing, this is the durable history.
public class AttendanceLogEntry
{
    public long Id { get; set; }
    public string EventId { get; set; } = string.Empty;
    public string TerminalUserId { get; set; } = string.Empty;
    public string? EmployeeName { get; set; } // filled in once we have the mapping cache
    public DateTime Timestamp { get; set; }
    public string EventType { get; set; } = string.Empty; // CLOCK_IN / CLOCK_OUT
    public string SyncStatus { get; set; } = "Pending";     // Pending, Synced, Failed
    public string? LastError { get; set; }
}