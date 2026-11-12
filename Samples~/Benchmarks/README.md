# Benchmarks

**Compatibility:** EasyResources 0.3.0+. No prerequisite sample imports.

Open **ResourceBenchmark.unity** and build it as a **Windows x86_64 Development Build**. Include only this scene. Defaults: 15 measured batches per case, three warmup batches, automatic player exit.

```powershell
ResourceBenchmark.exe -batchmode -nographics -easyResourcesReport "C:\Temp\EasyResources-report.json"
```

The report contains four workloads, each with reused and recreated costs. The largest workload has 10,000 independent stores, 16 resource types, 8 cost rows and 160,000 simultaneous reservations. It checks final balances, atomic failures and repeated Commit/Cancel calls.

`gcProbeAllocationCount` must be positive. `gcAllocationCount = 0` proves zero GC allocations in the measured operation. When allocations exist, `gcBytesPerBatch = -1` means their size in bytes is not measured; use `gcAllocationsPerBatch` for the event count. The recorder sums samples, so its one-element buffer does not truncate events within the synchronous batch.

The scene also runs in Editor without quitting it, but use Player results for performance comparisons. See the package's **Documentation~/Validation.md** for measured CPU, GC and memory, methodology and limitations.
