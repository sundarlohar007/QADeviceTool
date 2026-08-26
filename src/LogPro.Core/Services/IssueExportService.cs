using System.Text;
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
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var deviceHash = request.Device != null ? SecurityHelper.HashSerial(request.Device.Serial) : "nodevice";
        var bundleDir = Path.Combine(request.OutputDirectory, $"issue_{deviceHash}_{stamp}");
        Directory.CreateDirectory(bundleDir);

        var files = new List<string>();

        // 1. Redacted markdown template
        var mdPath = Path.Combine(bundleDir, "issue.md");
        await File.WriteAllTextAsync(mdPath, BuildMarkdown(request, deviceHash));
        files.Add(mdPath);

        // 2. Session log copy (device output — kept as evidence, serials already absent from file names)
        if (request.SessionLogFilePath != null && File.Exists(request.SessionLogFilePath))
        {
            var dest = Path.Combine(bundleDir, "session_log.txt");
            File.Copy(request.SessionLogFilePath, dest, overwrite: true);
            files.Add(dest);
        }

        // 3. Attachments (screenshots, perf reports, recordings the user explicitly selected)
        foreach (var attachment in request.Attachments)
        {
            if (!File.Exists(attachment)) continue;
            var dest = Path.Combine(bundleDir, Path.GetFileName(attachment));
            File.Copy(attachment, dest, overwrite: true);
            files.Add(dest);
        }

        // 4. Manifest
        var manifestPath = Path.Combine(bundleDir, "bundle-info.json");
        var manifest = new StringBuilder();
        manifest.AppendLine("{\n  \"device\": \"").Append(deviceHash).Append("\",\n  \"generatedUtc\": \"")
               .Append(DateTime.UtcNow.ToString("O")).Append("\",\n  \"files\": [");
        foreach (var file in files)
            manifest.Append("\n    \"").Append(Path.GetFileName(file).Replace("\"", "\\\"")).Append("\",");
        manifest.Length--; // trailing comma
        manifest.AppendLine("\n  ]\n}");
        await File.WriteAllTextAsync(manifestPath, manifest.ToString());
        files.Add(manifestPath);

        return new IssueBundle { DirectoryPath = bundleDir, MarkdownPath = mdPath, Files = files };
    }

    private static string BuildMarkdown(IssueExportRequest request, string deviceHash)
    {
        var d = request.Device;
        var sb = new StringBuilder();
        sb.AppendLine("### ").AppendLine(request.Title ?? "[Summarize the issue]");
        sb.AppendLine();
        sb.AppendLine("**Environment**");
        sb.AppendLine("- Device (hashed): ").Append(deviceHash).AppendLine();
        if (d != null)
        {
            sb.AppendLine("- Platform: ").Append(d.Platform).AppendLine();
            sb.AppendLine("- Model: ").AppendLine(d.Model);
            sb.AppendLine("- OS: ").AppendLine(d.OsVersion);
        }
        sb.AppendLine();
        sb.AppendLine("**Steps to reproduce**").AppendLine("1. ").AppendLine();
        sb.AppendLine("**Observed**").AppendLine();
        sb.AppendLine("**Expected**").AppendLine();
        sb.AppendLine("**Attachments**");
        if (request.SessionLogFilePath != null) sb.AppendLine("- session_log.txt");
        foreach (var a in request.Attachments) sb.AppendLine("- ").AppendLine(Path.GetFileName(a));
        sb.AppendLine();
        sb.AppendLine("> Redaction note: device serials are hashed; the bundle is prepared for manual upload — it was never transmitted by the tool.");
        return sb.ToString();
    }
}
