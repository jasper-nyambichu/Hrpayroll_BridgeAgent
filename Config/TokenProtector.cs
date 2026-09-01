using System.Security.Cryptography;
using System.Text;

namespace WorkerService1.Config;

// Wraps Windows DPAPI so the device token never sits as plaintext in
// appsettings.json. Encryption is tied to the current Windows user
// account — the encrypted value is unreadable if copied to another
// machine or read by another user, without needing to manage a
// separate encryption key ourselves.
public static class TokenProtector
{
    // A prefix so we can tell at a glance whether a config value is
    // already encrypted or still plaintext (e.g. on first run, before
    // the token has ever been protected).
    private const string EncryptedPrefix = "DPAPI:";

    public static string Protect(string plaintext)
    {
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var encrypted = ProtectedData.Protect(bytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
        return EncryptedPrefix + Convert.ToBase64String(encrypted);
    }

    public static string Unprotect(string storedValue)
    {
        if (!storedValue.StartsWith(EncryptedPrefix))
        {
            // Not yet encrypted (e.g. first run with a plaintext value
            // pasted in manually) — return as-is so the app still works;
            // the caller is responsible for re-saving it encrypted.
            return storedValue;
        }

        var base64 = storedValue.Substring(EncryptedPrefix.Length);
        var encrypted = Convert.FromBase64String(base64);
        var bytes = ProtectedData.Unprotect(encrypted, optionalEntropy: null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(bytes);
    }

    public static bool IsProtected(string storedValue) => storedValue.StartsWith(EncryptedPrefix);
}