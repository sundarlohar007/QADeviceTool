using System.Text;
using System.Text.Json;
using LogPro.Helpers;
using LogPro.Models;

namespace LogPro.Services;

/// <summary>What goes into an exported issue bundle.</summary>
public sealed class IssueExportRequest
{
    public DeviceInfo? Device { get; init; }
    public string? Title { get; init; }
    public string? SessionLogFilePath { get; init; }
    public IReadOnlyList<string> Attachments { get; init; } = Array.Empty<string>(); // screenshot / perf json / recording
    public string OutputDirectory { get; init; } = Directory.GetCurrentDirectory();
}

public sealed class IssueBundle
{
    public string DirectoryPath { get; init; } = string.Empty;
    public string MarkdownPath { get; init; } = string.Empty;
    public IReadOnlyList<string> Files { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Issue-template export (privacy hard gate): writes a REDACTED evidence bundle and a
/// markdown template to disk — the tool never touches the network; the user attaches
/// the files manually in their tracker.
/// </summary>
public static class IssueExportService
{
    public static async Task<IssueBundle> ExportAsync(IssueExportRequest request)
    {
        if (!PathHelper.TryGetSafeLocalDirectory(request.OutputDirectory, out var outputDirectory))
            throw new ArgumentException("Issue bundle output must be on a local, non-reparse-point volume.", nameof(request));

        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var deviceHash = request.Device != null ? SecurityHelper.HashSerial(request.Device.Serial) : "nodevice";
        var bundleDir = Path.Combine(outputDirectory, $"issue_{deviceHash}_{stamp}");
        Directory.CreateDirectory(bundleDir);
        PathHelper.RestrictDirectoryAccess(bundleDir);

        var files = new List<string>();

        // 1. Redacted markdown template
        var mdPath = Path.Combine(bundleDir, "issue.md");
        await File.WriteAllTextAsync(mdPath, BuildMarkdown(request, deviceHash));
        files.Add(mdPath);

        // 2. Session log copy. It is redacted line-by-line; issue bundles never contain
        // the raw session log even when the caller selected a raw export elsewhere.
        if (request.SessionLogFilePath != null && File.Exists(request.SessionLogFilePath) &&
            PathHelper.IsSafeLocalPath(request.SessionLogFilePath))
        {
            var dest = Path.Combine(bundleDir, "session_log.txt");
            await RedactTextFileAsync(request.SessionLogFilePath, dest);
            files.Add(dest);
        }

        // 3. Attachments (screenshots, perf reports, recordings the user explicitly selected)
        foreach (var attachment in request.Attachments)
        {
            if (!File.Exists(attachment) || !PathHelper.IsSafeLocalPath(attachment)) continue;
            var name = SecurityHelper.SanitizeFileName(Path.GetFileName(attachment));
            if (string.IsNullOrWhiteSpace(name)) continue;
            var dest = Path.Combine(bundleDir, name);
            if (IsTextAttachment(attachment))
                await RedactTextFileAsync(attachment, dest);
            else
                File.Copy(attachment, dest, overwrite: true);
            files.Add(dest);
        }

        // 4. Manifest
        var manifestPath = Path.Combine(bundleDir, "bundle-info.json");
        var manifest = JsonSerializer.Serialize(new
        {
            device = deviceHash,
            generatedUtc = DateTime.UtcNow,
            files = files.Select(Path.GetFileName).ToArray()
        }, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(manifestPath, manifest);
        files.Add(manifestPath);

        return new IssueBundle { DirectoryPath = bundleDir, MarkdownPath = mdPath, Files = files };
    }

    private static string BuildMarkdown(IssueExportRequest request, string deviceHash)
    {
        var d = request.Device;
        var sb = new StringBuilder();
        sb.AppendLine("### ").AppendLine(SecurityHelper.RedactSensitiveText(request.Title ?? "[Summarize the issue]"));
        sb.AppendLine();
        sb.AppendLine("**Environment**");
        sb.AppendLine("- Device (hashed): ").Append(deviceHash).AppendLine();
        if (d != null)
        {
            sb.AppendLine("- Platform: ").Append(d.Platform).AppendLine();
            sb.AppendLine("- Model: ").AppendLine(SecurityHelper.RedactSensitiveText(d.Model));
            sb.AppendLine("- OS: ").AppendLine(SecurityHelper.RedactSensitiveText(d.OsVersion));
        }
        sb.AppendLine();
        sb.AppendLine("**Steps to reproduce**").AppendLine("1. ").AppendLine();
        sb.AppendLine("**Observed**").AppendLine();
        sb.AppendLine("**Expected**").AppendLine();
        sb.AppendLine("**Attachments**");
        if (request.SessionLogFilePath != null) sb.AppendLine("- session_log.txt");
        foreach (var a in request.Attachments)
            sb.AppendLine("- ").AppendLine(SecurityHelper.SanitizeFileName(Path.GetFileName(a)));
        sb.AppendLine();
        sb.AppendLine("> Redaction note: device serials are hashed; the bundle is prepared for manual upload — it was never transmitted by the tool.");
        return sb.ToString();
    }

    private static bool IsTextAttachment(string path)
        => Path.GetExtension(path) is ".txt" or ".log" or ".json" or ".csv" or ".md";

    private static async Task RedactTextFileAsync(string source, string destination)
    {
        using var reader = new StreamReader(source);
        await using var writer = new StreamWriter(destination, false);
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            await writer.WriteLineAsync(SecurityHelper.RedactSensitiveText(line)).ConfigureAwait(false);
    }
}
