using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace EasyResources.Tests
{
    public sealed class ResourceAdvancedTests
    {
        private ResourceDefinition wood, stone, water;
        private ResourceStore a, b;
        [SetUp]
        public void Setup()
        {
            wood = ResourceDefinition.Create("Wood", "wood", weightPerUnit: 2);
            stone = ResourceDefinition.Create("Stone", "stone", weightPerUnit: 3);
            water = ResourceDefinition.Create("Water", "water", decimalPlaces: 3, unit: "L");
            a = new ResourceStore(new ResourceCost(new ResourceAmount(wood, 10), new ResourceAmount(stone, 5)));
            b = new ResourceStore();
        }

        [TearDown]
        public void Teardown()
        {
            a.Dispose();
            b.Dispose();
            UnityEngine.Object.DestroyImmediate(wood);
            UnityEngine.Object.DestroyImmediate(stone);
            UnityEngine.Object.DestroyImmediate(water);
        }

        private ResourceCost Cost(long w, long s = 0) => new ResourceCost(new ResourceAmount(wood, w), new ResourceAmount(stone, s));
        [Test]
        public void TransferIsAtomicAndBothStoresStayLockedThroughoutNotifications()
        {
            var events = 0;
            a.Changed += () =>
            {
                events++;
                Assert.That(a.GetTotal(wood), Is.EqualTo(6));
                Assert.That(b.GetTotal(wood), Is.EqualTo(4));
                Assert.Throws<InvalidOperationException>(() => b.Add(wood, 1));
            };
            b.Changed += () =>
            {
                events++;
                Assert.That(a.GetTotal(stone), Is.EqualTo(3));
                Assert.Throws<InvalidOperationException>(() => a.Add(stone, 1));
            };
            Assert.That(a.TryTransferTo(b, Cost(4, 2)), Is.True);
            Assert.That(events, Is.EqualTo(2));
        }

        [Test]
        public void DestinationFailureNeverChargesSource()
        {
            b.TrySetLimits(new ResourceLimits(new[] { new ResourceLimit(wood, 3) }), out _);
            Assert.That(a.TryTransferTo(b, Cost(4, 2), out var failure), Is.False);
            Assert.That(failure.Code, Is.EqualTo(ResourceFailureCode.ResourceCapacityExceeded));
            Assert.That(a.GetTotal(wood), Is.EqualTo(10));
            Assert.That(b.GetTotal(stone), Is.Zero);
        }

        [Test]
        public void ExchangeChecksGrossInputsAndFinalCapacity()
        {
            a.TrySetLimits(new ResourceLimits(new[] { new ResourceLimit(wood, 10) }), out _);
            Assert.That(a.TryExchange(Cost(4), Cost(3)), Is.True);
            Assert.That(a.GetTotal(wood), Is.EqualTo(9));
            Assert.That(a.TryExchange(Cost(10), Cost(11)), Is.False);
            Assert.That(a.GetTotal(wood), Is.EqualTo(9));
        }

        [Test]
        public void CompositeTransactionIsReusableAndObserverCannotMutatePlan()
        {
            var plan = new ResourceTransaction().Transfer(a, b, Cost(2));
            a.Changed += () => Assert.Throws<InvalidOperationException>(() => plan.Clear());
            Assert.That(plan.TryExecute(), Is.True);
            Assert.That(plan.TryExecute(), Is.True);
            Assert.That(a.GetTotal(wood), Is.EqualTo(6));
            Assert.That(b.GetTotal(wood), Is.EqualTo(4));
        }

        [Test]
        public void MultipleStoresRollbackWhenLastParticipantCannotPay()
        {
            var plan = new ResourceTransaction().Spend(a, Cost(2)).Spend(b, Cost(1)).Add(a, stone, 4);
            Assert.That(plan.TryExecute(), Is.False);
            Assert.That(a.GetTotal(wood), Is.EqualTo(10));
            Assert.That(a.GetTotal(stone), Is.EqualTo(5));
        }

        [Test]
        public void WeightVolumeFilterAndPartialAcceptanceAreExplicit()
        {
            b.TrySetLimits(new ResourceLimits(acceptedResources: new[] { wood }, maximumWeight: 9, maximumVolume: 3), out _);
            Assert.That(b.TryAdd(stone, 1, out var denied), Is.False);
            Assert.That(denied.Code, Is.EqualTo(ResourceFailureCode.ResourceNotAccepted));
            Assert.That(b.AddUpTo(wood, 100), Is.EqualTo(3));
            Assert.That(b.AddUpTo(wood, 1), Is.Zero);
            Assert.That(b.TrySetLimits(new ResourceLimits(maximumWeight: 1), out _), Is.False);
            Assert.That(b.Limits.MaximumWeight, Is.EqualTo(9));
        }

        [Test]
        public void LongAmountsAndFixedPrecisionRoundTripWithoutFloatLoss()
        {
            a.Add(water, water.ToUnits(1.125m));
            Assert.That(a.GetTotal(water), Is.EqualTo(1125));
            Assert.That(water.FromUnits(1125), Is.EqualTo(1.125m));
            Assert.Throws<ArgumentException>(() => water.ToUnits(.0001m));
            b.Add(wood, 5000000000L);
            Assert.That(b.GetTotal(wood), Is.EqualTo(5000000000L));
            Assert.Throws<OverflowException>(() => b.Add(wood, long.MaxValue));
        }

        [Test]
        public void CostArithmeticIsExactAndDoesNotOverflowIntermediateMultiplication()
        {
            Assert.That(QuantityMath.Scale(long.MaxValue, long.MaxValue, long.MaxValue), Is.EqualTo(long.MaxValue));
            Assert.Throws<ArgumentException>(() => Cost(3).Scale(1, 2));
            Assert.That(Cost(3).Scale(1, 2, ResourceRounding.Up).GetAmount(wood), Is.EqualTo(2));
            Assert.That(ResourceCost.Merge(Cost(2, 1), Cost(3, 2)).Multiply(2).GetAmount(wood), Is.EqualTo(10));
        }

        [Test]
        public void PartialCommitResizeAndCancelPreserveOnlyRemainingHold()
        {
            a.TryReserve(Cost(6, 3), out var hold);
            Assert.That(hold.TryCommit(Cost(2, 1), out _), Is.True);
            Assert.That(a.GetTotal(wood), Is.EqualTo(8));
            Assert.That(a.GetReserved(wood), Is.EqualTo(4));
            Assert.That(hold.TryResize(Cost(7, 3), out _), Is.True);
            Assert.That(hold.TryResize(Cost(9), out _), Is.False);
            Assert.That(a.GetReserved(wood), Is.EqualTo(7));
            Assert.That(hold.Cancel(), Is.True);
            Assert.That(a.GetAvailable(wood), Is.EqualTo(8));
        }

        [Test]
        public void ReservationCompletionAndOutputGrantAreAtomicAndRetryable()
        {
            a.TryReserve(Cost(4), out var hold);
            b.TrySetLimits(new ResourceLimits(new[] { new ResourceLimit(stone, 0) }), out _);
            var plan = new ResourceTransaction().Commit(hold).Add(b, stone, 2);
            Assert.That(plan.CanExecute(out _), Is.False);
            Assert.That(plan.TryExecute(), Is.False);
            Assert.That(hold.IsActive, Is.True);
            b.TrySetLimits(ResourceLimits.Unlimited, out _);
            Assert.That(plan.CanExecute(out _), Is.True);
            Assert.That(plan.TryExecute(), Is.True);
            Assert.That(a.GetTotal(wood), Is.EqualTo(6));
            Assert.That(b.GetTotal(stone), Is.EqualTo(2));
            Assert.That(plan.TryExecute(), Is.False);
            Assert.That(b.GetTotal(stone), Is.EqualTo(2));
        }

        [Test]
        public void OwnerCancellationExpiryAndRenewalAreIndependent()
        {
            a.TryReserve(Cost(2), out var x, out _, new ResourceReservationOptions("x", 100, 0));
            a.TryReserve(Cost(2), out var y, out _, new ResourceReservationOptions("y", 200, 0));
            Assert.That(x.Renew(150), Is.True);
            Assert.That(a.ExpireReservations(100), Is.Zero);
            Assert.That(a.ExpireReservations(150), Is.EqualTo(1));
            Assert.That(x.Commit(), Is.False);
            Assert.That(y.IsActive, Is.True);
            Assert.That(a.CancelAll("y"), Is.EqualTo(1));
            Assert.That(a.GetAvailable(wood), Is.EqualTo(10));
        }

        [Test]
        public void ActiveSnapshotRequiresOwnersAndExplicitRestoreThenRebasesDeadline()
        {
            a.TryReserve(Cost(4), out var old, out _, new ResourceReservationOptions("build-1", 500, 100));
            var saved = a.CaptureState(true, 200);
            Assert.Throws<InvalidOperationException>(() => b.RestoreState(saved, new[] { wood, stone }));
            b.RestoreState(saved, new[] { wood, stone }, new ResourceRestoreOptions(resumeReservations: true, nowMilliseconds: 1000));
            Assert.That(b.TryGetReservation(old.Id, out var resumed), Is.True);
            Assert.That(b.ExpireReservations(1299), Is.Zero);
            Assert.That(b.ExpireReservations(1300), Is.EqualTo(1));
            Assert.That(resumed.Commit(), Is.False);
            Assert.That(b.GetTotal(wood), Is.EqualTo(10));
        }

        [Test]
        public void AnonymousHoldsAreNotSilentlySaved()
        {
            a.TryReserve(Cost(1), out _);
            Assert.Throws<InvalidOperationException>(() => a.CaptureState(true));
        }

        [Test]
        public void RestoringInvalidatesOldHandlesAndScopes()
        {
            var scope = new ResourceReservationScope(a, "job");
            scope.TryReserve(Cost(2), out var old);
            var saved = a.CaptureState(true);
            a.RestoreState(saved, new[] { wood, stone }, new ResourceRestoreOptions(resumeReservations: true, replaceActiveReservations: true));
            scope.Dispose();
            Assert.That(old.Commit(), Is.False);
            Assert.That(a.ActiveReservationCount, Is.EqualTo(1));
            Assert.That(a.TryGetReservation(old.Id, out var fresh), Is.True);
            Assert.That(fresh.Commit(), Is.True);
        }

        [Test]
        public void RestoreAllDoesNotPartiallyApplyInvalidStateAndPublishesWholeGroup()
        {
            var sa = a.CaptureState();
            var sb = b.CaptureState();
            sb.Resources.Add(new ResourceStateEntry("missing", 1));
            Assert.Throws<ArgumentException>(() => ResourceSnapshots.RestoreAll(new[] { a, b }, new[] { sa, sb }, new[] { wood, stone }));
            Assert.That(a.GetTotal(wood), Is.EqualTo(10));
            sb.Resources.Clear();
            sb.Resources.Add(new ResourceStateEntry("stone", 4));
            a.Changed += () =>
            {
                Assert.That(b.GetTotal(stone), Is.EqualTo(4));
                Assert.Throws<InvalidOperationException>(() => b.Add(stone, 1));
            };
            ResourceSnapshots.RestoreAll(new[] { a, b }, new[] { sa, sb }, new[] { wood, stone });
        }

        [Test]
        public void LegacyVersionOneAndAliasPrecisionMigrationAreSupported()
        {
            var old = new ResourceContainerState
            {
                Version = 1
            };
            old.Resources.Add(new ResourceStateEntry("old-water", 2));
            var migrated = ResourceStateMigration.Migrate(old, new[] { water }, new[] { new ResourceIdMigration("old-water", water) });
            b.RestoreState(migrated, new[] { water });
            Assert.That(b.GetTotal(water), Is.EqualTo(2000));
            Assert.That(old.Version, Is.EqualTo(1));
            Assert.That(old.Resources[0].Amount, Is.EqualTo(2));
        }

        [Test]
        public void BoundedHistoryReportsDroppedRevisionsAndDetailedEventsSeeFinalState()
        {
            a.EnableHistory(2);
            var observed = 0;
            a.DetailedChanged += c =>
            {
                observed++;
                Assert.That(a.GetTotal(wood), Is.EqualTo(6));
                Assert.That(a.GetTotal(stone), Is.EqualTo(3));
            };
            a.TrySpend(Cost(4, 2));
            Assert.That(observed, Is.EqualTo(2));
            var history = new List<ResourceChange>();
            a.CopyHistoryTo(history);
            Assert.That(history.Count, Is.EqualTo(2));
            a.DetailedChanged -= null; // Removing an absent subscriber is harmless.
        }

        [Test]
        public void BatchOverflowLeavesQueuedOperationsIntact()
        {
            var batch = new ResourceBatch(2);
            var first = new ResourceTransaction().Transfer(a, b, Cost(1));
            var second = new ResourceTransaction().Transfer(a, b, Cost(2));
            Assert.That(batch.TryEnqueue(first), Is.True);
            Assert.That(batch.TryEnqueue(second), Is.True);
            Assert.That(batch.TryEnqueue(new ResourceTransaction().Spend(a, Cost(3))), Is.False);
            Assert.That(batch.Process(1), Is.EqualTo(1));
            Assert.That(batch.Count, Is.EqualTo(1));
            Assert.That(b.GetTotal(wood), Is.EqualTo(1));
            batch.Process(1);
            Assert.That(b.GetTotal(wood), Is.EqualTo(3));
        }
    }
}
