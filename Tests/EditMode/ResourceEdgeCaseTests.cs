using System;
using System.Collections.Generic;
using System.Numerics;
using EasyResources.LargeNumbers;
using EasyResources.Networking;
using EasyResources.Production;
using NUnit.Framework;

namespace EasyResources.Tests
{
    public sealed class ResourceEdgeCaseTests
    {
        private ResourceDefinition a, b;
        [Test]
        public void WrongThreadCannotDequeueOrUnfreezeAnOperation()
        {
            using var store = new ResourceStore();
            var plan = new ResourceTransaction().Add(store, a, 1);
            var batch = new ResourceBatch();
            batch.TryEnqueue(plan);
            Assert.Throws<InvalidOperationException>(() => System.Threading.Tasks.Task.Run(() => batch.Process(1)).GetAwaiter().GetResult());
            Assert.That(batch.Count, Is.EqualTo(1));
            Assert.Throws<InvalidOperationException>(() => plan.Clear());
            Assert.That(batch.Process(1), Is.EqualTo(1));
            Assert.That(store.GetTotal(a), Is.EqualTo(1));
        }

        [Test]
        public void SingleStoreFastPathsMatchGeneralTransactionsAcrossRandomFailuresAndHolds()
        {
            var limits = new ResourceLimits(new[] { new ResourceLimit(a, 12), new ResourceLimit(b, 12) }, maximumWeight: 18, maximumVolume: 18);
            using var direct = new ResourceStore(new ResourceCost(new ResourceAmount(a, 6), new ResourceAmount(b, 6)), limits: limits, historyCapacity: 32);
            using var general = new ResourceStore(new ResourceCost(new ResourceAmount(a, 6), new ResourceAmount(b, 6)), limits: limits, historyCapacity: 32);
            var transaction = new ResourceTransaction();
            var random = new Random(1729);
            var directEvents = 0;
            var generalEvents = 0;
            direct.Changed += () => directEvents++;
            general.Changed += () => generalEvents++;
            for (var step = 0; step < 300; step++)
            {
                var amount = random.Next(0, 5);
                var resource = random.Next(2) == 0 ? a : b;
                var cost = Cost(resource, amount);
                bool x, y;
                switch (random.Next(4))
                {
                    case 0:
                        x = direct.TryAdd(resource, amount, out _);
                        y = transaction.Clear().Add(general, resource, amount).TryExecute(out _, kind: ResourceChangeKind.Add);
                        break;
                    case 1:
                        x = direct.TrySpend(cost);
                        y = transaction.Clear().Spend(general, cost).TryExecute(out _, kind: ResourceChangeKind.Spend);
                        break;
                    default:
                        x = direct.TryReserve(cost, out var first);
                        y = general.TryReserve(cost, out var second);
                        if (x && y)
                        {
                            var part = Cost(resource, amount / 2);
                            Assert.That(first.TryCommit(part, out _), Is.EqualTo(transaction.Clear().Commit(second, part).TryExecute(out _, kind: ResourceChangeKind.Commit)));
                            Assert.That(first.Commit(), Is.EqualTo(transaction.Clear().Commit(second).TryExecute(out _, kind: ResourceChangeKind.Commit)));
                        }

                        break;
                }

                Assert.That(x, Is.EqualTo(y), "result at " + step);
                Assert.That(direct.GetTotal(a), Is.EqualTo(general.GetTotal(a)));
                Assert.That(direct.GetTotal(b), Is.EqualTo(general.GetTotal(b)));
                Assert.That(direct.GetReserved(a), Is.EqualTo(general.GetReserved(a)));
                Assert.That(direct.GetReserved(b), Is.EqualTo(general.GetReserved(b)));
                Assert.That(direct.Revision, Is.EqualTo(general.Revision));
                Assert.That(directEvents, Is.EqualTo(generalEvents));
                Assert.That(direct.UsedWeight, Is.EqualTo(general.UsedWeight));
            }

            var left = new List<ResourceChange>();
            var right = new List<ResourceChange>();
            direct.CopyHistoryTo(left);
            general.CopyHistoryTo(right);
            Assert.That(left.Count, Is.EqualTo(right.Count));
            for (var i = 0; i < left.Count; i++)
            {
                Assert.That(left[i].Revision, Is.EqualTo(right[i].Revision));
                Assert.That(left[i].Kind, Is.EqualTo(right[i].Kind));
                Assert.That(left[i].TotalDelta, Is.EqualTo(right[i].TotalDelta));
                Assert.That(left[i].ReservedDelta, Is.EqualTo(right[i].ReservedDelta));
            }
        }

        [Test]
        public void EveryFailureCanBeDisplayedWithoutAnOptionalResourceReference()
        {
            foreach (ResourceFailureCode code in Enum.GetValues(typeof(ResourceFailureCode)))
                Assert.That(new ResourceFailure(code).ToString(), Is.Not.Null.And.Not.Empty);
        }

        private static ResourceCost Cost(ResourceDefinition resource, long amount) => new ResourceCost(new ResourceAmount(resource, amount));
        [SetUp]
        public void SetUp()
        {
            a = ResourceDefinition.Create("A");
            b = ResourceDefinition.Create("B");
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(a);
            UnityEngine.Object.DestroyImmediate(b);
        }

        [Test]
        public void HistoryTracksAnIncompleteEvictedTransactionAndForcesFullReplication()
        {
            using var store = new ResourceStore(historyCapacity: 1);
            var sender = new ResourceReplicator(store, 1);
            new ResourceTransaction().Add(store, a, 1).Add(store, b, 1).TryExecute();
            var history = new List<ResourceChange>();
            store.CopyHistoryTo(history);
            Assert.That(history.Count, Is.EqualTo(1));
            Assert.That(store.DroppedHistoryThroughRevision, Is.EqualTo(store.Revision));
            Assert.That(sender.Capture(sender.StreamId, 0).IsFull, Is.True);
        }

        [Test]
        public void BatchFreezesEnqueuedPlansAndDefersNewWorkUntilTheNextProcessCall()
        {
            using var store = new ResourceStore();
            var first = new ResourceTransaction().Add(store, a, 1);
            var second = new ResourceTransaction().Add(store, b, 1);
            var batch = new ResourceBatch(2);
            Assert.That(batch.TryEnqueue(first), Is.True);
            Assert.Throws<InvalidOperationException>(() => first.Add(store, a, 10));
            Assert.Throws<InvalidOperationException>(() => first.TryExecute());
            var queued = false;
            store.Changed += () =>
            {
                if (!queued)
                {
                    queued = true;
                    Assert.That(batch.TryEnqueue(second), Is.True);
                }
            };
            Assert.That(batch.Process(2), Is.EqualTo(1));
            Assert.That(store.GetTotal(b), Is.Zero);
            Assert.That(batch.Process(2), Is.EqualTo(1));
            Assert.That(store.GetTotal(b), Is.EqualTo(1));
            first.Clear();
        }

        [Test]
        public void ZeroBigCapacityIsExplicitAndExpiredBigHoldsAreNotCreated()
        {
            using var store = new BigResourceStore(maximums: new BigResourceCost(new BigResourceAmount(a, BigInteger.Zero)));
            Assert.That(store.TryAdd(new BigResourceCost(new BigResourceAmount(a, 1)), out _), Is.False);
            Assert.That(store.TryReserve(new BigResourceCost(), out _, new ResourceReservationOptions("expired", 10, 10)), Is.False);
            Assert.That(store.ActiveReservationCount, Is.Zero);
        }

        [Test]
        public void InvalidAuthoritySnapshotCannotReplaceMoneyOrReplayWatermarks()
        {
            using var wallet = new ResourceStore(Cost(a, 5));
            var authority = new ResourceAuthority(new[] { a });
            authority.RegisterStore("wallet", wallet, "owner");
            var session = authority.OpenSession("owner");
            var snapshot = authority.CaptureState();
            snapshot.Sessions[0].LastSequence = 99;
            snapshot.Stores[0].State.Resources[0] = new ResourceStateEntry(a.Id, -1);
            Assert.Throws<ArgumentException>(() => authority.RestoreState(snapshot));
            Assert.That(wallet.GetTotal(a), Is.EqualTo(5));
            Assert.That(authority.Execute("owner", new ResourceCommand { SessionId = session, Sequence = 1, Source = "wallet", ResourceId = a.Id, Amount = 1, ExpectedSourceRevision = 0 }).Success, Is.True);
        }

        [Test]
        public void ReplicaRejectsReservedBalanceWithoutReservationsAndCanRetryInitialSnapshot()
        {
            using var replica = new ResourceReplica(new[] { a });
            var packet = new ResourcePacket
            {
                StreamId = "stream",
                IsFull = true,
                Revision = 1
            };
            packet.Resources.Add(new ReplicatedBalance(new ResourceBalance(a, 10, 5)));
            Assert.That(replica.TryApply(packet, out _), Is.False);
            Assert.That(replica.StreamId, Is.Null);
            Assert.That(replica.Store.GetTotal(a), Is.Zero);
            packet.ReservationCount = 1;
            Assert.That(replica.TryApply(packet, out _), Is.True);
            Assert.That(replica.Store.GetAvailable(a), Is.EqualTo(5));
        }

        [Test]
        public void OfflineProductionCatchupRespectsCompletionBudget()
        {
            var recipe = ProductionRecipe.Create("Recipe", Cost(a, 1), Cost(b, 1), 100);
            try
            {
                using var source = new ResourceStore(Cost(a, 10));
                using var queue = new ProductionQueue(source, ownerId: "queue");
                for (var i = 0; i < 4; i++)
                    queue.TryEnqueue(recipe, 0, out _);
                var state = queue.CaptureState(10);
                using var restored = new ResourceStore();
                restored.RestoreState(source.CaptureState(true, 10), new[] { a, b }, new ResourceRestoreOptions(resumeReservations: true, nowMilliseconds: 0));
                using var rebound = ProductionQueue.FromState(restored, restored, state, new[] { recipe }, new[] { a, b }, 0, offlineMilliseconds: 1000);
                Assert.That(rebound.Tick(0, 2), Is.EqualTo(2));
                Assert.That(rebound.Count, Is.EqualTo(2));
                Assert.That(rebound.Tick(0, 2), Is.EqualTo(2));
                Assert.That(restored.GetTotal(b), Is.EqualTo(4));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(recipe);
            }
        }

        [Test]
        public void FlowOverflowDoesNotConsumeInputsOrChangeRemainders()
        {
            using var store = new ResourceStore(Cost(a, 10));
            var flow = new ResourceFlow(store, new ResourceRate(a, -1), new ResourceRate(b, long.MaxValue));
            Assert.That(flow.Tick(long.MaxValue), Is.False);
            Assert.That(flow.LastFailure.Code, Is.EqualTo(ResourceFailureCode.AmountOverflow));
            Assert.That(store.GetTotal(a), Is.EqualTo(10));
            Assert.That(flow.CaptureState().Rates[0].Remainder, Is.Zero);
        }

        [Test]
        public void MigrationAlsoConvertsFrozenOperationRowsWithoutMutatingOriginal()
        {
            var precise = ResourceDefinition.Create("Precise", decimalPlaces: 3);
            try
            {
                var original = new List<ResourceStateEntry>
                {
                    new ResourceStateEntry("old", 2)
                };
                var converted = ResourceStateMigration.MigrateRows(original, new[] { precise }, new[] { new ResourceIdMigration("old", precise) });
                Assert.That(converted[0].Amount, Is.EqualTo(2000));
                Assert.That(original[0].Amount, Is.EqualTo(2));
                Assert.That(converted[0].DecimalPlaces, Is.EqualTo(3));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(precise);
            }
        }
    }
}
