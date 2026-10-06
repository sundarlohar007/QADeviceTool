using System.Runtime.CompilerServices;

namespace LogPro.Tests;

internal static class TestEnvironment
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        Environment.SetEnvironmentVariable("LOGPRO_DATA_DIRECTORY",
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LogProTests", Guid.NewGuid().ToString("N")));
    }
}
