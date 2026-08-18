using System.Net;
using System.Text.Json;
using LogPro.Models;
using LogPro.Services;
using LogPro.Services.Profiling;

namespace LogPro.Cli;

/// <summary>
/// Local control API (§16) — a loopback-only HTTP surface for CI/Appium harnesses.
/// Runs inside `logpro-cli serve`. Trust boundary: 127.0.0.1 only, same user, no auth
/// (documented in SECURITY.md); the engine is shared with the GUI apps.
/// </summary>
public sealed class ControlApiServer : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly AdbService _adb;
    private readonly IosService _ios;
    private readonly object _lock = new();
    private readonly Dictionary<string, CaptureHandle> _captures = new();
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private AndroidPerformanceProfiler? _profiler;
    private Task? _loop;

    public ControlApiServer(AdbService adb, IosService ios)
    {
        _adb = adb;
        _ios = ios;
    }

    public void Start(int port)
    {
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        _cts = new CancellationTokenSource();
        _loop = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        while (_cts is { IsCancellationRequested: false })
        {
            HttpListenerContext ctx;
            try { ctx = await _listener!.GetContextAsync(); }
            catch (HttpListenerException) { break; }
            catch (ObjectDisposedException) { break; }

            try { await HandleAsync(ctx); }
            catch (Exception ex)
            {
                AppLogger.Log.Error(ex, "[ControlApi] Request failed");
                await WriteJsonAsync(ctx.Response, 500, new { error = ex.Message });
            }
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url!.AbsolutePath.TrimEnd('/');
        var method = ctx.Request.HttpMethod;

        switch (method, path)
        {
            case ("GET", "/health"):
                await WriteJsonAsync(ctx.Response, 200, new
                {
                    status = "ok",
                    version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)
                });
                return;

            case ("GET", "/devices"):
                {
                    var devices = (await _adb.GetConnectedDevicesAsync())
                        .Concat(await _ios.GetConnectedDevicesAsync())
                        .Select(d => new { serial = d.Serial, platform = d.Platform.ToString(), state = d.ConnectionState.ToString(), displayName = d.DisplayName });
                    await WriteJsonAsync(ctx.Response, 200, devices);
                    return;
                }

            case ("POST", "/capture/start"):
                {
                    var req = await ReadJsonAsync<CaptureStartRequest>(ctx.Request);
                    if (req?.Serial == null) { await WriteJsonAsync(ctx.Response, 400, new { error = "serial required" }); return; }

                    var device = await FindDeviceAsync(req.Serial);
                    if (device == null) { await WriteJsonAsync(ctx.Response, 404, new { error = "device not found" }); return; }

                    var sessions = new SessionService(_adb, _ios);
                    if (!string.IsNullOrWhiteSpace(req.Out)) sessions.SessionsRootDirectory = req.Out;
                    if (!string.IsNullOrWhiteSpace(req.Package)) PreferencesService.Current.TargetPackageName = req.Package;

                    var session = sessions.CreateSession(device);
                    if (!await sessions.StartCaptureAsync(session))
                    {
                        await WriteJsonAsync(ctx.Response, 500, new { error = "failed to start capture" });
                        return;
                    }

                    lock (_lock) _captures[session.Id] = new CaptureHandle(sessions, session);
                    await WriteJsonAsync(ctx.Response, 200, new { sessionId = session.Id, directory = session.SessionDirectory });
                    return;
                }

            case ("POST", "/capture/stop"):
                {
                    var req = await ReadJsonAsync<CaptureStopRequest>(ctx.Request);
                    CaptureHandle? handle;
                    lock (_lock)
                    {
                        if (req?.SessionId == null || !_captures.TryGetValue(req.SessionId, out handle)) handle = null;
                        else _captures.Remove(req.SessionId);
                    }
                    if (handle == null) { await WriteJsonAsync(ctx.Response, 404, new { error = "session not found" }); return; }

                    handle.Sessions.StopCapture(handle.Session);
                    await WriteJsonAsync(ctx.Response, 200, new { lines = handle.Session.LogLineCount, logFile = handle.Session.LogFilePath });
                    return;
                }

            case ("GET", "/profile/start"):
                {
                    var serial = ctx.Request.QueryString["serial"] ?? string.Empty;
                    var package = ctx.Request.QueryString["package"];
                    if (serial.Length == 0) { await WriteJsonAsync(ctx.Response, 400, new { error = "serial required" }); return; }

                    lock (_lock)
                    {
                        _profiler?.Dispose();
                        _profiler = new AndroidPerformanceProfiler(_adb, serial, string.IsNullOrWhiteSpace(package) ? null : package);
                        _profiler.Start();
                    }
                    await WriteJsonAsync(ctx.Response, 200, new { ok = true });
                    return;
                }

            case ("GET", "/profile/snapshot"):
                {
                    ProfilerSnapshot? latest;
                    lock (_lock) latest = _profiler?.History.LastOrDefault();
                    await WriteJsonAsync(ctx.Response, 200, latest == null ? null : new
                    {
                        latest.Fps,
                        latest.FrameTimeP90Ms,
                        latest.CpuPercent,
                        latest.PssKb,
                        latest.ThermalStatus,
                        latest.BatteryLevel,
                        latest.JankyFrames
                    });
                    return;
                }

            case ("GET", "/profile/stop"):
                {
                    AndroidPerformanceProfiler? profiler;
                    lock (_lock) { profiler = _profiler; _profiler = null; }
                    if (profiler == null) { await WriteJsonAsync(ctx.Response, 404, new { error = "not profiling" }); return; }

                    await profiler.StopAsync();
                    var summary = ProfilerReportWriter.Summarize(profiler.History);
                    profiler.Dispose();
                    await WriteJsonAsync(ctx.Response, 200, new
                    {
                        samples = profiler.History.Count,
                        avgFps = summary.AvgFps,
                        minFps = summary.MinFps,
                        jankyFrames = summary.JankyFrames,
                        maxCpuPercent = summary.MaxCpuPercent,
                        memoryGrowthKb = summary.MemoryGrowthKb,
                        slowSession = summary.SlowSession
                    });
                    return;
                }

            case ("POST", "/soak"):
                {
                    var req = await ReadJsonAsync<SoakRequest>(ctx.Request);
                    if (req?.Serial == null) { await WriteJsonAsync(ctx.Response, 400, new { error = "serial required" }); return; }
                    var seconds = Math.Clamp(req.Seconds, 1, 24 * 3600);

                    Func<CancellationToken, Task> load = async token =>
                    {
                        var i = 0;
                        while (!token.IsCancellationRequested)
                        {
                            await _adb.ExecuteCommandAsync(req.Serial, $"shell input keyevent {82 + (i++ % 4)}");
                            await Task.Delay(200, token);
                        }
                    };

                    var report = await SoakRunner.RunAsync(_adb, req.Serial, req.Package ?? "", TimeSpan.FromSeconds(seconds), load);
                    await WriteJsonAsync(ctx.Response, 200, new
                    {
                        report.Duration.TotalSeconds,
                        report.SampleCount,
                        report.AvgFpsStart,
                        report.AvgFpsEnd,
                        report.FpsDecay,
                        report.MemoryGrowthKb,
                        report.JankyFrames,
                        report.MaxThermalStatus,
                        flagged = report.HasIssues
                    });
                    return;
                }

            default:
                await WriteJsonAsync(ctx.Response, 404, new { error = "not found" });
                return;
        }
    }

    private async Task<DeviceInfo?> FindDeviceAsync(string serial)
        => (await _adb.GetConnectedDevicesAsync()).Concat(await _ios.GetConnectedDevicesAsync())
            .FirstOrDefault(d => d.Serial.Equals(serial, StringComparison.OrdinalIgnoreCase));

    private static async Task<T?> ReadJsonAsync<T>(HttpListenerRequest request)
    {
        using var reader = new StreamReader(request.InputStream);
        var body = await reader.ReadToEndAsync();
        return string.IsNullOrWhiteSpace(body) ? default : JsonSerializer.Deserialize<T>(body, Json);
    }

    private static async Task WriteJsonAsync(HttpListenerResponse response, int statusCode, object? value)
    {
        response.StatusCode = statusCode;
        response.ContentType = "application/json";
        var json = JsonSerializer.Serialize(value, Json);
        await using var writer = new StreamWriter(response.OutputStream);
        await writer.WriteAsync(json);
    }

    private sealed record CaptureHandle(SessionService Sessions, LogSession Session);

    private sealed class CaptureStartRequest { public string? Serial { get; set; } public string? Package { get; set; } public string? Out { get; set; } }
    private sealed class CaptureStopRequest { public string? SessionId { get; set; } }
    private sealed class SoakRequest { public string? Serial { get; set; } public int Seconds { get; set; } = 300; public string? Package { get; set; } }

    public void Dispose()
    {
        _cts?.Cancel();
        _listener?.Stop();
        _listener?.Close();
        lock (_lock)
        {
            foreach (var h in _captures.Values) h.Sessions.StopCapture(h.Session);
            _captures.Clear();
            _profiler?.Dispose();
            _profiler = null;
        }
    }
}
