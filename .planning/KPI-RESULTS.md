# KPI Results (§19)

> Measured 2026-08-26 on the dev machine (WPF Debug build; CLI Release). GA targets in parens.

## Engine KPIs — `logpro-cli kpi` (Release)

| Probe | Result |
|---|---|
| CSV export, 100k lines | 204.6 ms |
| JSON export, 100k lines | 177.4 ms |
| Tail-read (last 500 of 100k) | 34.0 ms |
| SurfaceFlinger parser (200 × 500 frames) | 38.3 ms (~0.19 ms/parse) |

## Micro-benchmarks — `src/LogPro.Benchmarks` (BenchmarkDotNet, Release)

| Method | Mean | Allocated |
|---|---|---|
| ParseSurfaceFlingerLatency (500 frames) | 55.7 µs | 198 KB |
| SummarizeFrames | 67.3 µs | 212 KB |
| ParseCpuPercent | 475 ns | 1 KB |
| ParseMemInfoTotals | 645 ns | 1.3 KB |
| HashSerial | 342 ns | 360 B |

## App KPIs (WPF, Debug build — Release/published expected equal or better)

| Metric | Result | Target |
|---|---|---|
| Cold start → MainViewModel live | 0.88 s | ≤ 2 s ✅ |
| Idle RAM (WorkingSet) | 174 MB | ≤ ~200 MB ✅ |

## GA gate checklist (§19.1)

- [x] Bundled tools only (adb/scrcpy/pymobiledevice3 real binaries, verified in releases)
- [x] Cold start ≤ 2 s (0.88 s measured)
- [x] Idle RAM ≈ WPF parity ≤ ~200 MB (174 MB measured)
- [ ] 60 fps @ 100K rows — engine-side proxies measured (100k export 200 ms, virtualized UI); full UI-fps measurement pending Avalonia GA
- [ ] Installer ≤ 100 MB — 127 MB today (NativeAOT validated on Avalonia, pending WPF→Avalonia switchover)
- [ ] Code-signed (owner certs) + fresh-OS install tests per platform
