namespace WorkerService1.Config;

// Strongly-typed binding target for the "Agent" section of appsettings.json.
// DeviceToken is the raw plaintext token issued once by
// POST /attendance/devices (DeviceRegistrationResponse.plaintextToken) —
// the backend only ever stores its hash, so this value lives only here,
// on this branch machine.
public class AgentSettings
{
    public string BackendBaseUrl { get; set; } = string.Empty;
    public string DeviceSerial { get; set; } = string.Empty;
    public string DeviceToken { get; set; } = string.Empty;

    public int PollIntervalSeconds { get; set; } = 15;
    public int SyncIntervalSeconds { get; set; } = 10;
}