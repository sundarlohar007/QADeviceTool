using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LogPro.Cli;
using LogPro.Models;
using LogPro.Services;
using Moq;

namespace LogPro.Tests;

public class ControlApiLifecycleTests
{
    [Fact]
    public async Task Shutdown_CancelsDiscoveryBeforeAnyCaptureStarts()
    {
        using var prefs = new IsolatedPreferences();
        var adb = new Mock<IAdbService>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        adb.As<ICancellableDeviceQueries>().Setup(x => x.GetConnectedDevicesAsync(It.IsAny<CancellationToken>()))
            .Returns(async (CancellationToken token) =>
            {
                entered.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
                return new List<DeviceInfo>();
            });
        await using var server = new ControlApiServer(adb.Object, new Mock<IIosService>().Object);
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        server.Start(port);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(5) };
        client.DefaultRequestHeaders.Add("X-LogPro-Api-Key", server.ApiKey);
        var request = client.PostAsJsonAsync("/capture/start", new { serial = "TEST", @out = prefs.DirectoryPath });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
        try { using var response = await request; } catch (HttpRequestException) { }
        adb.Verify(x => x.StartLogCaptureAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<LogcatBuffer>(), It.IsAny<LogcatFormat>()), Times.Never);
    }

    [Fact]
    public async Task Api_RejectsDuplicateDeviceCapture_AndStopReturnsFinalSavedCount()
    {
        using var prefs = new IsolatedPreferences();
        PreferencesService.Current.TargetPackageName = "com.unchanged.app";
        var adb = new Mock<IAdbService>();
        var ios = new Mock<IIosService>();
        adb.Setup(a => a.GetConnectedDevicesAsync()).ReturnsAsync(new List<DeviceInfo> { new() { Serial = "TEST", Platform = DevicePlatform.Android } });
        ios.Setup(a => a.GetConnectedDevicesAsync()).ReturnsAsync(new List<DeviceInfo>());
        adb.Setup(a => a.StartLogCaptureAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<LogcatBuffer>(), It.IsAny<LogcatFormat>()))
            .Returns(() => Task.FromResult(Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "adb.exe"), "--production-stream")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, StandardOutputEncoding = Encoding.UTF8 })));
        await using var server = new ControlApiServer(adb.Object, ios.Object);
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        server.Start(port);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.Add("X-LogPro-Api-Key", server.ApiKey);
        var body = new { serial = "TEST", @out = prefs.DirectoryPath, package = "" };
        var responses = await Task.WhenAll(client.PostAsJsonAsync("/capture/start", body), client.PostAsJsonAsync("/capture/start", body));
        responses.Select(r => r.StatusCode).Should().Contain(HttpStatusCode.OK).And.Contain(HttpStatusCode.Conflict);
        using var started = JsonDocument.Parse(await responses.Single(r => r.IsSuccessStatusCode).Content.ReadAsStringAsync());
        var id = started.RootElement.GetProperty("sessionId").GetString();
        var stopped = await client.PostAsJsonAsync("/capture/stop", new { sessionId = id });
        stopped.StatusCode.Should().Be(HttpStatusCode.OK);
        using var result = JsonDocument.Parse(await stopped.Content.ReadAsStringAsync());
        var file = result.RootElement.GetProperty("logFile").GetString()!;
        (await File.ReadAllLinesAsync(file)).LongLength.Should().Be(result.RootElement.GetProperty("lines").GetInt64());
        result.RootElement.GetProperty("complete").GetBoolean().Should().BeTrue();
        PreferencesService.Current.TargetPackageName.Should().Be("com.unchanged.app");
        foreach (var response in responses) response.Dispose();
    }
}
