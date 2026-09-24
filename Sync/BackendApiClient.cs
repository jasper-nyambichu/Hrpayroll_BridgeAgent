using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WorkerService1.Config;

namespace WorkerService1.Sync;

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
        httpClient.DefaultRequestHeaders.Add("X-Device-Serial", _settings.DeviceSerial.Trim());
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

    public async Task<SyncResult> RegisterEnrollmentAsync(long employeeId, string terminalUserId, CancellationToken cancellationToken)
    {
        var request = new EnrollmentRequest(employeeId, _settings.DeviceSerial, terminalUserId);

        var response = await _httpClient.PostAsJsonAsync("/attendance/enrollment", request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Enrollment mapping push failed for employeeId={EmployeeId}: {StatusCode} {Body}",
                employeeId, response.StatusCode, body);

            return SyncResult.Failure(body);
        }

        _logger.LogInformation("Enrollment mapping registered on backend for employeeId={EmployeeId}", employeeId);
        return SyncResult.Ok();
    }
}

public class LocalDateTimeJsonConverter : JsonConverter<DateTime>
{
    private const string Format = "yyyy-MM-ddTHH:mm:ss.ffffff";

    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => DateTime.Parse(reader.GetString()!, System.Globalization.CultureInfo.InvariantCulture);

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString(Format, System.Globalization.CultureInfo.InvariantCulture));
}

public record SyncEventRequest(
    string EventId,
    string TerminalUserId,
    string DeviceSerial,
    [property: JsonConverter(typeof(LocalDateTimeJsonConverter))] DateTime Timestamp,
    string VerifyMethod,
    string EventType
);

public record EnrollmentRequest(
    long EmployeeId,
    string DeviceSerial,
    string TerminalUserId
);

public record ApiResponse<T>(bool Success, string? Message, T? Data, DateTime Timestamp);

public record SyncResult(bool Succeeded, string? Error)
{
    public static SyncResult Ok() => new(true, null);
    public static SyncResult Failure(string error) => new(false, error);
}