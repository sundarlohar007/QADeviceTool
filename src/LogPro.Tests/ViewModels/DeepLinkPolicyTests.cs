using LogPro.Models;
using LogPro.Services;
using LogPro.ViewModels;
using Moq;

namespace LogPro.Tests.ViewModels;

public class DeepLinkPolicyTests
{
    [Fact]
    public async Task HttpsAppLink_ShowsOfflinePolicyWarningWithoutLaunchingAdb()
    {
        var adb = new Mock<IAdbService>();
        var ios = new Mock<IIosService>();
        var monitor = new Mock<IDeviceMonitorService>();
        var vm = new DeepLinkViewModel(adb.Object, ios.Object, monitor.Object, new ImmediateUiDispatcher())
        {
            SelectedDevice = new DeviceInfo
            {
                Serial = "device-1",
                Platform = DevicePlatform.Android,
                ConnectionState = DeviceConnectionState.Online
            },
            TargetUrl = "https://example.test/app-link"
        };

        await vm.FireIntentCommand.ExecuteAsync(null);

        vm.StatusMessage.Should().Contain("HTTPS Android App Links are blocked");
        adb.Verify(x => x.BroadcastIntentAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        vm.Dispose();
    }
}
