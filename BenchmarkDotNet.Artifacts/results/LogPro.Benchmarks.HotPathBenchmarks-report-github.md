```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200.9168)
11th Gen Intel Core i7-11800H 2.30GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.202
  [Host]   : .NET 10.0.6 (10.0.626.17701), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  ShortRun : .NET 10.0.6 (10.0.626.17701), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                     | Mean        | Error       | StdDev      | Gen0    | Gen1   | Allocated |
|--------------------------- |------------:|------------:|------------:|--------:|-------:|----------:|
| ParseSurfaceFlingerLatency | 55,681.6 ns | 71,885.4 ns | 3,940.28 ns | 16.1133 | 3.9673 |  202496 B |
| SummarizeFrames            | 67,348.1 ns | 14,141.7 ns |   775.16 ns | 17.2119 | 3.4180 |  217064 B |
| ParseCpuPercent            |    475.1 ns |    544.3 ns |    29.84 ns |  0.0839 |      - |    1056 B |
| ParseMemInfoTotals         |    645.0 ns |    864.6 ns |    47.39 ns |  0.1068 |      - |    1352 B |
| HashSerial                 |    341.5 ns |    356.8 ns |    19.55 ns |  0.0286 |      - |     360 B |
