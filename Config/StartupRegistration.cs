using Microsoft.Win32;

namespace WorkerService1.Config;

// Registers this app to launch automatically at Windows login, via the
// standard per-user Run key — no admin rights needed, no installer
// required. Idempotent: safe to call on every startup, only writes the
// registry if the entry is missing or points at a stale path (e.g. after
// the app was moved or rebuilt to a new location).
public static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "HrPayrollBridgeAgent";

    public static void EnsureRegistered()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            return;
        }

        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key is null)
        {
            return;
        }

        var currentValue = key.GetValue(AppName) as string;
        if (!string.Equals(currentValue, exePath, StringComparison.OrdinalIgnoreCase))
        {
            key.SetValue(AppName, exePath);
        }
    }

    public static void Unregister()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(AppName, throwOnMissingValue: false);
    }
}