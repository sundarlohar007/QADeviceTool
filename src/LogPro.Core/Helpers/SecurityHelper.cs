using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace LogPro.Helpers;

public static class SecurityHelper
{
    /// <summary>
    /// LogPro's privacy boundary is deliberately not user-toggleable. Features that can
    /// initiate network traffic must be rejected by the engine, not merely hidden in UI.
    /// </summary>
    public static bool OfflineOnly => true;

    private static readonly Regex PackageNamePattern = new(
        @"^[a-zA-Z_][a-zA-Z0-9_]*(\.[a-zA-Z_][a-zA-Z0-9_]*)+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex NetworkInterfacePattern = new(
        @"^[a-zA-Z0-9_.-]{1,32}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex DeviceSelectorPattern = new(
        @"^[a-zA-Z0-9._-]{1,128}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex DeviceArgumentPattern = new(
        @"^[a-zA-Z0-9._:/\[\]\- ()]{1,512}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex NetworkCommandPattern = new(
        @"(?i)(?:^|\s)(?:connect|pair|disconnect|tcpip|mdns|forward|curl|wget|nc|netcat|socat|telnet|ftp|tftp|ping|traceroute|nslookup|dig)(?:\s|$)|(?:--host|-H|--port|-P|--network)\b|https?://|wss?://|ftps?://",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string HashSerial(string serial)
    {
        if (string.IsNullOrEmpty(serial))
            return "unknown";

        var bytes = Encoding.UTF8.GetBytes(serial);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    public static bool IsValidPackageName(string? packageName)
        => !string.IsNullOrWhiteSpace(packageName) && packageName.Length <= 255 && PackageNamePattern.IsMatch(packageName);

    /// <summary>
    /// Device selectors accepted by the offline transport. Network ADB selectors such as
    /// 192.0.2.10:5555 are deliberately rejected by this policy.
    /// </summary>
    public static bool IsValidOfflineDeviceSelector(string? selector)
        => !string.IsNullOrWhiteSpace(selector) && DeviceSelectorPattern.IsMatch(selector);

    public static bool IsValidNetworkInterface(string? networkInterface)
        => !string.IsNullOrWhiteSpace(networkInterface) && NetworkInterfacePattern.IsMatch(networkInterface);

    public static bool IsSafeDeviceArgument(string? value)
        => !string.IsNullOrWhiteSpace(value) && DeviceArgumentPattern.IsMatch(value);

    public static bool IsNetworkEndpoint(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var candidate = value.Trim();
        if (candidate.Contains('\r') || candidate.Contains('\n') || candidate.Contains('"') || candidate.Contains('\''))
            return false;

        return Uri.TryCreate($"tcp://{candidate}", UriKind.Absolute, out var uri)
            && !string.IsNullOrWhiteSpace(uri.Host)
            && uri.Port is > 0 and <= 65535;
    }

    /// <summary>Only non-network custom URI schemes are allowed in offline mode.</summary>
    public static bool IsOfflineSafeUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var uri = value.Trim();
        if (uri.Contains('\r') || uri.Contains('\n')) return false;
        if (Regex.IsMatch(uri, @"(?i)(?:https?|ftp|ftps|ws|wss|file|data|mailto):")) return false;
        return Regex.IsMatch(uri, @"^[a-zA-Z][a-zA-Z0-9+.-]*:", RegexOptions.CultureInvariant);
    }

    /// <summary>Returns true for command text that can initiate host/device network traffic.</summary>
    public static bool IsNetworkCapableCommand(string? command)
        => !string.IsNullOrWhiteSpace(command) && NetworkCommandPattern.IsMatch(command);

    /// <summary>
    /// Small read-only command surface for the interactive terminal. Arbitrary device
    /// shell is intentionally unavailable in offline mode because no command allowlist
    /// can safely reason about every vendor binary or shell escape sequence.
    /// </summary>
    public static bool IsOfflineSafeReadOnlyCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        if (command.Any(c => c is '\r' or '\n' or ';' or '|' or '&' or '`' or '$' or '<' or '>')) return false;
        if (IsNetworkCapableCommand(command)) return false;

        var normalized = command.Trim();
        return new[]
        {
            "shell getprop", "shell dumpsys", "shell ps", "shell top", "shell cat",
            "shell ls", "shell pm list", "shell wm size", "shell settings get",
            "logcat -d", "logcat -b", "version", "devices"
        }.Any(prefix => normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Redacts device identifiers, credentials, URLs, network addresses, local usernames,
    /// and package-like identifiers before text reaches logs or an issue bundle.
    /// </summary>
    public static string RedactSensitiveText(string? text, bool redactIdentifiers = true)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;

        var result = text;
        result = Regex.Replace(result, @"(?i)\b(authorization\s*[:=]\s*bearer\s+|bearer\s+)[^\s,;]+", "$1[REDACTED]");
        result = Regex.Replace(result, @"(?i)\b(password|passwd|token|secret|api[_-]?key|pairing[_ -]?code)\s*[:=]\s*[^\s,;]+", "$1=[REDACTED]");
        result = Regex.Replace(result, @"(?i)([?&](?:token|code|key|password|secret|sig|signature)=)[^&\s]+", "$1[REDACTED]");
        result = Regex.Replace(result, @"(?i)\b(?:https?|ftp|ftps|ws|wss)://[^\s<>']+", "[URL]");
        result = Regex.Replace(result, @"\b(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{32})\b", "[DEVICE_ID]");
        result = Regex.Replace(result, @"\b(?:[A-Fa-f0-9]{2}[:-]){5}[A-Fa-f0-9]{2}\b", "[MAC]");
        result = Regex.Replace(result, @"\b(?:\d{1,3}\.){3}\d{1,3}\b", "[IP]");
        result = Regex.Replace(result, @"(?i)[A-Z]:\\Users\\[^\\/\s]+", "[LOCAL_PATH]");
        result = Regex.Replace(result, @"(?i)(?:/Users/|/home/)[^/\s]+", "[LOCAL_PATH]");

        if (redactIdentifiers)
        {
            result = Regex.Replace(result, @"(?<![A-Za-z0-9_])(?:[A-Za-z_][A-Za-z0-9_]*\.){2,}[A-Za-z_][A-Za-z0-9_]*", "[PACKAGE]");
            result = Regex.Replace(result, @"(?i)\b(?:RF|HT)[A-Z0-9]{6,16}\b", "[SERIAL]");
        }

        return result;
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
