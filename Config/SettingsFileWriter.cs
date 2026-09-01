using System.Text.Json;
using System.Text.Json.Nodes;
using System.IO;


namespace WorkerService1.Config;

// Rewrites just the Agent:DeviceToken value inside appsettings.json,
// preserving everything else in the file untouched. Used once, the first
// time a plaintext token gets encrypted at rest.
public static class SettingsFileWriter
{
    public static void UpdateDeviceToken(string encryptedToken)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(path))
        {
            return;
        }

        var json = File.ReadAllText(path);
        var node = JsonNode.Parse(json);
        if (node is null)
        {
            return;
        }

        node["Agent"]!["DeviceToken"] = encryptedToken;

        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(path, node.ToJsonString(options));
    }
}