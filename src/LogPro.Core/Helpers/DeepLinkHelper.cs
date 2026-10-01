using System.Globalization;
using System.Text.RegularExpressions;
using LogPro.Models;

namespace LogPro.Helpers;

/// <summary>Validation shared by the editor and device boundary. Never executes a link.</summary>
public static class DeepLinkHelper
{
    public const int MaxUriLength = 8192;
    public const string PolicyWarning = "This offline build supports custom-scheme and intent: links only. HTTPS Android App Links are blocked by its network policy.";
    private static readonly HashSet<string> BlockedSchemes = new(StringComparer.OrdinalIgnoreCase)
        { "http", "https", "ftp", "ftps", "ws", "wss", "file", "data", "mailto", "javascript", "android-app" };
    private static readonly Regex Scheme = new(@"^[a-zA-Z][a-zA-Z0-9+.-]*$", RegexOptions.CultureInvariant);

    public static bool IsPackageId(string value) => value.Length <= 255 && Regex.IsMatch(value,
        @"^[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)+$", RegexOptions.CultureInvariant);

    public static bool TryValidateOptions(string uri, DeepLinkOptions options, out string error)
    {
        error = string.Empty;
        if (options.PackageId.Length == 0) return true;
        if (!IsPackageId(options.PackageId)) { error = "Enter a valid Android package identifier or leave the target empty."; return false; }
        if (!uri.StartsWith("intent:", StringComparison.OrdinalIgnoreCase)) return true;
        var package = Regex.Match(uri, @";package=([^;]+)").Groups[1].Value;
        var component = Regex.Match(uri, @";component=([^;]+)").Groups[1].Value;
        if ((package.Length > 0 && Uri.UnescapeDataString(package) != options.PackageId) ||
            (component.Length > 0 && !Uri.UnescapeDataString(component).StartsWith(options.PackageId + "/", StringComparison.Ordinal)))
        { error = "The selected package conflicts with the package or component inside the intent URI."; return false; }
        return true;
    }

    public static bool TryValidate(string? value, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(value)) { error = "Enter a custom-scheme link or Android intent URI."; return false; }
        var text = value.Trim();
        if (text.Length > MaxUriLength || value.Any(char.IsControl))
        { error = "Links must be at most 8192 characters and contain no control characters."; return false; }
        if (text.Contains('`') || text.Contains("$(", StringComparison.Ordinal))
        { error = "Shell substitutions are not allowed in links. Percent-encode literal reserved characters."; return false; }
        if (Regex.IsMatch(text, @"%(?![0-9a-fA-F]{2})")) { error = "Invalid percent encoding in the link."; return false; }
        var colon = text.IndexOf(':');
        if (colon < 1 || !Scheme.IsMatch(text[..colon])) { error = "The link needs a valid URI scheme."; return false; }
        var scheme = text[..colon];
        if (BlockedSchemes.Contains(scheme)) { error = PolicyWarning; return false; }
        if (Regex.IsMatch(text, @"(?i)(?:https?|ftps?|wss?)://")) { error = PolicyWarning; return false; }
        if (!scheme.Equals("intent", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(text, UriKind.Absolute, out _)) { error = "The custom-scheme URI is malformed."; return false; }
            return true;
        }

        // Android uses the last fragment and decodes field values before rebuilding data.
        var fragment = text.LastIndexOf('#');
        if (fragment < 0 || !text[fragment..].StartsWith("#Intent;", StringComparison.Ordinal) ||
            !text.EndsWith(";end", StringComparison.Ordinal))
        { error = "Intent URIs must end with #Intent;…;end."; return false; }
        var hasScheme = false;
        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in text[(fragment + 8)..^3].Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = field.IndexOf('=');
            if (equals < 1) { error = "Malformed intent field or unsupported selector. Use a single intent."; return false; }
            var key = field[..equals];
            string decoded;
            try { decoded = Uri.UnescapeDataString(field[(equals + 1)..]); }
            catch (UriFormatException) { error = "Invalid percent encoding in an intent field."; return false; }
            if (decoded.Any(char.IsControl) || Regex.IsMatch(decoded, @"(?i)(?:https?|ftps?|wss?)://"))
            { error = PolicyWarning; return false; }
            if (key != "category" && !fields.Add(key)) { error = "Duplicate intent fields are ambiguous."; return false; }
            switch (key)
            {
                case "scheme":
                    if (!Scheme.IsMatch(decoded) || BlockedSchemes.Contains(decoded) || decoded.Equals("intent", StringComparison.OrdinalIgnoreCase))
                    { error = PolicyWarning; return false; }
                    hasScheme = true;
                    break;
                case "package":
                    if (!IsPackageId(decoded)) { error = "Invalid intent package identifier."; return false; }
                    break;
                case "component":
                    if (!Regex.IsMatch(decoded, @"^[A-Za-z_][A-Za-z0-9_.]*/[A-Za-z_.][A-Za-z0-9_.$]*$"))
                    { error = "Invalid intent component."; return false; }
                    break;
                case "launchFlags":
                    var validFlags = decoded.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                        ? uint.TryParse(decoded[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out _)
                        : int.TryParse(decoded, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
                    if (!validFlags) { error = "Invalid intent launch flags."; return false; }
                    break;
                case "action":
                case "category":
                case "type":
                case "identifier":
                case "sourceBounds":
                    if (decoded.Length == 0) { error = "Intent fields cannot be empty."; return false; }
                    break;
                default:
                    if (!Regex.IsMatch(key, @"^[SBbcdfils]\.[^=;\s]+$"))
                    { error = "Unsupported intent field. Use Android scalar extras such as S.name=value."; return false; }
                    if (!ValidExtra(key[0], decoded)) { error = "An intent extra has an invalid value for its declared type."; return false; }
                    break;
            }
        }
        if (text[7..fragment].Length > 0 && !hasScheme)
        { error = "An intent destination needs an explicit custom scheme."; return false; }
        return true;
    }

    private static bool ValidExtra(char type, string value) => type switch
    {
        'S' => true,
        'B' => bool.TryParse(value, out _),
        'b' => sbyte.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
        'c' => value.Length == 1,
        'd' => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d),
        'f' => float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) && float.IsFinite(f),
        'i' => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
        'l' => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
        's' => short.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
        _ => false
    };

    public static string SafeLabel(string value)
    {
        var colon = value.IndexOf(':');
        return colon > 0 && Scheme.IsMatch(value[..colon]) ? value[..colon].ToLowerInvariant() + ":[destination hidden]" : "[link hidden]";
    }

    public static string Preview(string value, bool reveal)
    {
        if (!TryValidate(value, out var error)) return error;
        var text = value.Trim();
        if (!text.StartsWith("intent:", StringComparison.OrdinalIgnoreCase))
            return $"Scheme: {text[..text.IndexOf(':')]} • Action: android.intent.action.VIEW\nDestination: {(reveal ? text : SafeLabel(text))}";
        var index = text.LastIndexOf("#Intent;", StringComparison.Ordinal);
        var fields = text[(index + 8)..^3].Split(';', StringSplitOptions.RemoveEmptyEntries);
        return "Android intent\nDestination: " + (reveal ? text[..index] : SafeLabel(text)) + "\n" +
            string.Join("\n", fields.Select(f =>
            {
                var split = f.IndexOf('=');
                return f[..split] + "=" + (reveal || f.StartsWith("scheme=", StringComparison.Ordinal) ? Uri.UnescapeDataString(f[(split + 1)..]) : "[hidden]");
            }));
    }

    internal static DeepLinkResult ParseResult(ToolLauncherResult result)
    {
        // URI payloads in the Starting line must never be interpreted as status keywords.
        var text = string.Join("\n", (result.Output + "\n" + result.Error).Split('\n')
            .Where(line => !line.TrimStart().StartsWith("Starting:", StringComparison.OrdinalIgnoreCase)));
        if (Regex.IsMatch(text, @"(?im)^Process cancelled\.\s*$")) return new(DeepLinkOutcome.Cancelled, "Launch cancelled. An already delivered intent cannot be undone.");
        if (Regex.IsMatch(text, @"(?im)^Status:\s*timeout\s*$|^(?:Process|Device queue) timed out\.\s*$"))
            return new(DeepLinkOutcome.TimedOut, "Launch timed out. The app may have opened; verify the device before retrying.");
        if (!result.Success || Regex.IsMatch(text, @"(?im)^\s*(?:Error(?:\s+type\s+\d+)?\s*:|Exception|SecurityException|java\.[\w.]*Exception)"))
        {
            var message = text.Contains("unable to resolve", StringComparison.OrdinalIgnoreCase) ? "No installed activity handles this link. Check the scheme and target package." :
                text.Contains("permission", StringComparison.OrdinalIgnoreCase) || text.Contains("SecurityException", StringComparison.OrdinalIgnoreCase) ? "Android denied access to the target activity. Check exported status and permissions." :
                text.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) ? "Accept the USB debugging authorization prompt on the device." :
                text.Contains("offline", StringComparison.OrdinalIgnoreCase) || text.Contains("not found", StringComparison.OrdinalIgnoreCase) ? "Device unavailable. Reconnect and try again." :
                text.Contains("unknown", StringComparison.OrdinalIgnoreCase) ? "This Android version does not support the requested command or option." :
                "Android could not launch the link. Check the installed app, intent format, and device state.";
            return new(DeepLinkOutcome.Failed, message);
        }
        if (!Regex.IsMatch(text, @"(?im)^Status:\s*ok\s*$"))
            return new(DeepLinkOutcome.Failed, "Android did not confirm a successful launch.");
        var activity = Regex.Match(text, @"(?m)^Activity:\s*([A-Za-z0-9_.$]+/[A-Za-z0-9_.$]+)\s*$").Groups[1].Value;
        int? Time(string name) => int.TryParse(Regex.Match(text, $@"(?m)^{name}:\s*(\d+)\s*$").Groups[1].Value,
            NumberStyles.None, CultureInfo.InvariantCulture, out var time) ? time : null;
        var outcome = text.Contains("delivered to", StringComparison.OrdinalIgnoreCase) ? DeepLinkOutcome.Delivered :
            text.Contains("brought to the front", StringComparison.OrdinalIgnoreCase) ? DeepLinkOutcome.TaskBroughtForward :
            text.Contains("Warning: Activity not started", StringComparison.OrdinalIgnoreCase) ? DeepLinkOutcome.Deferred : DeepLinkOutcome.Launched;
        return new(outcome, outcome switch
        {
            DeepLinkOutcome.Delivered => "Intent delivered to an already running activity.",
            DeepLinkOutcome.TaskBroughtForward => "Existing task brought to the foreground.",
            DeepLinkOutcome.Deferred => "Android deferred this launch; inspect the device.",
            _ => "Android confirmed the activity launch. Verify the destination screen on the device."
        }, activity, Time("TotalTime"), Time("WaitTime"));
    }
}
