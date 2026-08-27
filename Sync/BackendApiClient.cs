using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WorkerService1.Config;

namespace WorkerService1.Sync;

// Thin typed wrapper around the two device-authenticated endpoints the
// bridge-agent needs. Mirrors the backend's ApiResponse<T> envelope and
// DTO shapes exactly — see BiometricSyncController /
// BiometricSyncRequest / DeviceMappingItemResponse on the backend.
public class BackendApiClient
{
    private readonly HttpClient _httpClient;
    private readonly AgentSettings _settings;
    private readonly ILogger<BackendApiClient> _logger;

    public BackendApiClient(HttpClient httpClient, IOptions<AgentSettings> settings, ILogger<BackendApiClient> logger)
    {
        _settings = settings.Value;
        _logger = logger;

        httpClient.BaseAddress = new Uri(_settings.BackendBaseUrl);
        httpClient.DefaultRequestHeaders.Add("X-Device-Serial", _settings.DeviceSerial);
        httpClient.DefaultRequestHeaders.Add("X-Device-Token", _settings.DeviceToken);
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        _httpClient = httpClient;
    }

    public async Task<SyncResult> SyncEventAsync(SyncEventRequest request, CancellationToken cancellationToken)
    {
        var response = await _httpClient.PostAsJsonAsync("/attendance/biometric/sync", request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Sync failed for eventId={EventId}: {StatusCode} {Body}",
                request.EventId, response.StatusCode, body);

            return SyncResult.Failure(body);
        }

        var envelope = JsonSerializer.Deserialize<ApiResponse<JsonElement>>(body,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        if (envelope is null || !envelope.Success)
        {
            _logger.LogWarning(
                "Backend reported failure for eventId={EventId}: {Message}",
                request.EventId, envelope?.Message);

            return SyncResult.Failure(envelope?.Message ?? "Unknown backend error");
        }

        return SyncResult.Ok();
    }
}

// Mirrors BiometricSyncRequest on the backend field-for-field.
public record SyncEventRequest(
    string EventId,
    string TerminalUserId,
    string DeviceSerial,
    DateTime Timestamp,
    string VerifyMethod,
    string EventType
);

// Mirrors ApiResponse<T> on the backend.
public record ApiResponse<T>(bool Success, string? Message, T? Data, DateTime Timestamp);

public record SyncResult(bool Succeeded, string? Error)
{
    public static SyncResult Ok() => new(true, null);
    public static SyncResult Failure(string error) => new(false, error);
}