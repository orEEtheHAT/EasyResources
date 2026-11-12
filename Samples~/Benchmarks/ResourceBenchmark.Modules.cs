using System;
using System.Diagnostics;
using System.Numerics;
using EasyResources.Inventory;
using EasyResources.LargeNumbers;
using EasyResources.Networking;
using EasyResources.Production;
using EasyResources.Progression;
using EasyResources.Trading;
using UnityEngine.Profiling;

namespace EasyResources.Samples
{
    public sealed partial class ResourceBenchmark
    {
        private void AddModuleMeasurements(Report report)
        {
            var wood = ResourceDefinition.Create("Wood");
            var gold = ResourceDefinition.Create("Gold");
            var unitWood = new ResourceCost(new ResourceAmount(wood, 1));
            var unitGold = new ResourceCost(new ResourceAmount(gold, 1));
            var initial = new ResourceCost(new ResourceAmount(wood, 10000), new ResourceAmount(gold, 10000));
            var recipe = ProductionRecipe.Create("Cycle", unitWood, unitWood, 0);
            var catalog = TradeCatalog.Create("Shop", new TradeOffer(wood, 1, unitGold, unitGold));
            var item = ItemDefinition.Create("Ore", maximumStack: 10000);
            var node = ProgressionNode.Create("Rank", new[] { new ProgressionRank(unitGold) });
            var tree = ProgressionDefinition.Create("Tree", Array.Empty<long>(), new[] { node });
            try
            {
                using var a = new ResourceStore(initial);
                using var b = new ResourceStore(initial);
                var transfer = new ResourceTransaction();
                var back = new ResourceTransaction();
                transfer.Transfer(a, b, unitWood);
                back.Transfer(b, a, unitWood);
                report.results.Add(MeasureModule("atomic-transfer-pair", () =>
                {
                    if (!transfer.TryExecute() || !back.TryExecute())
                        throw new Exception("Transfer failed.");
                }, () => a.GetTotal(wood) == 10000 && b.GetTotal(wood) == 10000));
                var up = new ResourceCost(new ResourceAmount(wood, 2));
                report.results.Add(MeasureModule("resize-partial-reservation", () =>
                {
                    a.TryReserve(up, out var hold);
                    if (!hold.TryCommit(unitWood, out _) || !hold.TryResize(up, out _))
                        throw new Exception("Partial hold failed.");
                    hold.Cancel();
                    a.Add(wood, 1);
                }, () => a.GetTotal(wood) == 10000 && a.ActiveReservationCount == 0));
                using var queue = new ProductionQueue(a, ownerId: "benchmark");
                var plan = recipe.Prepare();
                report.results.Add(MeasureModule("production-enqueue-complete", () =>
                {
                    if (!queue.TryEnqueue(plan, 0, out _) || queue.Tick(0) != 1)
                        throw new Exception("Production failed.");
                }, () => a.GetTotal(wood) == 10000 && queue.Count == 0));
                var consume = new ResourceFlow(a, new ResourceRate(wood, -1), new ResourceRate(gold, 1));
                var produce = new ResourceFlow(a, new ResourceRate(wood, 1), new ResourceRate(gold, -1));
                report.results.Add(MeasureModule("resource-flow-pair", () =>
                {
                    if (!consume.Tick(1000) || !produce.Tick(1000))
                        throw new Exception("Flow failed.");
                }, () => a.GetTotal(wood) == 10000 && a.GetTotal(gold) == 10000));
                var shop = new ResourceShop(catalog, b, b);
                var buy = shop.QuoteBuy(wood);
                var sell = shop.QuoteSell(wood);
                report.results.Add(MeasureModule("reuse-trade-quote-pair", () =>
                {
                    if (!shop.TryExecute(a, buy, out _) || !shop.TryExecute(a, sell, out _))
                        throw new Exception("Trade failed.");
                }, () => a.GetTotal(wood) == 10000 && a.GetTotal(gold) == 10000));
                using var inventory = new InventoryStore(24);
                inventory.TryAdd(item, 10, out _);
                report.results.Add(MeasureModule("inventory-stack-add-remove", () =>
                {
                    if (!inventory.TryAdd(item, 1, out _) || !inventory.TryRemove(item, 1, out _))
                        throw new Exception("Inventory failed.");
                }, () => inventory.GetCount(item) == 10));
                using var profile = new ProgressionProfile(tree, a);
                var prestige = new PrestigeRule(1, unitGold);
                report.results.Add(MeasureModule("unlock-prestige-pair", () =>
                {
                    if (!profile.TryUnlock(node, out _) || !profile.TryPrestige(prestige, out _))
                        throw new Exception("Progression failed.");
                }, () => a.GetTotal(gold) == 10000 && profile.GetRank(node) == 0));
                var authority = new ResourceAuthority(new[] { gold });
                authority.RegisterStore("wallet", a, "actor");
                var session = authority.OpenSession("actor");
                long sequence = 0;
                report.results.Add(MeasureModule("authority-command-and-refund", () =>
                {
                    var reply = authority.Execute("actor", new ResourceCommand { SessionId = session, Sequence = ++sequence, Source = "wallet", ResourceId = gold.Id, Amount = 1, ExpectedSourceRevision = a.Revision });
                    if (!reply.Success)
                        throw new Exception("Authority failed.");
                    a.Add(gold, 1);
                }, () => a.GetTotal(gold) == 10000));
                var largeAmount = BigInteger.Pow(10, 50);
                using var large = new BigResourceStore(new BigResourceCost(new BigResourceAmount(gold, largeAmount)));
                var largeUnit = new BigResourceCost(new BigResourceAmount(gold, 1));
                report.results.Add(MeasureModule("big-integer-spend-add", () =>
                {
                    if (!large.TrySpend(largeUnit, out _) || !large.TryAdd(largeUnit, out _))
                        throw new Exception("Large economy failed.");
                }, () => large.GetTotal(gold) == largeAmount));
            }
            finally
            {
                Destroy(wood);
                Destroy(gold);
                Destroy(recipe);
                Destroy(catalog);
                Destroy(item);
                Destroy(node);
                Destroy(tree);
            }
        }

        private Result MeasureModule(string name, Action iteration, Func<bool> verify)
        {
            const int iterations = 1000;
            for (var i = 0; i < 64; i++)
                iteration();
            var times = new double[measuredBatches];
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            long allocations = 0;
            var memory = GC.GetTotalMemory(false);
            for (var batch = 0; batch < times.Length; batch++)
            {
                using (var recorder = NewAllocationRecorder())
                {
                    var started = Stopwatch.GetTimestamp();
                    for (var i = 0; i < iterations; i++)
                        iteration();
                    var elapsed = Stopwatch.GetTimestamp() - started;
                    recorder.Stop();
                    allocations += recorder.Count == 0 ? 0 : recorder.GetSample(0).Count;
                    times[batch] = elapsed * 1000.0 / Stopwatch.Frequency;
                }
            }

            if (!verify())
                throw new Exception("Module invariant failed: " + name);
            double sum = 0;
            foreach (var value in times)
                sum += value;
            Array.Sort(times);
            return new Result
            {
                scenario = name,
                costMode = "1000 scenario iterations per batch; cached configuration",
                containers = 2,
                resourceTypes = 2,
                costRows = 1,
                batches = times.Length,
                meanCpuMs = sum / times.Length,
                medianCpuMs = times[times.Length / 2],
                p95CpuMs = times[Math.Min(times.Length - 1, (int)Math.Ceiling(times.Length * .95) - 1)],
                gcAllocationCount = allocations,
                gcAllocationsPerBatch = allocations / times.Length,
                gcBytesPerBatch = allocations == 0 ? 0 : -1,
                managedHeapBytes = memory,
                unityAllocatedBytes = Profiler.GetTotalAllocatedMemoryLong(),
                unityReservedBytes = Profiler.GetTotalReservedMemoryLong(),
                balancesVerified = true
            };
        }
    }
}
