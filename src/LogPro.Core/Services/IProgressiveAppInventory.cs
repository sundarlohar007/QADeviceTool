using LogPro.Models;

namespace LogPro.Services;

public interface IProgressiveAppInventory
{
    Task<AppInventoryResult> GetAppInventoryAsync(string serial, Func<AppInventoryResult, Task>? progress, CancellationToken token);
}
