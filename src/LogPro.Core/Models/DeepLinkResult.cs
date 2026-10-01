namespace LogPro.Models;

public sealed record DeepLinkOptions(string PackageId = "", bool Browsable = false);

public enum DeepLinkOutcome { Launched, Delivered, TaskBroughtForward, Deferred, Failed, TimedOut, Cancelled, Unsupported }

public sealed record DeepLinkResult(DeepLinkOutcome Outcome, string Message, string Activity = "", int? TotalTimeMs = null, int? WaitTimeMs = null)
{
    public bool Success => Outcome is DeepLinkOutcome.Launched or DeepLinkOutcome.Delivered or DeepLinkOutcome.TaskBroughtForward;
}

public sealed record DeepLinkInspection(bool Supported, IReadOnlyList<string> Handlers, string Message);
public sealed record DeepLinkPreset(string Name, string Uri, string PackageId = "", bool Browsable = false);
public sealed record DeepLinkHistoryEntry(DateTimeOffset Time, string Device, string Link, DeepLinkResult Result)
{
    public string Summary => $"{Time:HH:mm:ss} • {Device} • {Link} • {Result.Outcome} • {Result.Message}";
}
