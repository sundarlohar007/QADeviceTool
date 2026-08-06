using System;
using System.IO;

namespace LogPro.Tests.Helpers;

public class TempDirectory : IDisposable
{
    public string DirectoryPath { get; }

    public TempDirectory(string? prefix = null)
    {
        var tempPath = System.IO.Path.GetTempPath();
        var dirName = string.IsNullOrEmpty(prefix)
            ? $"LogProTest_{Guid.NewGuid():N}"
            : $"LogProTest_{prefix}_{Guid.NewGuid():N}";
        DirectoryPath = System.IO.Path.Combine(tempPath, dirName);
        System.IO.Directory.CreateDirectory(DirectoryPath);
    }

    public string CreateSubDirectory(string name)
    {
        var subPath = System.IO.Path.Combine(DirectoryPath, name);
        System.IO.Directory.CreateDirectory(subPath);
        return subPath;
    }

    public void Dispose()
    {
        try
        {
            if (System.IO.Directory.Exists(DirectoryPath))
            {
                System.IO.Directory.Delete(DirectoryPath, true);
            }
        }
        catch
        {
            // Ignore cleanup failures
        }
    }
}