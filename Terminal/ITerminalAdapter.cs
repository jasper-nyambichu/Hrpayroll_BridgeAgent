namespace WorkerService1.Terminal;

// The seam between the sync engine and whatever physical device we're
// talking to. MockTerminalAdapter implements this now so we can build and
// test everything else without hardware; ZkTecoTerminalAdapter will
// implement the same interface later against the real TCP/IP protocol.
public interface ITerminalAdapter
{
    Task ConnectAsync(CancellationToken cancellationToken);

    // Raw attendance punches the terminal has recorded since we last asked.
    Task<IReadOnlyList<TerminalEvent>> PollEventsAsync(CancellationToken cancellationToken);

    // Pushes the branch's terminalUserId -> employeeId mappings down so the
    // physical terminal (or, for the mock, our in-memory model of one) knows
    // who's enrolled. Real ZKTeco terminals do their own on-device matching
    // once enrolled directly at the device, so this is mainly relevant for
    // the mock and for any future terminal models that support remote
    // enrollment sync.
    Task PushMappingsAsync(IReadOnlyList<TerminalMapping> mappings, CancellationToken cancellationToken);
}

// One raw punch event read directly off the terminal, before we've resolved
// it against anything on the backend.
public record TerminalEvent(
    string EventId,
    string TerminalUserId,
    DateTime Timestamp,
    string VerifyMethod,   // e.g. "FINGERPRINT" — matches the backend's BiometricSyncRequest.verifyMethod
    string EventType       // "CLOCK_IN" or "CLOCK_OUT"
);

public record TerminalMapping(string TerminalUserId, long EmployeeId);