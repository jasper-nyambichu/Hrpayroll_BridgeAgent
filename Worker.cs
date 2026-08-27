using WorkerService1.OfflineQueue;
using WorkerService1.Sync;
using WorkerService1.Terminal;

namespace WorkerService1;

// The agent's main loop. On each tick: poll the (mock, for now) terminal for
// new punches, enqueue them locally, then attempt to sync whatever's
// pending in the queue to the backend. Terminal reads and backend syncs are
// deliberately decoupled through the queue — a backend outage never blocks
// reading new punches off the terminal.
public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly ITerminalAdapter _terminalAdapter;
    private readonly OfflineQueueStore _queueStore;
    private readonly BackendApiClient _apiClient;

    public Worker(
        ILogger<Worker> logger,
        ITerminalAdapter terminalAdapter,
        OfflineQueueStore queueStore,
        BackendApiClient apiClient)
    {
        _logger = logger;
        _terminalAdapter = terminalAdapter;
        _queueStore = queueStore;
        _apiClient = apiClient;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _queueStore.Initialize();
        await _terminalAdapter.ConnectAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            await PollTerminalAsync(stoppingToken);
            await SyncPendingAsync(stoppingToken);

            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        }
    }

    private async Task PollTerminalAsync(CancellationToken stoppingToken)
    {
        var events = await _terminalAdapter.PollEventsAsync(stoppingToken);

        foreach (var evt in events)
        {
            _queueStore.Enqueue(new QueuedEvent
            {
                EventId = evt.EventId,
                TerminalUserId = evt.TerminalUserId,
                Timestamp = evt.Timestamp,
                VerifyMethod = evt.VerifyMethod,
                EventType = evt.EventType,
            });

            _logger.LogInformation("Queued event {EventId} ({EventType})", evt.EventId, evt.EventType);
        }
    }

    private async Task SyncPendingAsync(CancellationToken stoppingToken)
    {
        var pending = _queueStore.GetPending();

        foreach (var queued in pending)
        {
            var request = new SyncEventRequest(
                EventId: queued.EventId,
                TerminalUserId: queued.TerminalUserId,
                DeviceSerial: "MOCK-DEVICE-001", // TODO: pull from AgentSettings instead of hardcoding
                Timestamp: queued.Timestamp,
                VerifyMethod: queued.VerifyMethod,
                EventType: queued.EventType
            );

            try
            {
                var result = await _apiClient.SyncEventAsync(request, stoppingToken);

                if (result.Succeeded)
                {
                    _queueStore.MarkSynced(queued.Id);
                    _logger.LogInformation("Synced event {EventId}", queued.EventId);
                }
                else
                {
                    _queueStore.MarkFailedAttempt(queued.Id, result.Error ?? "unknown error");
                    _logger.LogWarning("Sync failed for {EventId}, will retry: {Error}", queued.EventId, result.Error);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // App is shutting down mid-request — not a real sync failure,
                // just stop trying and let ExecuteAsync's loop exit cleanly.
                _logger.LogInformation("Sync loop cancelled due to shutdown.");
                return;
            }
            catch (Exception ex)
            {
                // Any other failure (network down, DNS failure, timeout, etc.)
                // — log it and leave the event queued for the next tick rather
                // than crashing the whole agent.
                _queueStore.MarkFailedAttempt(queued.Id, ex.Message);
                _logger.LogWarning(ex, "Unexpected error syncing {EventId}, will retry", queued.EventId);
            }
        }
    }
}