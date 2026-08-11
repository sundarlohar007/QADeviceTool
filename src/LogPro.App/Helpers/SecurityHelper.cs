using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace LogPro.Helpers;

public static class SecurityHelper
{
    public static string HashSerial(string serial)
    {
        if (string.IsNullOrEmpty(serial))
            return "unknown";

        var bytes = Encoding.UTF8.GetBytes(serial);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash)[..16].ToLower();
    }

    /// <summary>True if the string looks like a hashed serial key (16 lowercase hex chars).</summary>
    public static bool IsHashedSerialKey(string key)
        => key.Length == 16 && key.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static string GetSafeSessionName(string? customName, string deviceHash, string platform)
    {
        if (!string.IsNullOrWhiteSpace(customName))
        {
            var sanitized = SanitizeFileName(customName);
            if (!string.IsNullOrEmpty(sanitized))
                return sanitized;
        }

        return $"{platform.ToLower()}_{deviceHash}";
    }

    public static string SanitizeFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return string.Empty;

        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new StringBuilder();

        foreach (var c in fileName)
        {
            // Block path separators and dot-dot traversal
            if (c == '/' || c == '\\' || c == ':')
                continue;
            if (!invalid.Contains(c))
                sanitized.Append(c);
        }

        var result = sanitized.ToString().Trim();
        // Collapse consecutive dots to prevent traversal
        while (result.Contains(".."))
            result = result.Replace("..", ".");
        if (result.Length > 50)
            result = result[..50];

        return result;
    }
}