using WorkerService1.OfflineQueue;
using WorkerService1.Sync;
using WorkerService1.Terminal;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace WorkerService1;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly ITerminalAdapter _terminalAdapter;
    private readonly OfflineQueueStore _queueStore;
    private readonly AttendanceLogStore _attendanceLog;
    private readonly BackendApiClient _apiClient;

    public Worker(
        ILogger<Worker> logger,
        ITerminalAdapter terminalAdapter,
        OfflineQueueStore queueStore,
        AttendanceLogStore attendanceLog,
        BackendApiClient apiClient)
    {
        _logger = logger;
        _terminalAdapter = terminalAdapter;
        _queueStore = queueStore;
        _attendanceLog = attendanceLog;
        _apiClient = apiClient;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _queueStore.Initialize();

        _queueStore.Initialize();
        _attendanceLog.Initialize();

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

            // Recorded permanently here — this survives even after the
            // queue row above gets deleted on successful sync.
            _attendanceLog.RecordPunch(new AttendanceLogEntry
            {
                EventId = evt.EventId,
                TerminalUserId = evt.TerminalUserId,
                Timestamp = evt.Timestamp,
                EventType = evt.EventType,
                SyncStatus = "Pending",
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
                DeviceSerial: "MOCK-DEVICE-002", // TODO: pull from AgentSettings instead of hardcoding
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
                    _attendanceLog.UpdateStatus(queued.EventId, "Synced");
                    _logger.LogInformation("Synced event {EventId}", queued.EventId);
                }
                else
                {
                    _queueStore.MarkFailedAttempt(queued.Id, result.Error ?? "unknown error");
                    _attendanceLog.UpdateStatus(queued.EventId, "Failed", result.Error);
                    _logger.LogWarning("Sync failed for {EventId}, will retry: {Error}", queued.EventId, result.Error);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("Sync loop cancelled due to shutdown.");
                return;
            }
            catch (Exception ex)
            {
                _queueStore.MarkFailedAttempt(queued.Id, ex.Message);
                _attendanceLog.UpdateStatus(queued.EventId, "Failed", ex.Message);
                _logger.LogWarning(ex, "Unexpected error syncing {EventId}, will retry", queued.EventId);
            }
        }
    }
}