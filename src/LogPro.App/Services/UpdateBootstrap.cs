using System.Diagnostics;
using System.IO;
using System.Windows;
using LogPro.Helpers;

namespace LogPro.Services;

/// <summary>Runs before normal startup; elevation never accepts an arbitrary download or digest.</summary>
internal static class UpdateBootstrap
{
    internal static async Task<bool> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is not ("--update-tool" or "--installation-check" or "--verify-installation")) return false;
        var installation = args[0] == "--installation-check";
        var verifyOnly = args[0] == "--verify-installation";
        var created = true;
        using var updating = verifyOnly ? null : new Mutex(false, "LogProUpdating", out created);
        try
        {
            if (!created) throw new InvalidOperationException("Another LogPro updater is running.");
            if (args[0] == "--update-tool" && args.Length >= 4 && args[2] == "--wait-process" && int.TryParse(args[3], out var parentId))
            {
                try
                {
                    using var parent = Process.GetProcessById(parentId);
                    if (!string.Equals(parent.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Updater parent must be this installation of LogPro.");
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                    await parent.WaitForExitAsync(timeout.Token);
                }
                catch (ArgumentException) { /* parent already closed */ }
            }
            if (!verifyOnly && Mutex.TryOpenExisting("LogProRunning", out var running))
            {
                running.Dispose();
                throw new InvalidOperationException("Close all LogPro windows before installing tool updates.");
            }
            if (!await ToolResolver.VerifyBundledToolsAsync(requireManifest: true, requireTools: true))
                throw new InvalidDataException("Installed tool verification failed. Reinstall the complete Windows package.");
            if (verifyOnly) { Application.Current.Shutdown(0); return true; }
            if (UpdateService.RequiresElevation) throw new InvalidOperationException("The Windows updater requires administrator access.");
            using var updater = new UpdateService();
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            var operation = args.Length > 1 ? args[1] : "";
            var messages = new List<string>();
            if (!installation && operation.StartsWith("rollback:", StringComparison.Ordinal))
            {
                var result = await updater.RollbackLastUpdateAsync(operation[9..]);
                if (!result.Success) throw new InvalidOperationException(result.Message);
                messages.Add(result.Message);
            }
            else
            {
                if (!installation && operation is not ("adb" or "scrcpy" or "pymobiledevice3"))
                    throw new InvalidOperationException("Unknown managed tool.");
                var updates = await updater.CheckAllAsync(deadline.Token);
                if (!installation && updates.Any(u => u.ToolName == operation && u.ReleaseNotes.StartsWith("Check failed:", StringComparison.Ordinal)))
                    throw new InvalidOperationException("Unable to check this component. Check your connection and retry.");
                foreach (var update in updates.Where(u => u.IsInstallable && (installation || u.ToolName == operation)))
                {
                    // A second installer cannot safely replace files while the first installer is
                    // still waiting for this check. Stage it for the normal application's updater.
                    if (installation && update.ToolName == "logpro")
                    {
                        await updater.PrepareUpdateAsync(update, deadline.Token);
                        messages.Add("A newer LogPro installer is downloaded. Open Settings after setup to install it.");
                        continue;
                    }
                    var result = await updater.ApplyUpdateAsync(update, ct: deadline.Token);
                    messages.Add(result.Message);
                    if (!result.Success && !installation) throw new InvalidOperationException(result.Message);
                }
                if (messages.Count == 0) messages.Add("No newer compatible packages are available. Bundled tools are ready.");
            }
            if (!installation) MessageBox.Show(string.Join(Environment.NewLine, messages) + "\n\nStart LogPro again to continue.", "LogPro Windows updater");
            Application.Current.Shutdown(0);
        }
        catch (Exception ex)
        {
            AppLogger.Log.Warn(ex, "Windows update/installation check failed");
            if (!installation && !verifyOnly) MessageBox.Show(SecurityHelper.RedactSensitiveText(ex.Message), "LogPro Windows updater", MessageBoxButton.OK, MessageBoxImage.Warning);
            // Offline installation retains its verified bundled tools.
            Application.Current.Shutdown(installation ? 0 : 1);
        }
        return true;
    }
}
