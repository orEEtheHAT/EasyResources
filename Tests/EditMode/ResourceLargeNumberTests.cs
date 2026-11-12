using System;
using System.Numerics;
using EasyResources.LargeNumbers;
using NUnit.Framework;

namespace EasyResources.Tests
{
    public sealed class ResourceLargeNumberTests
    {
        private ResourceDefinition gold;
        private BigResourceCost Cost(BigInteger amount) => new BigResourceCost(new BigResourceAmount(gold, amount));
        [SetUp]
        public void SetUp() => gold = ResourceDefinition.Create("Gold");
        [TearDown]
        public void TearDown() => UnityEngine.Object.DestroyImmediate(gold);
        [Test]
        public void ExactLargeQuantitiesReserveCommitTransferAndRoundTrip()
        {
            var huge = BigInteger.Pow(10, 100);
            using var store = new BigResourceStore(Cost(huge));
            using var target = new BigResourceStore();
            Assert.That(store.TryReserve(Cost(huge / 4), out var hold, new ResourceReservationOptions("job")), Is.True);
            Assert.That(store.TrySpend(Cost(huge), out _), Is.False);
            var state = store.CaptureState(true);
            using var restored = BigResourceStore.FromState(state, new[] { gold });
            Assert.That(restored.TryGetReservation(hold.Id, out var rebound), Is.True);
            Assert.That(rebound.Commit(), Is.True);
            Assert.That(rebound.Commit(), Is.False);
            Assert.That(restored.GetTotal(gold), Is.EqualTo(huge * 3 / 4));
            Assert.That(restored.TryTransferTo(target, Cost(huge / 4), out _), Is.True);
            Assert.That(target.GetTotal(gold), Is.EqualTo(huge / 4));
            Assert.That(restored.GetTotal(gold), Is.EqualTo(huge / 2));
        }

        [Test]
        public void LargeBridgeDoesNotLoseFundsOnOrdinaryCapacityFailure()
        {
            using var big = new BigResourceStore(Cost(100));
            using var normal = new ResourceStore(limits: new ResourceLimits(new[] { new ResourceLimit(gold, 2) }));
            var cost = new ResourceCost(new ResourceAmount(gold, 3));
            Assert.That(big.TryExport(normal, cost, out _), Is.False);
            Assert.That(big.GetTotal(gold), Is.EqualTo(new BigInteger(100)));
            normal.TrySetLimits(ResourceLimits.Unlimited, out _);
            var coherent = false;
            normal.Changed += () => coherent = big.GetTotal(gold) == 97;
            Assert.That(big.TryExport(normal, cost, out _), Is.True);
            Assert.That(coherent, Is.True);
            Assert.That(big.TryImport(normal, cost, out _), Is.True);
            Assert.That(big.GetTotal(gold), Is.EqualTo(new BigInteger(100)));
        }

        [Test]
        public void LargeMalformedSnapshotAndOverheldStateAreRejected()
        {
            using var store = new BigResourceStore(Cost(10));
            var state = store.CaptureState();
            state.Resources[0].Amount = "-1";
            Assert.Throws<ArgumentException>(() => BigResourceStore.FromState(state, new[] { gold }));
            state.Resources[0].Amount = "10";
            state.Reservations.Add(new BigReservationState { Id = 1, OwnerId = "job", Resources = new System.Collections.Generic.List<BigResourceRow> { new BigResourceRow { ResourceId = gold.Id, Amount = "11" } } });
            Assert.Throws<ArgumentException>(() => BigResourceStore.FromState(state, new[] { gold }));
        }

        [Test]
        public void LargeCapacityFailureRollsBackBothSides()
        {
            using var a = new BigResourceStore(Cost(20));
            using var b = new BigResourceStore(maximums: Cost(5));
            Assert.That(a.TryTransferTo(b, Cost(6), out _), Is.False);
            Assert.That(a.GetTotal(gold), Is.EqualTo(new BigInteger(20)));
            Assert.That(b.GetTotal(gold), Is.EqualTo(BigInteger.Zero));
        }
    }
}
