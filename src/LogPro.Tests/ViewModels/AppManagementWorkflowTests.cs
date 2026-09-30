using LogPro.Models;
using System.IO.Compression;
using LogPro.Services;
using LogPro.ViewModels;
using Moq;

namespace LogPro.Tests.ViewModels;

public class AppManagementWorkflowTests
{
    private sealed class AsyncOnlyFiles(string path) : IFileDialogService
    {
        public string? OpenFile(string title, string filter) => null;
        public Task<string?> OpenFileAsync(string title, string filter) => Task.FromResult<string?>(path);
        public string? SaveFile(string title, string filter, string defaultFileName) => null;
        public string? OpenFolder(string title) => null;
    }

    private sealed class AsyncOnlyDialogs : IDialogService
    {
        public bool Confirm(string title, string message) => false;
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);
        public void Info(string title, string message) { }
        public void Error(string title, string message) { }
    }

    private static DeviceInfo Device(string serial, DevicePlatform platform = DevicePlatform.Android) => new()
    {
        Serial = serial, Name = serial, Platform = platform, ConnectionState = DeviceConnectionState.Online
    };

    private static AppItem App(string id, AppCategory category = AppCategory.User, bool running = false) => new()
    {
        PackageId = id, Name = id, Category = category, IsRunning = running
    };

    private static (AppManagementViewModel Vm, Mock<IDeviceMonitorService> Monitor,
        Mock<IAdbService> Adb, Mock<IIosService> Ios, Mock<ISessionService> Sessions) Create(params DeviceInfo[] devices)
    {
        var monitor = new Mock<IDeviceMonitorService>();
        monitor.Setup(x => x.CurrentDevices).Returns(devices.ToList());
        var adb = new Mock<IAdbService>();
        var ios = new Mock<IIosService>();
        var sessions = new Mock<ISessionService>();
        adb.Setup(x => x.GetAppInventoryAsync(It.IsAny<string>()))
            .ReturnsAsync(new AppInventoryResult(true, Array.Empty<AppItem>()));
        ios.Setup(x => x.GetAppInventoryAsync(It.IsAny<string>()))
            .ReturnsAsync(new AppInventoryResult(true, Array.Empty<AppItem>()));
        var vm = new AppManagementViewModel(adb.Object, ios.Object, monitor.Object,
            sessions.Object, new ImmediateUiDispatcher());
        return (vm, monitor, adb, ios, sessions);
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(10);
        condition().Should().BeTrue("the background inventory request should settle");
    }

    [Fact]
    public async Task InitialDevices_AreAvailableWithoutAnotherMonitorEvent()
    {
        var ctx = Create(Device("A"));
        using var vm = ctx.Vm;
        await UntilAsync(() => !vm.IsLoading);
        vm.SelectedDevice!.Serial.Should().Be("A");
        vm.Devices.Should().ContainSingle();
    }

    [Fact]
    public async Task LateResultFromPreviousDevice_CannotReplaceCurrentInventory()
    {
        var pending = new TaskCompletionSource<AppInventoryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ctx = Create();
        using var vm = ctx.Vm;
        ctx.Adb.Setup(x => x.GetAppInventoryAsync("A")).Returns(pending.Task);
        ctx.Adb.Setup(x => x.GetAppInventoryAsync("B"))
            .ReturnsAsync(new AppInventoryResult(true, new[] { App("com.example.b") }));

        ctx.Monitor.Raise(x => x.DevicesChanged += null, new List<DeviceInfo> { Device("A"), Device("B") });
        vm.SelectedDevice = vm.Devices[1];
        await UntilAsync(() => vm.InstalledApps.Any(a => a.PackageId == "com.example.b"));
        pending.SetResult(new AppInventoryResult(true, new[] { App("com.example.a") }));
        await Task.Delay(30);

        vm.InstalledApps.Should().ContainSingle(a => a.PackageId == "com.example.b");
    }

    [Fact]
    public async Task FailedInventory_ShowsErrorInsteadOfZeroApps()
    {
        var ctx = Create();
        using var vm = ctx.Vm;
        ctx.Adb.Setup(x => x.GetAppInventoryAsync("A"))
            .ReturnsAsync(AppInventoryResult.Failed("ADB offline"));
        ctx.Monitor.Raise(x => x.DevicesChanged += null, new List<DeviceInfo> { Device("A") });
        await UntilAsync(() => !vm.IsLoading);

        vm.StatusMessage.Should().Contain("ADB offline");
        vm.StatusMessage.Should().NotContain("Found 0");
    }

    [Fact]
    public async Task ReconnectedDevice_ReloadsInventoryAfterOfflineState()
    {
        var ctx = Create();
        using var vm = ctx.Vm;
        ctx.Adb.Setup(x => x.GetAppInventoryAsync("A"))
            .ReturnsAsync(new AppInventoryResult(true, new[] { App("com.example.app") }));
        ctx.Monitor.Raise(x => x.DevicesChanged += null, new List<DeviceInfo> { Device("A") });
        await UntilAsync(() => vm.InstalledApps.Count == 1);
        var offline = Device("A");
        offline.ConnectionState = DeviceConnectionState.Offline;
        ctx.Monitor.Raise(x => x.DevicesChanged += null, new List<DeviceInfo> { offline });
        vm.InstalledApps.Should().BeEmpty();

        ctx.Monitor.Raise(x => x.DevicesChanged += null, new List<DeviceInfo> { Device("A") });
        await UntilAsync(() => vm.InstalledApps.Count == 1);
    }

    [Fact]
    public async Task MetadataRefresh_KeepsSelectionAndAvoidsAnotherInventoryCall()
    {
        var ctx = Create();
        using var vm = ctx.Vm;
        ctx.Adb.Setup(x => x.GetAppInventoryAsync("A"))
            .ReturnsAsync(new AppInventoryResult(true, new[] { App("com.example.app") }));
        ctx.Monitor.Raise(x => x.DevicesChanged += null, new List<DeviceInfo> { Device("A") });
        await UntilAsync(() => vm.InstalledApps.Count == 1);
        vm.SelectedApp = vm.InstalledApps[0];
        var refreshed = Device("A");
        refreshed.BatteryLevel = "55%";
        ctx.Monitor.Raise(x => x.DevicesChanged += null, new List<DeviceInfo> { refreshed });

        vm.SelectedDevice!.Serial.Should().Be("A");
        vm.SelectedApp!.PackageId.Should().Be("com.example.app");
        ctx.Adb.Verify(x => x.GetAppInventoryAsync("A"), Times.Once);
    }

    [Fact]
    public async Task GlobalSelection_UsesExistingPickerItem()
    {
        var dispatcher = new ImmediateUiDispatcher();
        using var store = new DeviceStore(dispatcher);
        store.UpdateDevices(new[] { Device("A"), Device("B") });
        var adb = new Mock<IAdbService>();
        adb.Setup(x => x.GetAppInventoryAsync(It.IsAny<string>()))
            .ReturnsAsync(new AppInventoryResult(true, Array.Empty<AppItem>()));
        using var vm = new AppManagementViewModel(adb.Object, new Mock<IIosService>().Object,
            new Mock<IDeviceMonitorService>().Object, new Mock<ISessionService>().Object, dispatcher, store);
        await UntilAsync(() => !vm.IsLoading);

        vm.OnDeviceSelected(store.Devices[1]);
        await UntilAsync(() => vm.SelectedDevice?.Serial == "B" && !vm.IsLoading);
        vm.SelectedDevice.Should().BeSameAs(vm.Devices[1]);
        store.SelectedDevice!.Serial.Should().Be("B");
    }

    [Fact]
    public async Task FiltersSearchAndRunningState_UseInventoryMetadata()
    {
        var ctx = Create();
        using var vm = ctx.Vm;
        ctx.Adb.Setup(x => x.GetAppInventoryAsync("A"))
            .ReturnsAsync(new AppInventoryResult(true, new[] {
                App("com.example.user", AppCategory.User, true),
                App("com.example.system", AppCategory.System)
            }, RunningStateAvailable: true));
        ctx.Monitor.Raise(x => x.DevicesChanged += null, new List<DeviceInfo> { Device("A") });
        await UntilAsync(() => vm.InstalledApps.Count == 2);

        vm.SelectedFilter = "Running";
        vm.FilteredApps.Should().ContainSingle(a => a.PackageId == "com.example.user");
        vm.SearchText = "system";
        vm.FilteredApps.Should().BeEmpty();
        vm.SelectedFilter = "System";
        vm.FilteredApps.Should().ContainSingle(a => a.PackageId == "com.example.system");
    }

    [Fact]
    public async Task IosInstallException_StillRestartsLogCapture()
    {
        var ctx = Create();
        using var vm = ctx.Vm;
        var session = new LogSession { DeviceSerial = "IOS", Platform = DevicePlatform.iOS };
        ctx.Sessions.Setup(x => x.GetActiveSessionForDevice("IOS")).Returns(session);
        ctx.Sessions.Setup(x => x.WaitForCaptureStopAsync(session)).Returns(Task.CompletedTask);
        ctx.Sessions.Setup(x => x.StartCaptureAsync(session, It.IsAny<LogcatBuffer>(), It.IsAny<LogcatFormat>()))
            .ReturnsAsync(true);
        ctx.Ios.Setup(x => x.InstallIpaAsync("IOS", It.IsAny<string>(), It.IsAny<Action<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("install failed"));
        ctx.Monitor.Raise(x => x.DevicesChanged += null, new List<DeviceInfo> { Device("IOS", DevicePlatform.iOS) });
        await UntilAsync(() => !vm.IsLoading);

        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".ipa");
        try
        {
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
                archive.CreateEntry("Payload/Test.app/Info.plist");
            await vm.InstallFilesAsync([path]);
            ctx.Sessions.Verify(x => x.StopCapture(session), Times.Once);
            ctx.Sessions.Verify(x => x.WaitForCaptureStopAsync(session), Times.Once);
            ctx.Sessions.Verify(x => x.StartCaptureAsync(session, It.IsAny<LogcatBuffer>(), It.IsAny<LogcatFormat>()), Times.Once);
            vm.ConsoleOutput.Should().Contain("Log capture resumed");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task IosInstall_ReportsCaptureRestartFailureEvenWhenAppInstalled()
    {
        var ctx = Create();
        using var vm = ctx.Vm;
        var session = new LogSession { DeviceSerial = "IOS", Platform = DevicePlatform.iOS };
        ctx.Sessions.Setup(x => x.GetActiveSessionForDevice("IOS")).Returns(session);
        ctx.Sessions.Setup(x => x.WaitForCaptureStopAsync(session)).Returns(Task.CompletedTask);
        ctx.Sessions.Setup(x => x.StartCaptureAsync(session, It.IsAny<LogcatBuffer>(), It.IsAny<LogcatFormat>()))
            .ReturnsAsync(false);
        ctx.Ios.Setup(x => x.InstallIpaAsync("IOS", It.IsAny<string>(), It.IsAny<Action<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, "Installed"));
        ctx.Monitor.Raise(x => x.DevicesChanged += null, new List<DeviceInfo> { Device("IOS", DevicePlatform.iOS) });
        await UntilAsync(() => !vm.IsLoading);

        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".ipa");
        try
        {
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
                archive.CreateEntry("Payload/Test.app/Info.plist");
            await vm.InstallFilesAsync([path]);
            vm.StatusMessage.Should().Contain("log capture did not resume");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task InstallCommand_UsesAsyncPicker()
    {
        var ctx = Create();
        using var vm = ctx.Vm;
        ctx.Adb.Setup(x => x.InstallApkAsync("A", It.IsAny<string>(), It.IsAny<Action<string>>(),
            It.IsAny<CancellationToken>(), false)).ReturnsAsync((true, "Installed"));
        ctx.Monitor.Raise(x => x.DevicesChanged += null, new List<DeviceInfo> { Device("A") });
        await UntilAsync(() => !vm.IsLoading);
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".apk");
        var previous = UiServices.Files;
        try
        {
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
                archive.CreateEntry("AndroidManifest.xml");
            UiServices.Files = new AsyncOnlyFiles(path);
            await vm.InstallAppCommand.ExecuteAsync(null);
            ctx.Adb.Verify(x => x.InstallApkAsync("A", path, It.IsAny<Action<string>>(),
                It.IsAny<CancellationToken>(), false), Times.Once);
        }
        finally { UiServices.Files = previous; File.Delete(path); }
    }

    [Fact]
    public async Task UninstallCommand_UsesAsyncConfirmation()
    {
        var ctx = Create();
        using var vm = ctx.Vm;
        ctx.Adb.Setup(x => x.GetAppInventoryAsync("A"))
            .ReturnsAsync(new AppInventoryResult(true, new[] { App("com.example.app") }));
        ctx.Adb.Setup(x => x.UninstallAppAsync("A", "com.example.app")).ReturnsAsync(true);
        ctx.Monitor.Raise(x => x.DevicesChanged += null, new List<DeviceInfo> { Device("A") });
        await UntilAsync(() => vm.InstalledApps.Count == 1);
        vm.SelectedApp = vm.InstalledApps[0];
        var previous = UiServices.Dialogs;
        try
        {
            UiServices.Dialogs = new AsyncOnlyDialogs();
            await vm.UninstallAppCommand.ExecuteAsync(null);
            ctx.Adb.Verify(x => x.UninstallAppAsync("A", "com.example.app"), Times.Once);
        }
        finally { UiServices.Dialogs = previous; }
    }

    [Fact]
    public void PackagePreflight_RejectsWrongPlatformAndMalformedArchive()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".apk");
        try
        {
            File.WriteAllText(path, "not an APK");
            AppManagementViewModel.ValidateInstallPackage(path, DevicePlatform.iOS).Should().Contain(".ipa");
            AppManagementViewModel.ValidateInstallPackage(path, DevicePlatform.Android).Should().Contain("valid package archive");
        }
        finally { File.Delete(path); }
    }
}
