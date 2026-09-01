using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using WorkerService1.Config;
using System.Net.Http;

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

        // Migrate a plaintext token to an encrypted one on first run, so
        // appsettings.json never has to be hand-edited with an encrypted
        // blob — you just paste the real token once and it self-protects.
        string realToken;
        if (!string.IsNullOrEmpty(_settings.DeviceToken) && !TokenProtector.IsProtected(_settings.DeviceToken))
        {
            realToken = _settings.DeviceToken;
            var encrypted = TokenProtector.Protect(realToken);
            SettingsFileWriter.UpdateDeviceToken(encrypted);
            _logger.LogInformation("Device token encrypted at rest for the first time.");
        }
        else
        {
            realToken = TokenProtector.Unprotect(_settings.DeviceToken ?? string.Empty);
        }

        httpClient.BaseAddress = new Uri(_settings.BackendBaseUrl);
        httpClient.DefaultRequestHeaders.Add("X-Device-Serial", _settings.DeviceSerial);
        httpClient.DefaultRequestHeaders.Add("X-Device-Token", realToken);
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        _httpClient = httpClient;
    }

    public async Task<SyncResult> SyncEventAsync(SyncEventRequest request, CancellationToken cancellationToken)
    {
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        jsonOptions.Converters.Add(new DateTimeNoOffsetConverter());

        var response = await _httpClient.PostAsJsonAsync("/attendance/biometric/sync", request, jsonOptions, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Sync failed for eventId={EventId}: {StatusCode} {Body}",
                request.EventId, response.StatusCode, body);

            return SyncResult.Failure(body);
        }

        var envelope = JsonSerializer.Deserialize<ApiResponse<JsonElement>>(body, jsonOptions);

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

// Java's LocalDateTime has no timezone concept and rejects any offset
// suffix .NET's default DateTime serializer includes (e.g. "+03:00").
// This formats timestamps as plain "yyyy-MM-ddTHH:mm:ss.fffffff" instead,
// matching what Jackson's LocalDateTimeDeserializer actually expects.
public class DateTimeNoOffsetConverter : System.Text.Json.Serialization.JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => DateTime.Parse(reader.GetString()!);

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString("yyyy-MM-ddTHH:mm:ss.fffffff"));
}