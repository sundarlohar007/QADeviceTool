using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LogPro.Helpers;
using LogPro.Models;
using LogPro.Services;
using LogPro.Services.Profiling;

namespace LogPro.Cli;

/// <summary>
/// Local control API (§16) — a loopback-only HTTP surface for CI/Appium harnesses.
/// Runs inside `logpro-cli serve`. Trust boundary: IPv4 loopback plus a per-process API key;
/// the engine is shared with the GUI apps. The API key is printed once by the CLI and is
/// never accepted through a URL/query string.
/// </summary>
public sealed class ControlApiServer : IDisposable, IAsyncDisposable
{
    private const int MaxRequestBodyBytes = 1_048_576;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IAdbService _adb;
    private readonly IIosService _ios;
    private readonly object _lock = new();
    private readonly Dictionary<string, CaptureHandle> _captures = new();
    private readonly string _apiKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly List<Task> _requestTasks = new();
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private AndroidPerformanceProfiler? _profiler;
    private Task? _loop;
    private volatile bool _disposed;
    private readonly SessionService _sessions;
    private readonly SemaphoreSlim _requestSlots = new(8, 8);

    public string ApiKey => _apiKey;

    public ControlApiServer(IAdbService adb, IIosService ios)
    {
        _adb = adb;
        _ios = ios;
        _sessions = new SessionService(adb, ios);
    }

    public void Start(int port)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (port is < 1024 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        if (_listener != null) throw new InvalidOperationException("Control API is already running.");

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

            if (!_requestSlots.Wait(0)) { ctx.Response.StatusCode = 503; ctx.Response.Close(); continue; }
            var request = HandleRequestAsync(ctx);
            lock (_requestTasks) _requestTasks.Add(request);
            _ = request.ContinueWith(completed =>
            {
                lock (_requestTasks) _requestTasks.Remove(completed);
            }, TaskScheduler.Default);
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext ctx)
    {
        try
        {
            if (!IsAuthorized(ctx))
            {
                await WriteJsonAsync(ctx.Response, 401, new { error = "API key required" });
                return;
            }

            await HandleAsync(ctx);
        }
        catch (OperationCanceledException) when (_disposed) { try { ctx.Response.Close(); } catch { } }
        catch (Exception ex)
        {
            AppLogger.Log.Error(ex, "[ControlApi] Request failed");
            try { await WriteJsonAsync(ctx.Response, 500, new { error = SecurityHelper.RedactSensitiveText(ex.Message) }); }
            catch { /* client disconnected */ }
        }
        finally { _requestSlots.Release(); }
    }

    private bool IsAuthorized(HttpListenerContext ctx)
    {
        if (ctx.Request.Url?.AbsolutePath.TrimEnd('/') == "/health") return true;
        if (ctx.Request.RemoteEndPoint?.Address is not { } remote || !IPAddress.IsLoopback(remote)) return false;
        if (!string.IsNullOrWhiteSpace(ctx.Request.Headers["Origin"])) return false;

        var supplied = ctx.Request.Headers["X-LogPro-Api-Key"];
        if (string.IsNullOrEmpty(supplied)) return false;
        var expectedBytes = Encoding.UTF8.GetBytes(_apiKey);
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        return CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
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
                    var req = await ReadJsonAsync<CaptureStartRequest>(ctx.Request, _cts?.Token ?? CancellationToken.None);
                    if (req?.Serial == null) { await WriteJsonAsync(ctx.Response, 400, new { error = "serial required" }); return; }
                    if (!SecurityHelper.IsValidOfflineDeviceSelector(req.Serial))
                    { await WriteJsonAsync(ctx.Response, 400, new { error = "network device selectors are disabled" }); return; }
                    if (!string.IsNullOrWhiteSpace(req.Package) && !SecurityHelper.IsValidPackageName(req.Package))
                    { await WriteJsonAsync(ctx.Response, 400, new { error = "invalid package" }); return; }

                    var device = await FindDeviceAsync(req.Serial);
                    if (device == null) { await WriteJsonAsync(ctx.Response, 404, new { error = "device not found" }); return; }

                    if (_disposed) return;
                    var session = _sessions.CreateSessionAt(device, null, string.IsNullOrWhiteSpace(req.Out) ? _sessions.SessionsRootDirectory : req.Out);
                    session.TargetPackage = req.Package ?? string.Empty;
                    if (!await _sessions.StartCaptureAsync(session, new CaptureOptions(LogcatBuffer.Main, LogcatFormat.ThreadTime, req.Package ?? string.Empty), _cts?.Token ?? CancellationToken.None))
                    {
                        await WriteJsonAsync(ctx.Response, 409, new { error = string.IsNullOrWhiteSpace(session.CaptureError) ? "a capture is already active for this device" : session.CaptureError });
                        return;
                    }
                    lock (_lock)
                    {
                        if (!_disposed) _captures[session.Id] = new CaptureHandle(_sessions, session);
                    }
                    if (_disposed)
                    {
                        _sessions.StopCapture(session);
                        await _sessions.WaitForCaptureStopAsync(session);
                        return;
                    }
                    await WriteJsonAsync(ctx.Response, 200, new { sessionId = session.Id, directory = session.SessionDirectory });
                    return;
                }

            case ("POST", "/capture/stop"):
                {
                    var req = await ReadJsonAsync<CaptureStopRequest>(ctx.Request, _cts?.Token ?? CancellationToken.None);
                    CaptureHandle? handle;
                    lock (_lock)
                    {
                        if (req?.SessionId == null || !_captures.TryGetValue(req.SessionId, out handle)) handle = null;
                        else _captures.Remove(req.SessionId);
                    }
                    if (handle == null) { await WriteJsonAsync(ctx.Response, 404, new { error = "session not found" }); return; }

                    handle.Sessions.StopCapture(handle.Session);
                    await handle.Sessions.WaitForCaptureStopAsync(handle.Session);
                    await WriteJsonAsync(ctx.Response, 200, new { lines = handle.Session.LogLineCount, logFile = handle.Session.LogFilePath, complete = handle.Session.CaptureComplete, error = handle.Session.CaptureError });
                    return;
                }

            case ("GET", "/profile/start"):
                {
                    var serial = ctx.Request.QueryString["serial"] ?? string.Empty;
                    var package = ctx.Request.QueryString["package"];
                    if (serial.Length == 0) { await WriteJsonAsync(ctx.Response, 400, new { error = "serial required" }); return; }
                    if (!SecurityHelper.IsValidOfflineDeviceSelector(serial))
                    { await WriteJsonAsync(ctx.Response, 400, new { error = "network device selectors are disabled" }); return; }
                    if (!string.IsNullOrWhiteSpace(package) && !SecurityHelper.IsValidPackageName(package))
                    { await WriteJsonAsync(ctx.Response, 400, new { error = "invalid package" }); return; }
                    var device = await FindDeviceAsync(serial);
                    if (device is not { Platform: DevicePlatform.Android, ConnectionState: DeviceConnectionState.Online })
                    { await WriteJsonAsync(ctx.Response, 422, new { error = "profile requires an online Android device" }); return; }

                    bool alreadyProfiling;
                    lock (_lock)
                    {
                        alreadyProfiling = _profiler != null;
                        if (_disposed) return;
                        if (!alreadyProfiling)
                        {
                            _profiler = new AndroidPerformanceProfiler(_adb, serial, string.IsNullOrWhiteSpace(package) ? null : package);
                            _profiler.Start();
                        }
                    }
                    if (alreadyProfiling) { await WriteJsonAsync(ctx.Response, 409, new { error = "a profile is already running" }); return; }
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
                    var summary = profiler.Summary;
                    profiler.Dispose();
                    await WriteJsonAsync(ctx.Response, 200, new
                    {
                        samples = profiler.TotalSampleCount,
                        avgFps = summary.AvgFps,
                        minFps = summary.MinFps,
                        jankyFrames = summary.JankyFrames,
                        maxCpuPercent = summary.MaxCpuPercent,
                        memoryGrowthKb = summary.MemoryGrowthKb,
                        slowSession = summary.SlowSession,
                        hasSufficientData = summary.HasSufficientData,
                        verdict = summary.Verdict
                    });
                    return;
                }

            case ("POST", "/soak"):
                {
                    var req = await ReadJsonAsync<SoakRequest>(ctx.Request, _cts?.Token ?? CancellationToken.None);
                    if (req?.Serial == null) { await WriteJsonAsync(ctx.Response, 400, new { error = "serial required" }); return; }
                    if (!SecurityHelper.IsValidOfflineDeviceSelector(req.Serial))
                    { await WriteJsonAsync(ctx.Response, 400, new { error = "network device selectors are disabled" }); return; }
                    if (!string.IsNullOrWhiteSpace(req.Package) && !SecurityHelper.IsValidPackageName(req.Package))
                    { await WriteJsonAsync(ctx.Response, 400, new { error = "invalid package" }); return; }
                    var device = await FindDeviceAsync(req.Serial);
                    if (device is not { Platform: DevicePlatform.Android, ConnectionState: DeviceConnectionState.Online })
                    { await WriteJsonAsync(ctx.Response, 422, new { error = "soak requires an online Android device" }); return; }
                    var seconds = Math.Clamp(req.Seconds, 1, 24 * 3600);

                    Func<CancellationToken, Task> load = async token =>
                    {
                        var i = 0;
                        while (!token.IsCancellationRequested)
                        {
                            await _adb.ExecuteCommandAsync(req.Serial, $"shell input keyevent {82 + (i++ % 4)}", token);
                            await Task.Delay(200, token).ConfigureAwait(false);
                        }
                    };

                    var report = await SoakRunner.RunAsync(_adb, req.Serial, req.Package ?? "", TimeSpan.FromSeconds(seconds), load,
                        cancellationToken: _cts?.Token ?? CancellationToken.None);
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
                        flagged = report.HasIssues,
                        report.HasSufficientData,
                        report.LoadCompletedEarly,
                        report.LoadError
                    });
                    return;
                }

            default:
                await WriteJsonAsync(ctx.Response, 404, new { error = "not found" });
                return;
        }
    }

    private async Task<DeviceInfo?> FindDeviceAsync(string serial)
    {
        var token = _cts?.Token ?? CancellationToken.None;
        var android = await DeviceQueries.DiscoverAsync(_adb, token);
        var ios = await DeviceQueries.DiscoverAsync(_ios, token);
        token.ThrowIfCancellationRequested();
        return android.Concat(ios).FirstOrDefault(d => d.Serial.Equals(serial, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpListenerRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength64 > MaxRequestBodyBytes) return default;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        using var reader = new StreamReader(request.InputStream);
        var chars = new char[8192];
        var body = new StringBuilder();
        int read;
        try
        {
            while ((read = await reader.ReadAsync(chars.AsMemory(), linked.Token).ConfigureAwait(false)) > 0)
            {
                if (body.Length + read > MaxRequestBodyBytes) return default;
                body.Append(chars, 0, read);
            }
        }
        catch (OperationCanceledException)
        {
            return default;
        }

        try { return body.Length == 0 ? default : JsonSerializer.Deserialize<T>(body.ToString(), Json); }
        catch (JsonException) { return default; }
    }

    private static async Task WriteJsonAsync(HttpListenerResponse response, int statusCode, object? value)
    {
        response.StatusCode = statusCode;
        response.ContentType = "application/json";
        response.Headers["Cache-Control"] = "no-store";
        var json = JsonSerializer.Serialize(value, Json);
        await using var writer = new StreamWriter(response.OutputStream);
        await writer.WriteAsync(json);
    }

    private sealed record CaptureHandle(SessionService Sessions, LogSession Session);

    private sealed class CaptureStartRequest { public string? Serial { get; set; } public string? Package { get; set; } public string? Out { get; set; } }
    private sealed class CaptureStopRequest { public string? SessionId { get; set; } }
    private sealed class SoakRequest { public string? Serial { get; set; } public int Seconds { get; set; } = 300; public string? Package { get; set; } }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _cts?.Cancel();
        _listener?.Close();
        if (_loop != null) await _loop.ConfigureAwait(false);
        // Permanently reject starts, including requests already awaiting device discovery.
        await Task.Run(_sessions.StopAllCaptures).ConfigureAwait(false);
        Task[] requests;
        lock (_requestTasks) requests = _requestTasks.ToArray();
        await Task.WhenAll(requests).ConfigureAwait(false);
        AndroidPerformanceProfiler? profiler;
        lock (_lock)
        {
            _captures.Clear();
            profiler = _profiler;
            _profiler = null;
        }
        if (profiler != null) { await profiler.StopAsync().ConfigureAwait(false); profiler.Dispose(); }
        _cts?.Dispose();
    }
}
