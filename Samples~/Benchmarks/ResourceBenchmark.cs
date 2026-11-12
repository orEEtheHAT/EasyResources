using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Profiling;
using Debug = UnityEngine.Debug;

namespace EasyResources.Samples
{
    /// <summary>Standalone development-player benchmark. No gameplay input or external packages.</summary>
    [AddComponentMenu("EasyResources/Samples/Resource Benchmark")]
    public sealed partial class ResourceBenchmark : MonoBehaviour
    {
        [SerializeField, Min(3)]
        private int measuredBatches = 15;
        [SerializeField]
        private bool quitWhenFinished = true;
        private static readonly ProfilerMarker Marker = new ProfilerMarker("EasyResources.TransactionBatch");
        private long eventCount;
        [Serializable]
        public sealed class Result
        {
            public string scenario;
            public string costMode;
            public int containers, resourceTypes, costRows, holdsPerContainer, concurrentReservations, batches;
            public double meanCpuMs, medianCpuMs, p95CpuMs;
            public long gcAllocationCount, gcAllocationsPerBatch, gcBytesPerBatch, managedSetupBytes, unityAllocatedBytes, unityReservedBytes, managedHeapBytes;
            public long notifications;
            public bool balancesVerified;
        }

        [Serializable]
        public sealed class Report
        {
            public string utc, unity, platform, os, cpu, scriptingBackend;
            public bool developmentBuild;
            public int logicalProcessors, systemMemoryMB;
            public long gcProbeAllocationCount;
            public string measurement = "Main-thread elapsed CPU section; calibrated GC.Alloc ProfilerRecorder event count, summed in a one-sample buffer. Zero events proves zero bytes; gcBytesPerBatch=-1 means byte size is not available for allocating cases. Memory after warmup/full GC; no rendering/input inside measured section.";
            public List<Result> results = new List<Result>();
            public string error;
        }

        private IEnumerator Start()
        {
            if (!Application.isEditor)
            {
                Application.runInBackground = true;
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = -1;
            }

            var report = new Report
            {
                utc = DateTime.UtcNow.ToString("o"),
                unity = Application.unityVersion,
                platform = Application.platform.ToString(),
                os = SystemInfo.operatingSystem,
                cpu = SystemInfo.processorType,
                logicalProcessors = SystemInfo.processorCount,
                systemMemoryMB = SystemInfo.systemMemorySize,
                developmentBuild = Debug.isDebugBuild,
#if ENABLE_IL2CPP
                scriptingBackend = "IL2CPP",
#else
                scriptingBackend = "Mono",
#endif
            };
            using (var probe = NewAllocationRecorder())
            {
                GC.KeepAlive(new byte[1024]);
                probe.Stop();
                report.gcProbeAllocationCount = probe.Count == 0 ? 0 : probe.GetSample(0).Count;
                if (!probe.Valid || report.gcProbeAllocationCount == 0)
                    report.error = "GC.Alloc recorder failed the allocation positive control. Measurements are invalid.";
            }

            yield return null;
            var scenarios = new[]
            {
                new[]
                {
                    32,
                    2,
                    2,
                    2
                },
                new[]
                {
                    2000,
                    2,
                    2,
                    2
                },
                new[]
                {
                    128,
                    16,
                    8,
                    16
                },
                new[]
                {
                    10000,
                    16,
                    8,
                    16
                }
            };
            var names = new[]
            {
                "typical-small",
                "typical-large",
                "heavy-small",
                "heavy-large"
            };
            for (var scenario = 0; scenario < scenarios.Length; scenario++)
                for (var mode = 0; mode < 2; mode++)
                {
                    yield return null;
                    try
                    {
                        report.results.Add(Measure(names[scenario], scenarios[scenario], mode != 0));
                    }
                    catch (Exception exception)
                    {
                        report.error = exception.ToString();
                        Debug.LogException(exception);
                    }
                }

            try
            {
                AddModuleMeasurements(report);
            }
            catch (Exception exception)
            {
                report.error = exception.ToString();
                Debug.LogException(exception);
            }

            var json = JsonUtility.ToJson(report, true);
            var path = GetOutputPath();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, json);
            Debug.Log("EasyResources benchmark saved to " + path + "\n" + json);
            if (quitWhenFinished && !Application.isEditor)
                Application.Quit(report.error == null ? 0 : 1);
        }

        private Result Measure(string name, int[] parameters, bool recreateCost)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var beforeMemory = GC.GetTotalMemory(false);
            var count = parameters[0];
            var types = parameters[1];
            var rows = parameters[2];
            var holds = parameters[3];
            var definitions = new ResourceDefinition[types];
            var stores = new ResourceStore[count];
            var costs = new ResourceCost[count];
            var authoredRows = new ResourceAmount[count][];
            var tokens = new ResourceReservation[count * holds];
            try
            {
                for (var r = 0; r < types; r++)
                    definitions[r] = ResourceDefinition.Create("Resource " + r);
                var initial = new ResourceAmount[types];
                for (var r = 0; r < types; r++)
                    initial[r] = new ResourceAmount(definitions[r], 1000);
                var initialCost = new ResourceCost(initial);
                for (var c = 0; c < count; c++)
                {
                    stores[c] = new ResourceStore(initialCost, types, holds);
                    stores[c].Changed += CountEvent;
                    authoredRows[c] = new ResourceAmount[rows];
                    for (var r = 0; r < rows; r++)
                        authoredRows[c][r] = new ResourceAmount(definitions[r], c % 3 + 1);
                    costs[c] = new ResourceCost(authoredRows[c]);
                }

                var impossible = new ResourceCost(new ResourceAmount(definitions[0], 1), new ResourceAmount(definitions[1], 1001));
                var times = new double[measuredBatches];
                for (var warm = 0; warm < 3; warm++)
                    using (Marker.Auto())
                        Batch(stores, costs, authoredRows, tokens, holds, impossible, recreateCost);
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                var result = new Result
                {
                    scenario = name,
                    costMode = recreateCost ? "recreate-cost" : "reuse-cost",
                    containers = count,
                    resourceTypes = types,
                    costRows = rows,
                    holdsPerContainer = holds,
                    concurrentReservations = tokens.Length,
                    batches = measuredBatches,
                    managedSetupBytes = GC.GetTotalMemory(false) - beforeMemory,
                    unityAllocatedBytes = Profiler.GetTotalAllocatedMemoryLong(),
                    unityReservedBytes = Profiler.GetTotalReservedMemoryLong(),
                    managedHeapBytes = GC.GetTotalMemory(false)
                };
                eventCount = 0;
                long allocated = 0;
                for (var batch = 0; batch < measuredBatches; batch++)
                {
                    using (var recorder = NewAllocationRecorder())
                    {
                        var started = Stopwatch.GetTimestamp();
                        using (Marker.Auto())
                            Batch(stores, costs, authoredRows, tokens, holds, impossible, recreateCost);
                        var elapsed = Stopwatch.GetTimestamp() - started;
                        recorder.Stop();
                        allocated += recorder.Count == 0 ? 0 : recorder.GetSample(0).Count;
                        times[batch] = elapsed * 1000.0 / Stopwatch.Frequency;
                    }
                }

                for (var c = 0; c < count; c++)
                {
                    if (stores[c].ActiveReservationCount != 0)
                        throw new Exception("A reservation leaked.");
                    for (var r = 0; r < types; r++)
                        if (stores[c].GetTotal(definitions[r]) != 1000 || stores[c].GetReserved(definitions[r]) != 0)
                            throw new Exception("The benchmark changed a steady-state balance.");
                }

                double sum = 0;
                foreach (var time in times)
                    sum += time;
                Array.Sort(times);
                result.meanCpuMs = sum / times.Length;
                result.medianCpuMs = times[times.Length / 2];
                result.p95CpuMs = times[Math.Min(times.Length - 1, (int)Math.Ceiling(times.Length * .95) - 1)];
                result.gcAllocationCount = allocated;
                result.gcAllocationsPerBatch = allocated / times.Length;
                result.gcBytesPerBatch = allocated == 0 ? 0 : -1;
                result.notifications = eventCount;
                result.balancesVerified = true;
                return result;
            }
            finally
            {
                foreach (var store in stores)
                    store?.Dispose();
                foreach (var definition in definitions)
                    if (definition != null)
                        Destroy(definition);
            }
        }

        private void CountEvent() => eventCount++;
        private static ProfilerRecorder NewAllocationRecorder() => ProfilerRecorder.StartNew(ProfilerCategory.Internal, "GC.Alloc", 1, ProfilerRecorderOptions.SumAllSamplesInFrame | ProfilerRecorderOptions.CollectOnlyOnCurrentThread);
        private static void Batch(ResourceStore[] stores, ResourceCost[] costs, ResourceAmount[][] rows, ResourceReservation[] tokens, int holds, ResourceCost impossible, bool recreate)
        {
            for (var c = 0; c < stores.Length; c++)
            {
                if (recreate)
                    costs[c] = new ResourceCost(rows[c]);
                for (var h = 0; h < holds; h++)
                    if (!stores[c].TryReserve(costs[c], out tokens[c * holds + h]))
                        throw new Exception("Unexpected reserve failure.");
            }

            for (var c = 0; c < stores.Length; c++)
            {
                var store = stores[c];
                if (store.TrySpend(impossible))
                    throw new Exception("Atomic insufficient-funds check failed.");
                for (var h = 0; h < holds; h++)
                {
                    var token = tokens[c * holds + h];
                    if ((h & 1) == 0)
                    {
                        if (!token.Commit() || token.Commit())
                            throw new Exception("Commit was not idempotent.");
                        foreach (var row in rows[c])
                            store.Add(row.Resource, row.Amount);
                    }
                    else if (!token.Cancel() || token.Cancel())
                        throw new Exception("Cancel was not idempotent.");
                }

                if (!store.TrySpend(costs[c]))
                    throw new Exception("Unexpected immediate spend failure.");
                foreach (var row in rows[c])
                    store.Add(row.Resource, row.Amount);
            }
        }

        private static string GetOutputPath()
        {
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i + 1 < args.Length; i++)
                if (args[i] == "-easyResourcesReport")
                    return args[i + 1];
            return Path.Combine(Application.persistentDataPath, "EasyResources-benchmark.json");
        }
    }
}
