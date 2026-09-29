using LogPro.Services;

namespace LogPro.Tests.Services;

[Collection("HeavyE2E")]
public class IosServiceAvailabilityTests
{
    [Fact]
    public async Task CheckAvailability_UsesAnOperationalCli()
    {
        var status = await new IosService().CheckAvailabilityAsync();

        status.IsInstalled.Should().BeTrue(status.StatusMessage);
        status.Version.Should().NotBeNullOrWhiteSpace();
    }
}
