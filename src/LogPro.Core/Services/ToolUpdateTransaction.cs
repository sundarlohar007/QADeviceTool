using System.Text.Json;
using LogPro.Helpers;

namespace LogPro.Services;

/// <summary>Recoverable tool replacement. The backup remains intact until another successful update.</summary>
internal sealed class ToolUpdateTransaction(string appDirectory, string toolsDirectory)
{
    private string Manifest => Path.Combine(appDirectory, ToolManifest.DefaultFileName);
    private string Backup(string tool) => Path.Combine(appDirectory, ".logpro_backup_" + tool);
    private string JournalPath(string tool) => Path.Combine(appDirectory, ".logpro_transaction_" + tool + ".json");
    internal Action<string>? Checkpoint { get; set; }
    private sealed record Journal(string Tool, string RestoreName, string CurrentName, string OriginalManifest);

    private static void ValidateTool(string tool)
    {
        if (tool is not ("adb" or "scrcpy" or "pymobiledevice3")) throw new InvalidDataException("Unknown managed tool.");
    }

    private static bool ValidName(string tool, string name) => name == tool ||
        (tool == "scrcpy" && System.Text.RegularExpressions.Regex.IsMatch(name, @"^scrcpy-win64-v[0-9.]+$"));

    private static void DeleteDirectory(string directory)
    {
        if (!PathHelper.IsSafeLocalPath(directory)) throw new IOException("Unsafe tool directory.");
        Directory.Delete(directory, true);
    }

    private static void WriteAtomic(string destination, string text)
    {
        if (!PathHelper.IsSafeLocalPath(destination)) throw new IOException("Unsafe update metadata path.");
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(System.Text.Encoding.UTF8.GetBytes(text));
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static void CopyTree(string source, string destination)
    {
        if (!PathHelper.IsSafeLocalPath(source) || !PathHelper.IsSafeLocalPath(destination))
            throw new IOException("Tool directories must be local and contain no reparse points.");
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
        {
            if (!PathHelper.IsSafeLocalPath(file)) throw new IOException("Unsafe tool file.");
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
        }
        foreach (var directory in Directory.GetDirectories(source))
            CopyTree(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    internal async Task ValidateBackupAsync(string tool)
    {
        ValidateTool(tool);
        var backup = Backup(tool);
        if (!File.Exists(backup + ".name") || !File.Exists(backup + ".manifest.json") ||
            !ValidName(tool, File.ReadAllText(backup + ".name").Trim()) ||
            !(await ToolManifest.VerifyAsync(backup, backup + ".manifest.json").ConfigureAwait(false)).IsHealthy)
            throw new InvalidDataException("The previous tool package is missing or fails integrity verification.");
    }

    internal async Task CommitAsync(string tool, string staged, bool preserveBackup = false)
    {
        ValidateTool(tool);
        await RecoverAsync(tool).ConfigureAwait(false);
        var current = Directory.Exists(Path.Combine(toolsDirectory, tool)) ? tool :
            tool == "scrcpy" ? Directory.GetDirectories(toolsDirectory, "scrcpy-win64-*").Select(Path.GetFileName).FirstOrDefault() : null;
        if (current != null && !ValidName(tool, current)) throw new InvalidDataException("Invalid current tool directory.");
        var backup = Backup(tool);
        if (!preserveBackup)
        {
            if (current == null) throw new InvalidDataException("Repair the complete installation before updating a missing tool.");
            var prepared = Path.Combine(appDirectory, ".logpro_backup_stage_" + Guid.NewGuid().ToString("N"));
            try
            {
                CopyTree(Path.Combine(toolsDirectory, current), prepared);
                await ToolManifest.WriteAsync(prepared, prepared + ".manifest.json").ConfigureAwait(false);
                if (Directory.Exists(backup)) DeleteDirectory(backup);
                Directory.Move(prepared, backup);
                File.Move(prepared + ".manifest.json", backup + ".manifest.json", true);
                File.WriteAllText(backup + ".name", current);
            }
            finally
            {
                if (Directory.Exists(prepared)) DeleteDirectory(prepared);
                if (File.Exists(prepared + ".manifest.json")) File.Delete(prepared + ".manifest.json");
            }
        }
        await ValidateBackupAsync(tool).ConfigureAwait(false);
        var journal = new Journal(tool, File.ReadAllText(backup + ".name").Trim(), current ?? tool, File.ReadAllText(Manifest));
        var path = JournalPath(tool);
        WriteAtomic(path, JsonSerializer.Serialize(journal));
        try
        {
            Checkpoint?.Invoke("journal-written");
            if (current != null) DeleteDirectory(Path.Combine(toolsDirectory, current));
            Checkpoint?.Invoke("old-removed");
            Directory.Move(staged, Path.Combine(toolsDirectory, tool));
            Checkpoint?.Invoke("new-installed");
            await ToolManifest.WriteAsync(toolsDirectory, Manifest).ConfigureAwait(false);
            Checkpoint?.Invoke("manifest-written");
            File.Delete(path);
            ToolResolver.ClearCache();
        }
        catch
        {
            await RecoverAsync(tool).ConfigureAwait(false);
            throw;
        }
    }

    internal async Task RecoverAsync(string tool)
    {
        ValidateTool(tool);
        var path = JournalPath(tool);
        if (!File.Exists(path)) return;
        var journal = JsonSerializer.Deserialize<Journal>(File.ReadAllText(path)) ?? throw new InvalidDataException("Update journal is unreadable.");
        if (journal.Tool != tool || !ValidName(tool, journal.RestoreName) || !ValidName(tool, journal.CurrentName))
            throw new InvalidDataException("Invalid update journal paths.");
        await ValidateBackupAsync(tool).ConfigureAwait(false);
        var prepared = Path.Combine(appDirectory, ".logpro_restore_" + Guid.NewGuid().ToString("N"));
        try
        {
            CopyTree(Backup(tool), prepared);
            foreach (var name in new[] { tool, journal.RestoreName, journal.CurrentName }.Distinct())
            {
                var directory = Path.Combine(toolsDirectory, name);
                if (Directory.Exists(directory)) DeleteDirectory(directory);
            }
            Directory.Move(prepared, Path.Combine(toolsDirectory, journal.RestoreName));
            // Retain the expected hashes of all other tools; never re-bless unrelated modified files.
            var entries = JsonSerializer.Deserialize(journal.OriginalManifest, LogProJsonContext.Default.IReadOnlyListToolManifestEntry)!.ToList();
            entries.RemoveAll(e => new[] { tool, journal.CurrentName, journal.RestoreName }.Any(n => e.Path.StartsWith(n + "/", StringComparison.OrdinalIgnoreCase)));
            var restored = JsonSerializer.Deserialize(File.ReadAllText(Backup(tool) + ".manifest.json"), LogProJsonContext.Default.IReadOnlyListToolManifestEntry)!;
            entries.AddRange(restored.Select(e => new ToolManifestEntry { Path = journal.RestoreName + "/" + e.Path, Sha256 = e.Sha256 }));
            WriteAtomic(Manifest, JsonSerializer.Serialize(entries, LogProJsonContext.Default.IReadOnlyListToolManifestEntry));
            if (!(await ToolManifest.VerifyAsync(toolsDirectory, Manifest).ConfigureAwait(false)).IsHealthy)
                throw new InvalidDataException("Recovery restored the tool, but the installation still requires repair.");
            File.Delete(path);
            ToolResolver.ClearCache();
        }
        finally { if (Directory.Exists(prepared)) DeleteDirectory(prepared); }
    }
}
