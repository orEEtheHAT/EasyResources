using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using NUnit.Framework;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.TestTools;

namespace EasyResources.Tests
{
    public sealed class ResourceStoreTests
    {
        private ResourceDefinition wood;
        private ResourceDefinition stone;
        private ResourceStore store;
        private ResourceCost cost;
        private readonly List<ResourceDefinition> extra = new List<ResourceDefinition>();
        [SetUp]
        public void SetUp()
        {
            wood = ResourceDefinition.Create("Wood", "wood");
            stone = ResourceDefinition.Create("Stone", "stone");
            store = new ResourceStore(new ResourceCost(new ResourceAmount(wood, 10), new ResourceAmount(stone, 5)));
            cost = new ResourceCost(new ResourceAmount(wood, 4), new ResourceAmount(stone, 2));
        }

        [TearDown]
        public void TearDown()
        {
            store.Dispose();
            UnityEngine.Object.DestroyImmediate(wood);
            UnityEngine.Object.DestroyImmediate(stone);
            foreach (var resource in extra)
                UnityEngine.Object.DestroyImmediate(resource);
            extra.Clear();
        }

        [Test]
        public void SharedDefinitionsDoNotShareBalances()
        {
            using (var other = new ResourceStore(new ResourceCost(new ResourceAmount(wood, 2))))
            {
                store.Add(wood, 7);
                Assert.That(other.GetTotal(wood), Is.EqualTo(2));
                Assert.That(store.GetTotal(wood), Is.EqualTo(17));
            }
        }

        [Test]
        public void CompoundSpendUpdatesEveryResourceBeforeOneEvent()
        {
            var count = 0;
            store.Changed += () =>
            {
                count++;
                Assert.That(store.GetTotal(wood), Is.EqualTo(6));
                Assert.That(store.GetTotal(stone), Is.EqualTo(3));
            };
            Assert.That(store.TrySpend(cost), Is.True);
            Assert.That(count, Is.EqualTo(1));
        }

        [TestCase(11, 1)]
        [TestCase(1, 6)]
        public void InsufficientOneRowDoesNotChangeAnyBalanceOrNotify(int w, int s)
        {
            var notifications = 0;
            store.Changed += () => notifications++;
            var tooMuch = new ResourceCost(new ResourceAmount(wood, w), new ResourceAmount(stone, s));
            Assert.That(store.CanAfford(tooMuch), Is.False);
            Assert.That(store.TrySpend(tooMuch), Is.False);
            Assert.That(store.TryReserve(tooMuch, out var reservation), Is.False);
            Assert.That(reservation.IsActive, Is.False);
            Assert.That(store.GetTotal(wood), Is.EqualTo(10));
            Assert.That(store.GetTotal(stone), Is.EqualTo(5));
            Assert.That(notifications, Is.Zero);
        }

        [Test]
        public void DuplicateRowsAreSummedBeforeAffordabilityCheck()
        {
            var duplicates = new ResourceCost(new ResourceAmount(wood, 6), new ResourceAmount(wood, 5));
            Assert.That(store.TrySpend(duplicates), Is.False);
            Assert.That(store.GetTotal(wood), Is.EqualTo(10));
        }

        [Test]
        public void TwoConsumersCannotSpendReservedUnits()
        {
            var large = new ResourceCost(new ResourceAmount(wood, 7), new ResourceAmount(stone, 3));
            Assert.That(store.TryReserve(large, out var first), Is.True);
            Assert.That(store.GetTotal(wood), Is.EqualTo(10));
            Assert.That(store.GetReserved(wood), Is.EqualTo(7));
            Assert.That(store.GetAvailable(wood), Is.EqualTo(3));
            Assert.That(store.CanAfford(cost), Is.False);
            Assert.That(store.TryReserve(cost, out _), Is.False);
            Assert.That(store.TrySpend(cost), Is.False);
            Assert.That(first.Commit(), Is.True);
            Assert.That(store.GetTotal(wood), Is.EqualTo(3));
            Assert.That(store.GetReserved(wood), Is.Zero);
        }

        [Test]
        public void ReservationAndRestoreEventsExposeWholeTransactions()
        {
            var observations = new List<long[]>();
            store.Changed += () => observations.Add(new[] { store.GetTotal(wood), store.GetTotal(stone), store.GetReserved(wood), store.GetReserved(stone), store.ActiveReservationCount });
            var saved = store.CaptureState();
            store.TryReserve(cost, out var first);
            first.Cancel();
            store.TryReserve(cost, out var second);
            second.Commit();
            store.RestoreState(saved, new[] { wood, stone });
            Assert.That(observations.Count, Is.EqualTo(5));
            Assert.That(observations[0], Is.EqualTo(new[] { 10, 5, 4, 2, 1 }));
            Assert.That(observations[1], Is.EqualTo(new[] { 10, 5, 0, 0, 0 }));
            Assert.That(observations[2], Is.EqualTo(new[] { 10, 5, 4, 2, 1 }));
            Assert.That(observations[3], Is.EqualTo(new[] { 6, 3, 0, 0, 0 }));
            Assert.That(observations[4], Is.EqualTo(new[] { 10, 5, 0, 0, 0 }));
        }

        [Test]
        public void CopiedReservationCommitsExactlyOnce()
        {
            store.TryReserve(cost, out var reservation);
            var copy = reservation;
            Assert.That(reservation.Commit(), Is.True);
            Assert.That(copy.Commit(), Is.False);
            Assert.That(copy.Cancel(), Is.False);
            Assert.That(copy.IsActive, Is.False);
            Assert.That(store.GetTotal(wood), Is.EqualTo(6));
        }

        [Test]
        public void CompletedHandleIsAlsoSafeInsideItsCompletionEvent()
        {
            store.TryReserve(cost, out var reservation);
            var observed = false;
            store.Changed += () =>
            {
                Assert.That(reservation.Commit(), Is.False);
                Assert.That(reservation.Cancel(), Is.False);
                observed = true;
            };
            Assert.That(reservation.Commit(), Is.True);
            Assert.That(observed, Is.True);
        }

        [Test]
        public void CostCopiesCallerArrayAndActiveReservationKeepsItsCost()
        {
            var rows = new[]
            {
                new ResourceAmount(wood, 4)
            };
            var immutable = new ResourceCost(rows);
            store.TryReserve(immutable, out var reservation);
            rows[0] = new ResourceAmount(wood, 100);
            Assert.That(reservation.Commit(), Is.True);
            Assert.That(store.GetTotal(wood), Is.EqualTo(6));
            Assert.That(store.TrySpend(immutable), Is.True);
            Assert.That(store.GetTotal(wood), Is.EqualTo(2));
        }

        [Test]
        public void CancelAndDisposeReleaseOnlyTheirReservation()
        {
            store.TryReserve(cost, out var first);
            store.TryReserve(cost, out var second);
            Assert.That(first.Cancel(), Is.True);
            Assert.That(first.Cancel(), Is.False);
            Assert.That(first.Commit(), Is.False);
            Assert.That(store.GetTotal(wood), Is.EqualTo(10));
            Assert.That(store.GetReserved(wood), Is.EqualTo(4));
            second.Dispose();
            second.Dispose();
            Assert.That(store.GetAvailable(wood), Is.EqualTo(10));
        }

        [Test]
        public void DefaultAndDisposedOwnerHandlesAreSafe()
        {
            Assert.That(default(ResourceReservation).Commit(), Is.False);
            Assert.That(default(ResourceReservation).Cancel(), Is.False);
            store.TryReserve(cost, out var reservation);
            store.Dispose();
            Assert.That(reservation.IsActive, Is.False);
            Assert.That(reservation.Commit(), Is.False);
            Assert.That(reservation.Cancel(), Is.False);
            Assert.Throws<ObjectDisposedException>(() => store.GetTotal(wood));
        }

        [Test]
        public void ActiveReservationBlocksCaptureAndRestoreWithoutCancellingIt()
        {
            var saved = store.CaptureState();
            store.TryReserve(cost, out var reservation);
            Assert.Throws<InvalidOperationException>(() => store.CaptureState());
            Assert.Throws<InvalidOperationException>(() => store.RestoreState(saved, new[] { wood, stone }));
            Assert.That(reservation.IsActive, Is.True);
            reservation.Cancel();
            Assert.DoesNotThrow(() => store.CaptureState());
        }

        [Test]
        public void SnapshotRoundTripUsesIdsAndCopiesData()
        {
            var saved = store.CaptureState();
            var serialized = JsonUtility.ToJson(saved);
            var decoded = JsonUtility.FromJson<ResourceContainerState>(serialized);
            var renamed = ResourceDefinition.Create("Timber", "wood");
            extra.Add(renamed);
            store.TrySpend(cost);
            Assert.That(store.RestoreState(decoded, new[] { renamed, stone }), Is.Zero);
            Assert.That(store.GetTotal(renamed), Is.EqualTo(10));
            decoded.Resources.Clear();
            Assert.That(store.GetTotal(renamed), Is.EqualTo(10));
            Assert.That(saved.Version, Is.EqualTo(ResourceContainerState.CurrentVersion));
        }

        [Test]
        public void UnknownIdRejectsWholeRestoreByDefault()
        {
            var state = new ResourceContainerState();
            state.Resources.Add(new ResourceStateEntry("wood", 1));
            state.Resources.Add(new ResourceStateEntry("removed-content", 7));
            Assert.Throws<ArgumentException>(() => store.RestoreState(state, new[] { wood, stone }));
            Assert.That(store.GetTotal(wood), Is.EqualTo(10));
            Assert.That(store.GetTotal(stone), Is.EqualTo(5));
            Assert.That(store.RestoreState(state, new[] { wood, stone }, UnknownResourcePolicy.Ignore), Is.EqualTo(1));
            Assert.That(store.GetTotal(wood), Is.EqualTo(1));
            Assert.That(store.GetTotal(stone), Is.Zero);
        }

        [TestCase(-1)]
        [TestCase(0)]
        [TestCase(3)]
        public void UnsupportedVersionIsRejectedAtomically(int version)
        {
            var saved = store.CaptureState();
            saved.Version = version;
            Assert.Throws<ArgumentException>(() => store.RestoreState(saved, new[] { wood, stone }));
            Assert.That(store.GetTotal(wood), Is.EqualTo(10));
        }

        [TestCase("wood", -1)]
        [TestCase("", 2)]
        [TestCase(null, 2)]
        public void MalformedSavedRowIsRejected(string id, int amount)
        {
            var saved = new ResourceContainerState();
            saved.Resources.Add(new ResourceStateEntry(id, amount));
            Assert.Throws<ArgumentException>(() => store.RestoreState(saved, new[] { wood, stone }));
            Assert.That(store.GetTotal(wood), Is.EqualTo(10));
        }

        [Test]
        public void DuplicateSavedIdsAreRejectedEvenWhenUnknownIdsAreIgnored()
        {
            var saved = new ResourceContainerState();
            saved.Resources.Add(new ResourceStateEntry("unknown", 2));
            saved.Resources.Add(new ResourceStateEntry("unknown", 3));
            Assert.Throws<ArgumentException>(() => store.RestoreState(saved, new[] { wood }, UnknownResourcePolicy.Ignore));
            Assert.That(store.GetTotal(wood), Is.EqualTo(10));
        }

        [Test]
        public void DuplicateDefinitionIdsAreRejectedAtAllEntryPoints()
        {
            var duplicate = ResourceDefinition.Create("Other Wood", "wood");
            extra.Add(duplicate);
            Assert.Throws<ArgumentException>(() => store.Add(duplicate, 1));
            Assert.Throws<ArgumentException>(() => store.GetAvailable(duplicate));
            Assert.Throws<ArgumentException>(() => new ResourceCost(new ResourceAmount(wood, 1), new ResourceAmount(duplicate, 1)));
            Assert.Throws<ArgumentException>(() => store.RestoreState(store.CaptureState(), new[] { wood, duplicate, stone }));
        }

        [Test]
        public void NegativeNullAndOverflowInputsDoNotMutateBalances()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => store.Add(wood, -1));
            Assert.Throws<ArgumentNullException>(() => store.Add(null, 1));
            Assert.Throws<ArgumentNullException>(() => store.TrySpend(null));
            Assert.Throws<OverflowException>(() => store.Add(wood, long.MaxValue));
            Assert.Throws<OverflowException>(() => new ResourceCost(new ResourceAmount(wood, long.MaxValue), new ResourceAmount(wood, 1)));
            Assert.Throws<ArgumentException>(() => new ResourceCost(default(ResourceAmount)));
            Assert.That(store.GetTotal(wood), Is.EqualTo(10));
        }

        [Test]
        public void EmptyCostSucceedsWithoutSpendingAndItsReservationStillRequiresCompletion()
        {
            var empty = new ResourceCost();
            var events = 0;
            store.Changed += () => events++;
            Assert.That(store.TrySpend(empty), Is.True);
            Assert.That(events, Is.Zero);
            Assert.That(store.TryReserve(empty, out var reservation), Is.True);
            Assert.That(store.ActiveReservationCount, Is.EqualTo(1));
            Assert.That(reservation.Commit(), Is.True);
            Assert.That(store.GetTotal(wood), Is.EqualTo(10));
        }

        [Test]
        public void EventReentryIsRejectedAndAllObserversSeeTheSameCommittedState()
        {
            var attempts = 0;
            var secondObserver = 0;
            store.Changed += () =>
            {
                Assert.That(store.GetAvailable(wood), Is.EqualTo(6));
                Assert.Throws<InvalidOperationException>(() => store.Add(wood, 1));
                attempts++;
                Assert.Throws<InvalidOperationException>(() => store.TrySpend(cost));
                attempts++;
                Assert.Throws<InvalidOperationException>(() => store.TryReserve(cost, out _));
                attempts++;
                Assert.Throws<InvalidOperationException>(() => store.RestoreState(new ResourceContainerState(), new[] { wood }));
                attempts++;
                Assert.Throws<InvalidOperationException>(() => store.Dispose());
                attempts++;
            };
            store.Changed += () =>
            {
                secondObserver++;
                Assert.That(store.GetTotal(stone), Is.EqualTo(3));
            };
            Assert.That(store.TrySpend(cost), Is.True);
            Assert.That(attempts, Is.EqualTo(5));
            Assert.That(secondObserver, Is.EqualTo(1));
        }

        [Test]
        public void ReservationEventCannotCancelAnotherReservationDuringNotification()
        {
            store.TryReserve(cost, out var first);
            var rejected = false;
            store.Changed += () =>
            {
                Assert.Throws<InvalidOperationException>(() => first.Cancel());
                rejected = true;
            };
            store.TryReserve(cost, out var second);
            Assert.That(rejected, Is.True);
            Assert.That(first.IsActive && second.IsActive, Is.True);
            Assert.That(store.GetReserved(wood), Is.EqualTo(8));
        }

        [Test]
        public void ThrowingObserverDoesNotHideLaterObserversOrChangeTransactionResult()
        {
            var laterCalled = false;
            store.Changed += () => throw new InvalidOperationException("Expected observer failure");
            store.Changed += () => laterCalled = true;
            LogAssert.Expect(LogType.Exception, new Regex("Expected observer failure"));
            Assert.That(store.TrySpend(cost), Is.True);
            Assert.That(laterCalled, Is.True);
        }

        [Test]
        public void SubscriptionChangesTakeEffectOnNextNotification()
        {
            var lateCalls = 0;
            Action late = () => lateCalls++;
            Action first = null;
            first = () =>
            {
                store.Changed -= first;
                store.Changed += late;
            };
            store.Changed += first;
            store.Add(wood, 1);
            Assert.That(lateCalls, Is.Zero);
            store.Add(wood, 1);
            Assert.That(lateCalls, Is.EqualTo(1));
        }

        [Test]
        public void CallsFromAnotherThreadAreRejected()
        {
            Exception readError = null;
            Exception writeError = null;
            var thread = new Thread(() =>
            {
                try
                {
                    store.GetTotal(wood);
                }
                catch (Exception e)
                {
                    readError = e;
                }

                try
                {
                    store.Add(wood, 1);
                }
                catch (Exception e)
                {
                    writeError = e;
                }
            });
            thread.Start();
            Assert.That(thread.Join(2000), Is.True);
            Assert.That(readError, Is.TypeOf<InvalidOperationException>());
            Assert.That(writeError, Is.TypeOf<InvalidOperationException>());
        }

        [Test]
        public void CapacityGrowthAndOldHandlesCannotCorruptNewReservations()
        {
            store.Add(wood, 10000);
            var one = new ResourceCost(new ResourceAmount(wood, 1));
            var handles = new ResourceReservation[4096];
            for (var i = 0; i < handles.Length; i++)
                Assert.That(store.TryReserve(one, out handles[i]), Is.True);
            Assert.That(store.GetReserved(wood), Is.EqualTo(handles.Length));
            var stale = handles[0];
            for (var i = 0; i < handles.Length; i++)
                Assert.That(handles[i].Cancel(), Is.True);
            store.TryReserve(one, out var fresh);
            Assert.That(stale.Commit(), Is.False);
            Assert.That(fresh.IsActive, Is.True);
            Assert.That(store.GetReserved(wood), Is.EqualTo(1));
        }

        [Test]
        public void ManyContainersAndCostsRemainIndependent()
        {
            for (var i = 0; i < 256; i++)
                using (var other = new ResourceStore(new ResourceCost(new ResourceAmount(wood, 20), new ResourceAmount(stone, 10))))
                {
                    var different = new ResourceCost(new ResourceAmount(wood, i % 4 + 1), new ResourceAmount(stone, 1));
                    other.TryReserve(different, out var first);
                    other.TryReserve(different, out var second);
                    first.Commit();
                    second.Cancel();
                    Assert.That(other.GetTotal(wood), Is.EqualTo(20 - (i % 4 + 1)));
                    Assert.That(other.GetReserved(wood), Is.Zero);
                }

            Assert.That(store.GetTotal(wood), Is.EqualTo(10));
        }

        [Test]
        public void WarmFrequentOperationsAllocateNoManagedMemory()
        {
            var options = ProfilerRecorderOptions.SumAllSamplesInFrame | ProfilerRecorderOptions.CollectOnlyOnCurrentThread;
            using (var control = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "GC.Alloc", 1, options))
            {
                GC.KeepAlive(new byte[1024]);
                control.Stop();
                Assert.That(control.Valid && control.Count > 0 && control.GetSample(0).Count > 0, Is.True, "The allocation recorder must detect a deliberately allocated buffer.");
            }

            var notifications = 0;
            store.Changed += () => notifications++;
            for (var i = 0; i < 64; i++)
                HotCycle();
            long allocations;
            using (var recorder = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "GC.Alloc", 1, options))
            {
                for (var i = 0; i < 2048; i++)
                    HotCycle();
                recorder.Stop();
                allocations = recorder.Count == 0 ? 0 : recorder.GetSample(0).Count;
            }

            Assert.That(allocations, Is.Zero);
            Assert.That(notifications, Is.GreaterThan(0));
            Assert.That(store.GetTotal(wood), Is.EqualTo(10));
            void HotCycle()
            {
                store.CanAfford(cost);
                store.GetTotal(wood);
                store.GetAvailable(wood);
                store.GetReserved(wood);
                store.TrySpend(cost);
                store.Add(wood, 4);
                store.Add(stone, 2);
                store.TryReserve(cost, out var cancelled);
                cancelled.Cancel();
                store.TryReserve(cost, out var committed);
                committed.Commit();
                store.Add(wood, 4);
                store.Add(stone, 2);
            }
        }

        [Test]
        public void RandomOperationsMatchIndependentBalanceModel()
        {
            var random = new System.Random(81731);
            var total = 10;
            var reserved = 0;
            var holds = new List<(ResourceReservation handle, int amount)>();
            for (var i = 0; i < 3000; i++)
            {
                var amount = random.Next(1, 8);
                var operation = random.Next(5);
                var price = new ResourceCost(new ResourceAmount(wood, amount));
                if (operation == 0)
                {
                    store.Add(wood, amount);
                    total += amount;
                }
                else if (operation == 1)
                {
                    var expected = total - reserved >= amount;
                    Assert.That(store.TrySpend(price), Is.EqualTo(expected));
                    if (expected)
                        total -= amount;
                }
                else if (operation == 2)
                {
                    var expected = total - reserved >= amount;
                    Assert.That(store.TryReserve(price, out var handle), Is.EqualTo(expected));
                    if (expected)
                    {
                        reserved += amount;
                        holds.Add((handle, amount));
                    }
                }
                else if (holds.Count != 0)
                {
                    var index = random.Next(holds.Count);
                    var entry = holds[index];
                    if (operation == 3)
                    {
                        Assert.That(entry.handle.Commit(), Is.True);
                        total -= entry.amount;
                    }
                    else
                        Assert.That(entry.handle.Cancel(), Is.True);
                    reserved -= entry.amount;
                    holds.RemoveAt(index);
                    Assert.That(entry.handle.Commit(), Is.False);
                }

                Assert.That(store.GetTotal(wood), Is.EqualTo(total));
                Assert.That(store.GetReserved(wood), Is.EqualTo(reserved));
                Assert.That(store.GetAvailable(wood), Is.EqualTo(total - reserved));
                Assert.That(store.GetTotal(stone), Is.EqualTo(5));
            }
        }
    }
}
