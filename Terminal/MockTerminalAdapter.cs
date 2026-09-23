using Microsoft.Extensions.Logging;

namespace WorkerService1.Terminal;

// Simulates a ZKTeco standalone terminal so we can build and test the rest
// of the agent (offline queue, sync client, retry logic) with zero physical
// hardware. Generates a fake clock-in/clock-out event every ~15 seconds for
// a hardcoded terminal user, alternating event types, so the sync pipeline
// has something real to push.
public class MockTerminalAdapter : ITerminalAdapter
{
    private readonly ILogger<MockTerminalAdapter> _logger;
    private bool _nextIsClockOut;
    private int _eventCounter;

    public MockTerminalAdapter(ILogger<MockTerminalAdapter> logger)
    {
        _logger = logger;
    }

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("[MOCK] Simulated terminal connected.");
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TerminalEvent>> PollEventsAsync(CancellationToken cancellationToken)
    {
        _eventCounter++;

        var evt = new TerminalEvent(
            EventId: $"mock-evt-{_eventCounter}-{Guid.NewGuid():N}",
            TerminalUserId: "1001", // pretend this terminal user id is mapped to a real employee
            Timestamp: DateTime.UtcNow,
            VerifyMethod: "FINGERPRINT",
            EventType: _nextIsClockOut ? "CLOCK_OUT" : "CLOCK_IN"
        );

        _nextIsClockOut = !_nextIsClockOut;

        _logger.LogInformation(
            "[MOCK] Simulated punch: {EventType} for terminalUserId={TerminalUserId} at {Timestamp}",
            evt.EventType, evt.TerminalUserId, evt.Timestamp);

        return Task.FromResult<IReadOnlyList<TerminalEvent>>(new List<TerminalEvent> { evt });
    }

    public Task PushMappingsAsync(IReadOnlyList<TerminalMapping> mappings, CancellationToken cancellationToken)
    {
        _logger.LogInformation("[MOCK] Received {Count} mapping(s) to push to terminal (no-op).", mappings.Count);
        return Task.CompletedTask;
    }

    public Task<bool> EnrollAsync(string terminalUserId, CancellationToken cancellationToken)
    {
        _logger.LogInformation("[MOCK] Enrollment requested for terminalUserId={TerminalUserId} (no-op).", terminalUserId);
        return Task.FromResult(true);
    }
}