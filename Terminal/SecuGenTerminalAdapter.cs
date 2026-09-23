using System.Text.Json;
using Microsoft.Extensions.Logging;
using SecuGen.FDxSDKPro.Windows;

namespace WorkerService1.Terminal;

// Real hardware adapter for the SecuGen Hamster Plus (HSDU03P), using the
// FDx SDK Pro for Windows managed wrapper. Swap MockTerminalAdapter for
// this one line in Program.cs — nothing else in the app changes.
//
// IMPORTANT: enrolled fingerprint templates are stored locally in
// enrolled-templates.json (next to the running exe), keyed by
// terminalUserId. Run enrollment once per employee (via --enroll) before
// attendance capture can identify that person.
public class SecuGenTerminalAdapter : ITerminalAdapter, IDisposable
{
    private readonly ILogger<SecuGenTerminalAdapter> _logger;
    private SGFingerPrintManager? _fpm;
    private int _imageWidth;
    private int _imageHeight;

    private const int TemplateSize = 400;
    private const int MinCaptureQuality = 50;
    private const int CaptureTimeoutMs = 800;
    private const int EnrollTimeoutMs = 5000;

    private static readonly string TemplateStorePath =
        Path.Combine(AppContext.BaseDirectory, "enrolled-templates.json");

    private Dictionary<string, byte[]> _templates = new();
    private readonly Dictionary<string, bool> _lastWasClockOut = new();

    public SecuGenTerminalAdapter(ILogger<SecuGenTerminalAdapter> logger)
    {
        _logger = logger;
    }

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        _fpm = new SGFingerPrintManager();

        var initError = _fpm.Init(SGFPMDeviceName.DEV_AUTO);
        var openError = _fpm.OpenDevice((int)SGFPMPortAddr.USB_AUTO_DETECT);

        if (openError != (int)SGFPMError.ERROR_NONE)
        {
            _logger.LogError("SecuGen device failed to open. Init error={InitError}, Open error={OpenError}", initError, openError);
            throw new InvalidOperationException($"SecuGen device open failed (error {openError}). Check the reader is plugged in and the driver is installed.");
        }

        var info = new SGFPMDeviceInfoParam();
        var infoError = _fpm.GetDeviceInfo(info);
        if (infoError == (int)SGFPMError.ERROR_NONE)
        {
            _imageWidth = info.ImageWidth;
            _imageHeight = info.ImageHeight;
            _logger.LogInformation("SecuGen device connected. Serial={Serial}, {Width}x{Height}",
                System.Text.Encoding.ASCII.GetString(info.DeviceSN).TrimEnd('\0'), _imageWidth, _imageHeight);
        }
        else
        {
            _logger.LogWarning("SecuGen device opened but GetDeviceInfo failed (error {Error}); using fallback image size.", infoError);
            _imageWidth = 260;
            _imageHeight = 300;
        }

        LoadTemplates();

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TerminalEvent>> PollEventsAsync(CancellationToken cancellationToken)
    {
        if (_fpm is null)
            throw new InvalidOperationException("Device not connected. Call ConnectAsync first.");

        if (_templates.Count == 0)
            return Task.FromResult<IReadOnlyList<TerminalEvent>>(Array.Empty<TerminalEvent>());

        var template = TryCapture(CaptureTimeoutMs, MinCaptureQuality);
        if (template is null)
            return Task.FromResult<IReadOnlyList<TerminalEvent>>(Array.Empty<TerminalEvent>());

        var matchedUserId = IdentifyTemplate(template);
        if (matchedUserId is null)
        {
            _logger.LogInformation("Fingerprint captured but did not match any enrolled employee.");
            return Task.FromResult<IReadOnlyList<TerminalEvent>>(Array.Empty<TerminalEvent>());
        }

        var wasClockOut = _lastWasClockOut.GetValueOrDefault(matchedUserId, false);
        var eventType = wasClockOut ? "CLOCK_IN" : "CLOCK_OUT";
        _lastWasClockOut[matchedUserId] = !wasClockOut;

        var evt = new TerminalEvent(
            EventId: $"secugen-evt-{Guid.NewGuid():N}",
            TerminalUserId: matchedUserId,
            Timestamp: DateTime.UtcNow,
            VerifyMethod: "FINGERPRINT",
            EventType: eventType
        );

        _logger.LogInformation("Matched fingerprint: {EventType} for terminalUserId={TerminalUserId}", eventType, matchedUserId);

        return Task.FromResult<IReadOnlyList<TerminalEvent>>(new List<TerminalEvent> { evt });
    }

    public async Task<bool> EnrollAsync(string terminalUserId, CancellationToken cancellationToken)
    {
        if (_fpm is null)
            throw new InvalidOperationException("Device not connected. Call ConnectAsync first.");

        _logger.LogInformation("Place finger on the reader to enroll terminalUserId={TerminalUserId}...", terminalUserId);

        var template = TryCapture(EnrollTimeoutMs, MinCaptureQuality, retryUntilTimeout: true);
        if (template is null)
        {
            _logger.LogWarning("Enrollment failed: no acceptable capture within {Timeout}ms.", EnrollTimeoutMs);
            return false;
        }

        _templates[terminalUserId] = template;
        SaveTemplates();

        _logger.LogInformation("Enrollment successful for terminalUserId={TerminalUserId}.", terminalUserId);
        return await Task.FromResult(true);
    }

    public Task PushMappingsAsync(IReadOnlyList<TerminalMapping> mappings, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Received {Count} mapping(s) — no local action needed (backend resolves employeeId).", mappings.Count);
        return Task.CompletedTask;
    }

    private byte[]? TryCapture(int timeoutMs, int minQuality, bool retryUntilTimeout = false)
    {
        if (_fpm is null) return null;

        var image = new byte[_imageWidth * _imageHeight];
        var template = new byte[TemplateSize];
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        do
        {
            var captureError = _fpm.GetImage(image);
            if (captureError == (int)SGFPMError.ERROR_NONE)
            {
                var quality = 0;
                _fpm.GetImageQuality(_imageWidth, _imageHeight, image, ref quality);

                if (quality >= minQuality)
                {
                    var templateError = _fpm.CreateTemplate(null, image, template);
                    if (templateError == (int)SGFPMError.ERROR_NONE)
                        return template;

                    _logger.LogWarning("CreateTemplate failed with error {Error}.", templateError);
                }
            }

            if (!retryUntilTimeout)
                break;

        } while (stopwatch.ElapsedMilliseconds < timeoutMs);

        return null;
    }

    private string? IdentifyTemplate(byte[] capturedTemplate)
    {
        if (_fpm is null) return null;

        foreach (var (terminalUserId, storedTemplate) in _templates)
        {
            var matched = false;
            var matchError = _fpm.MatchTemplate(storedTemplate, capturedTemplate, (SGFPMSecurityLevel)3, ref matched);

            if (matchError == (int)SGFPMError.ERROR_NONE && matched)
                return terminalUserId;
        }

        return null;
    }

    private void LoadTemplates()
    {
        if (!File.Exists(TemplateStorePath))
        {
            _templates = new Dictionary<string, byte[]>();
            return;
        }

        try
        {
            var json = File.ReadAllText(TemplateStorePath);
            var stored = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
            _templates = stored.ToDictionary(kv => kv.Key, kv => Convert.FromBase64String(kv.Value));
            _logger.LogInformation("Loaded {Count} enrolled template(s) from {Path}.", _templates.Count, TemplateStorePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load enrolled templates from {Path}; starting empty.", TemplateStorePath);
            _templates = new Dictionary<string, byte[]>();
        }
    }

    private void SaveTemplates()
    {
        var toStore = _templates.ToDictionary(kv => kv.Key, kv => Convert.ToBase64String(kv.Value));
        var json = JsonSerializer.Serialize(toStore, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(TemplateStorePath, json);
    }

    public void Dispose()
    {
        _fpm?.CloseDevice();
    }
}