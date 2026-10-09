using LogPro.Helpers;

namespace LogPro.Services;

public enum DocumentTransferOutcome { Completed, Unsupported, Disconnected, TrustRequired, NotFound, Cancelled, Failed }
public sealed record DocumentTransferResult(DocumentTransferOutcome Outcome, string Message)
{
    public bool Success => Outcome == DocumentTransferOutcome.Completed;
    public static DocumentTransferResult FromTool(ToolLauncherResult result, CancellationToken token)
    {
        if (token.IsCancellationRequested) return new(DocumentTransferOutcome.Cancelled, "Document transfer cancelled.");
        if (result.Success) return new(DocumentTransferOutcome.Completed, "Document transfer completed.");
        var text = result.Error + "\n" + result.Output;
        bool Has(params string[] terms) => terms.Any(t => text.Contains(t, StringComparison.OrdinalIgnoreCase));
        if (Has("not paired", "pairing", "trust", "InvalidHostID"))
            return new(DocumentTransferOutcome.TrustRequired, "Unlock the iOS device and accept the trust dialog before retrying.");
        if (Has("NoDevice", "not connected", "disconnected", "connection", "BrokenPipe", "timed out", "timeout"))
            return new(DocumentTransferOutcome.Disconnected, "Connection to the iOS device failed. Check USB and device readiness, then retry.");
        if (Has("ApplicationLookupFailed", "InstallationLookupFailed", "File Sharing", "PermissionDenied", "permission denied", "not supported"))
            return new(DocumentTransferOutcome.Unsupported, "This app does not permit Documents access through iOS File Sharing. Use an app with File Sharing enabled.");
        if (Has("OBJECT_NOT_FOUND", "No such file", "not found"))
            return new(DocumentTransferOutcome.NotFound, "The app or document path was not found. Confirm the bundle ID and /Documents/ path.");
        return new(DocumentTransferOutcome.Failed, "Document transfer failed. " + SecurityHelper.RedactSensitiveText(result.Error.Length > 0 ? result.Error : result.Output));
    }
}

public interface IIosDocumentTransfers
{
    Task<DocumentTransferResult> PullDocumentAsync(string udid, string bundleId, string remotePath, string localPath, CancellationToken token);
    Task<DocumentTransferResult> PushDocumentAsync(string udid, string bundleId, string localPath, string remotePath, CancellationToken token);
}
