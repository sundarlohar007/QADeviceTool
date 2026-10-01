using LogPro.Helpers;
using LogPro.Models;
using LogPro.Services;
using LogPro.ViewModels;
using Moq;

namespace LogPro.Tests.ViewModels;

public class DeepLinkWorkflowTests
{
    private static DeviceInfo Android(string id = "device-1") => new()
    { Serial = id, Name = "Pixel", Platform = DevicePlatform.Android, ConnectionState = DeviceConnectionState.Online };
    private static (DeepLinkViewModel Vm, Mock<IAdbService> Adb, Mock<IDeviceMonitorService> Monitor) Create(params DeviceInfo[] devices)
    {
        var adb = new Mock<IAdbService>();
        var monitor = new Mock<IDeviceMonitorService>();
        monitor.Setup(m => m.CurrentDevices).Returns(devices);
        adb.Setup(a => a.LaunchDeepLinkAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DeepLinkOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeepLinkResult(DeepLinkOutcome.Launched, "Activity launched."));
        return (new(adb.Object, Mock.Of<IIosService>(), monitor.Object, new ImmediateUiDispatcher(),
            new DeepLinkPresetStore(Path.Combine(Path.GetTempPath(), "logpro-deep-tests-" + Guid.NewGuid(), "presets.json"))), adb, monitor);
    }

    [Fact]
    public void Startup_SeedsPickerAndPreservesLocalSelectionThroughMetadataUpdates()
    {
        var a = Android("a"); var b = Android("b");
        var (vm, _, monitor) = Create(a, b);
        using (vm)
        {
            vm.Devices.Should().HaveCount(2);
            vm.OnDeviceSelected(a);
            vm.SelectedDevice = vm.Devices.Single(d => d.Serial == "b");
            vm.TargetUrl = "myapp://page";
            vm.TargetPackage = "com.example.app";
            monitor.Raise(m => m.DevicesChanged += null, new List<DeviceInfo> { a, b });
            vm.OnDeviceSelected(a);
            vm.SelectedDevice!.Serial.Should().Be("b");
            vm.TargetPackage.Should().Be("com.example.app");
            vm.FireIntentCommand.CanExecute(null).Should().BeTrue();
        }
    }

    [Fact]
    public void PlatformAndAvailability_ProduceCorrectWarningsAndCommandAvailability()
    {
        var android = Android("same");
        var ios = new DeviceInfo { Serial = "same", Name = "iPhone", Platform = DevicePlatform.iOS, ConnectionState = DeviceConnectionState.Online };
        var (vm, _, monitor) = Create(android, ios);
        using (vm)
        {
            vm.TargetUrl = "myapp://page";
            vm.SelectedDevice = vm.Devices.Single(d => d.Platform == DevicePlatform.iOS);
            vm.CapabilityMessage.Should().Contain("bundled pymobiledevice3");
            vm.FireIntentCommand.CanExecute(null).Should().BeFalse();
            vm.SelectedDevice = vm.Devices.Single(d => d.Platform == DevicePlatform.Android);
            vm.CapabilityMessage.Should().NotContain("not supported for iOS");
            vm.FireIntentCommand.CanExecute(null).Should().BeTrue();
            monitor.Raise(m => m.DevicesChanged += null, new List<DeviceInfo> { android.WithTemporaryUnavailable(true), ios });
            vm.SelectedDevice!.Platform.Should().Be(DevicePlatform.Android);
            vm.FireIntentCommand.CanExecute(null).Should().BeFalse();
            vm.CapabilityMessage.Should().Contain("reconnecting");
        }
    }

    [Fact]
    public async Task InputEdits_DoNotChangeLaunchOrItsResultAndHistoryHidesPayload()
    {
        var (vm, adb, _) = Create(Android());
        using (vm)
        {
            var completion = new TaskCompletionSource<DeepLinkResult>();
            adb.Setup(a => a.LaunchDeepLinkAsync("device-1", "first://secret-customer?private=value", It.IsAny<DeepLinkOptions>(), It.IsAny<CancellationToken>()))
                .Returns(completion.Task);
            vm.TargetUrl = "first://secret-customer?private=value";
            var task = vm.FireIntentCommand.ExecuteAsync(null);
            vm.IsRouting.Should().BeTrue();
            vm.TargetUrl = "second://different";
            vm.StatusKind.Should().Be("Running");
            completion.SetResult(new(DeepLinkOutcome.Launched, "Activity launched."));
            await task;
            vm.StatusMessage.Should().Contain("first:[destination hidden]").And.NotContain("second:").And.NotContain("secret-customer");
            vm.History.Should().ContainSingle();
            vm.History[0].Summary.Should().NotContain("private=value");
            vm.IsRouting.Should().BeFalse();
        }
    }

    [Fact]
    public async Task DeviceSwitch_CancelsOutstandingLaunch()
    {
        var (vm, adb, _) = Create(Android("a"), Android("b"));
        using (vm)
        {
            CancellationToken observed = default;
            var completion = new TaskCompletionSource<DeepLinkResult>();
            adb.Setup(a => a.LaunchDeepLinkAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DeepLinkOptions>(), It.IsAny<CancellationToken>()))
                .Returns((string _, string _, DeepLinkOptions _, CancellationToken token) => { observed = token; return completion.Task; });
            vm.TargetUrl = "myapp://page";
            var task = vm.FireIntentCommand.ExecuteAsync(null);
            vm.SelectedDevice = vm.Devices.Single(d => d.Serial == "b");
            observed.IsCancellationRequested.Should().BeTrue();
            completion.SetResult(new(DeepLinkOutcome.Cancelled, "Cancelled."));
            await task;
            vm.StatusMessage.Should().NotContain("Cancelled.");
        }
    }

    [Fact]
    public async Task Disposal_CancelsAndIgnoresLateCompletion()
    {
        var (vm, adb, _) = Create(Android());
        CancellationToken observed = default;
        var completion = new TaskCompletionSource<DeepLinkResult>();
        adb.Setup(a => a.LaunchDeepLinkAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DeepLinkOptions>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string _, DeepLinkOptions _, CancellationToken token) => { observed = token; return completion.Task; });
        vm.TargetUrl = "myapp://page";
        var task = vm.FireIntentCommand.ExecuteAsync(null);
        vm.Dispose();
        var status = vm.StatusMessage;
        observed.IsCancellationRequested.Should().BeTrue();
        completion.SetResult(new(DeepLinkOutcome.Launched, "Late result."));
        await task;
        vm.StatusMessage.Should().Be(status);
        vm.History.Should().BeEmpty();
        vm.FireIntentCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void Disposal_IgnoresQueuedDeviceCallbacks()
    {
        var dispatcher = new QueuedDispatcher();
        var monitor = new Mock<IDeviceMonitorService>();
        monitor.Setup(m => m.CurrentDevices).Returns(new[] { Android() });
        var vm = new DeepLinkViewModel(Mock.Of<IAdbService>(), Mock.Of<IIosService>(), monitor.Object, dispatcher);
        vm.Dispose(); dispatcher.Flush();
        vm.Devices.Should().BeEmpty();
    }

    [Fact]
    public async Task Batch_ValidatesAllLinksBeforeStartingAndStopsOnUncertainTimeout()
    {
        var (vm, adb, _) = Create(Android());
        using (vm)
        {
            vm.BatchInput = "myapp://first\nintent://host#Intent;scheme=https;end";
            await vm.RunBatchCommand.ExecuteAsync(null);
            adb.Verify(a => a.LaunchDeepLinkAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DeepLinkOptions>(), It.IsAny<CancellationToken>()), Times.Never);
            vm.StatusMessage.Should().Contain("Nothing was launched");
            vm.BatchInput = "myapp://first\nmyapp://second\nmyapp://third";
            adb.SetupSequence(a => a.LaunchDeepLinkAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DeepLinkOptions>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DeepLinkResult(DeepLinkOutcome.Delivered, "Delivered."))
                .ReturnsAsync(new DeepLinkResult(DeepLinkOutcome.TimedOut, "Timeout."));
            await vm.RunBatchCommand.ExecuteAsync(null);
            vm.History.Should().HaveCount(2);
            vm.StatusMessage.Should().Contain("2/3 completed, 1 confirmed");
        }
    }

    [Fact]
    public async Task HandlerInspection_AndInstalledPackages_AreAvailableWithoutLaunching()
    {
        var (vm, adb, _) = Create(Android());
        using (vm)
        {
            vm.TargetUrl = "myapp://page";
            adb.Setup(a => a.InspectDeepLinkAsync("device-1", "myapp://page", It.IsAny<DeepLinkOptions>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DeepLinkInspection(true, new[] { "com.example.app/.Main" }, "One handler."));
            await vm.InspectHandlersCommand.ExecuteAsync(null);
            vm.HandlerSummary.Should().Contain("com.example.app/.Main");
            adb.Setup(a => a.ExecuteCommandWithResultAsync("device-1", "shell pm list packages", It.IsAny<CancellationToken>()))
                .ReturnsAsync((true, "package:com.example.app\npackage:com.example.app\npackage:com.other.app\nError: ignored", ""));
            await vm.LoadAppsCommand.ExecuteAsync(null);
            vm.InstalledPackages.Should().Equal("com.example.app", "com.other.app");
            vm.SelectedInstalledPackage = "com.example.app";
            vm.TargetPackage.Should().Be("com.example.app");
            vm.SelectedInstalledPackage = null;
            vm.TargetPackage.Should().Be("com.example.app", "clearing the picker must not null the editable target");
            vm.TargetPackage = "com.manual.app";
            vm.SelectedInstalledPackage.Should().BeNull();
            adb.Verify(a => a.LaunchDeepLinkAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DeepLinkOptions>(), It.IsAny<CancellationToken>()), Times.Never);
        }
    }

    [Fact]
    public void Presets_OnlyPersistAfterOptInAndDisablingRemovesSavedFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "logpro-deep-tests-" + Guid.NewGuid());
        var path = Path.Combine(directory, "presets.json");
        var monitor = new Mock<IDeviceMonitorService>();
        monitor.Setup(m => m.CurrentDevices).Returns(Array.Empty<DeviceInfo>());
        using var vm = new DeepLinkViewModel(Mock.Of<IAdbService>(), Mock.Of<IIosService>(), monitor.Object,
            new ImmediateUiDispatcher(), new DeepLinkPresetStore(path));
        try
        {
            vm.TargetUrl = "myapp://secret"; vm.PresetName = "Sample";
            vm.SavePresetCommand.Execute(null);
            vm.Presets.Should().ContainSingle(); File.Exists(path).Should().BeFalse();
            vm.PersistPresets = true;
            File.Exists(path).Should().BeTrue();
            var loaded = new DeepLinkPresetStore(path).Load(out var error);
            error.Should().BeEmpty(); loaded.Single().Uri.Should().Be("myapp://secret");
            vm.PersistPresets = false;
            File.Exists(path).Should().BeFalse(); vm.Presets.Should().ContainSingle();
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task History_IsBoundedAndConflictingBatchTargetsNeverLaunch()
    {
        var (vm, adb, _) = Create(Android());
        using (vm)
        {
            vm.TargetUrl = "myapp://page";
            for (var i = 0; i < 101; i++) await vm.FireIntentCommand.ExecuteAsync(null);
            vm.History.Should().HaveCount(100);
            vm.TargetPackage = "com.example.app";
            vm.BatchInput = "myapp://first\nintent://second#Intent;scheme=myapp;package=com.other.app;end";
            await vm.RunBatchCommand.ExecuteAsync(null);
            vm.StatusMessage.Should().Contain("conflicts").And.Contain("Nothing was launched");
            adb.Verify(a => a.LaunchDeepLinkAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DeepLinkOptions>(), It.IsAny<CancellationToken>()), Times.Exactly(101));
        }
    }

    [Fact]
    public void InvalidPresetFiles_AreRejectedWithoutLosingTheSource()
    {
        var directory = Path.Combine(Path.GetTempPath(), "logpro-deep-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "presets.json");
        try
        {
            File.WriteAllText(path, "[{\"Name\":\"Unsafe\",\"Uri\":\"intent://host#Intent;scheme=https;end\"}]");
            var store = new DeepLinkPresetStore(path);
            store.Load(out var error).Should().BeEmpty(); error.Should().NotBeEmpty(); File.Exists(path).Should().BeTrue();
            var monitor = new Mock<IDeviceMonitorService>();
            monitor.Setup(m => m.CurrentDevices).Returns(new[] { Android() });
            using var vm = new DeepLinkViewModel(Mock.Of<IAdbService>(), Mock.Of<IIosService>(), monitor.Object, new ImmediateUiDispatcher(), store);
            vm.StatusMessage.Should().Contain("could not be loaded");
            store.Save(new[] { new DeepLinkPreset("Unsafe", "https://example.test") }, out error).Should().BeFalse();
            File.ReadAllText(path).Should().Contain("scheme=https");
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class QueuedDispatcher : IUiDispatcher
    {
        private readonly Queue<Action> _actions = new();
        public bool IsOnUiThread => false;
        public void Post(Action action) => _actions.Enqueue(action);
        public Task InvokeAsync(Action action) { Post(action); return Task.CompletedTask; }
        public void Flush() { while (_actions.TryDequeue(out var action)) action(); }
    }
}
