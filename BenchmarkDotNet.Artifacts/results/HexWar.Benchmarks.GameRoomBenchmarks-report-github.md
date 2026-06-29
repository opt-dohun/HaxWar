```

BenchmarkDotNet v0.13.12, Debian GNU/Linux 12 (bookworm) (container)
Unknown processor
.NET SDK 9.0.315
  [Host]     : .NET 9.0.17 (9.0.1726.26416), Arm64 RyuJIT AdvSIMD
  Job-SQNTCA : .NET 9.0.17 (9.0.1726.26416), Arm64 RyuJIT AdvSIMD

Concurrent=True  Force=True  Server=True  

```
| Method                    | count | Mean          | Error      | StdDev     | Gen0      | Allocated   |
|-------------------------- |------ |--------------:|-----------:|-----------:|----------:|------------:|
| **CreateAndInitialize**       | **?**     |      **1.106 μs** |  **0.0171 μs** |  **0.0133 μs** |    **0.2747** |     **6.72 KB** |
| SimulateOneCompleteGame   | ?     |    165.307 μs |  3.3062 μs |  3.6748 μs |   11.7188 |   292.01 KB |
| **SimulateMultipleGamesPure** | **10**    |  **1,606.845 μs** | **31.5100 μs** | **49.0573 μs** |  **119.1406** |  **2921.72 KB** |
| **SimulateMultipleGamesPure** | **50**    |  **8,104.157 μs** | **18.5569 μs** | **14.4880 μs** |  **593.7500** | **14618.33 KB** |
| **SimulateMultipleGamesPure** | **100**   | **16,127.860 μs** | **93.0435 μs** | **77.6955 μs** | **1187.5000** | **29229.87 KB** |
