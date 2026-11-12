using System;
using System.Collections.Generic;
using EasyResources.Progression;
using NUnit.Framework;

namespace EasyResources.Tests
{
    public sealed class ResourceProgressionTests
    {
        private readonly List<UnityEngine.Object> assets = new List<UnityEngine.Object>();
        private T Keep<T>(T value)
            where T : UnityEngine.Object
        {
            assets.Add(value);
            return value;
        }

        private ResourceDefinition gold;
        private ResourceCost Cost(long amount) => new ResourceCost(new ResourceAmount(gold, amount));
        [SetUp]
        public void SetUp()
        {
            gold = Keep(ResourceDefinition.Create("Gold"));
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var asset in assets)
                UnityEngine.Object.DestroyImmediate(asset);
            assets.Clear();
        }

        [Test]
        public void LevelsPrerequisitesRanksAndModifiersAreConsistentDuringPaymentEvents()
        {
            var first = Keep(ProgressionNode.Create("First", new[] { new ProgressionRank(Cost(2), new ProgressionModifier("speed", 5)) }, minimumLevel: 2));
            var second = Keep(ProgressionNode.Create("Second", new[] { new ProgressionRank(Cost(3), new ProgressionModifier("speed", 2000, ProgressionModifierKind.PercentBasisPoints)) }, new[] { new ProgressionRequirement(first) }));
            var definition = Keep(ProgressionDefinition.Create("Tree", new long[] { 10, 30 }, new[] { first, second }));
            using var wallet = new ResourceStore(Cost(20));
            using var profile = new ProgressionProfile(definition, wallet);
            Assert.That(profile.TryUnlock(first, out _), Is.False);
            Assert.That(profile.TryUnlock(second, out _), Is.False);
            profile.AddExperience(10, out _);
            Assert.That(profile.Level, Is.EqualTo(2));
            Assert.That(profile.ExperienceToNextLevel, Is.EqualTo(20));
            var coherent = false;
            wallet.Changed += () => coherent = profile.GetRank(first) == 1;
            Assert.That(profile.TryUnlock(first, out _), Is.True);
            Assert.That(coherent, Is.True);
            Assert.That(profile.TryUnlock(second, out _), Is.True);
            Assert.That(profile.GetStat("speed", 10), Is.EqualTo(18));
            Assert.That(profile.TryUnlock(first, out _), Is.False);
        }

        [Test]
        public void ExclusiveBranchesAndPrestigeUseOneAtomicOperation()
        {
            var a = Keep(ProgressionNode.Create("A", new[] { new ProgressionRank(Cost(2)) }, exclusiveGroup: "class"));
            var b = Keep(ProgressionNode.Create("B", new[] { new ProgressionRank(Cost(2)) }, exclusiveGroup: "class"));
            var permanent = Keep(ProgressionNode.Create("Permanent", new[] { new ProgressionRank(Cost(1)) }, keepOnPrestige: true));
            var definition = Keep(ProgressionDefinition.Create("Tree", new long[] { 10 }, new[] { a, b, permanent }));
            using var wallet = new ResourceStore(Cost(10));
            using var profile = new ProgressionProfile(definition, wallet);
            profile.TryUnlock(a, out _);
            profile.TryUnlock(permanent, out _);
            Assert.That(profile.TryUnlock(b, out _), Is.False);
            profile.AddExperience(10, out _);
            var consistent = false;
            wallet.Changed += () => consistent = profile.Level == 1 && profile.PrestigeCount == 1 && profile.GetRank(a) == 0 && profile.GetRank(permanent) == 1;
            Assert.That(profile.TryPrestige(new PrestigeRule(2, Cost(5), Cost(2)), out _), Is.True);
            Assert.That(consistent, Is.True);
            Assert.That(wallet.GetTotal(gold), Is.EqualTo(10));
        }

        [Test]
        public void ResearchRestoreConsumesOnlyItsOwnedPaymentAfterRemainingDuration()
        {
            var node = Keep(ProgressionNode.Create("Research", new[] { new ProgressionRank(Cost(4)) }, researchMilliseconds: 100));
            var definition = Keep(ProgressionDefinition.Create("Research Tree", Array.Empty<long>(), new[] { node }));
            using var wallet = new ResourceStore(Cost(10));
            using var profile = new ProgressionProfile(definition, wallet);
            Assert.That(profile.TryStartResearch(node, 10, "research-1", out _), Is.True);
            Assert.That(profile.TryCompleteResearch(50, out _), Is.False);
            var state = profile.CaptureState(50);
            var funds = wallet.CaptureState(true, 50);
            using var restored = new ResourceStore();
            restored.RestoreState(funds, new[] { gold }, new ResourceRestoreOptions(resumeReservations: true, nowMilliseconds: 1000));
            using var rebound = ProgressionProfile.FromState(definition, restored, state, new[] { gold }, 1000);
            Assert.That(rebound.TryCompleteResearch(1059, out _), Is.False);
            var consistent = false;
            restored.Changed += () => consistent = rebound.GetRank(node) == 1 && rebound.Researching == null;
            Assert.That(rebound.TryCompleteResearch(1060, out _), Is.True);
            Assert.That(consistent, Is.True);
            Assert.That(restored.GetTotal(gold), Is.EqualTo(6));
            Assert.That(rebound.TryCompleteResearch(1061, out _), Is.False);
        }

        [Test]
        public void ResearchCancelReleasesInputsAndAllowsAnotherOperation()
        {
            var node = Keep(ProgressionNode.Create("Research", new[] { new ProgressionRank(Cost(4)) }, researchMilliseconds: 100));
            var definition = Keep(ProgressionDefinition.Create("Tree", Array.Empty<long>(), new[] { node }));
            using var wallet = new ResourceStore(Cost(4));
            using var profile = new ProgressionProfile(definition, wallet);
            profile.TryStartResearch(node, 0, "one", out _);
            Assert.That(wallet.GetAvailable(gold), Is.Zero);
            Assert.That(profile.CancelResearch(), Is.True);
            Assert.That(wallet.GetAvailable(gold), Is.EqualTo(4));
            Assert.That(profile.TryStartResearch(node, 1, "two", out _), Is.True);
        }

        [Test]
        public void InvalidGraphAndXpOverflowLeaveExistingStateIntact()
        {
            var outside = Keep(ProgressionNode.Create("Outside", new[] { new ProgressionRank(Cost(0)) }));
            var node = Keep(ProgressionNode.Create("Node", new[] { new ProgressionRank(Cost(0)) }, new[] { new ProgressionRequirement(outside) }));
            var invalid = Keep(ProgressionDefinition.Create("Invalid", new long[] { 1 }, new[] { node }));
            using var wallet = new ResourceStore();
            Assert.Throws<ArgumentException>(() => new ProgressionProfile(invalid, wallet));
            var valid = Keep(ProgressionDefinition.Create("Valid", Array.Empty<long>()));
            using var profile = new ProgressionProfile(valid, wallet);
            Assert.That(profile.AddExperience(long.MaxValue, out _), Is.True);
            Assert.That(profile.AddExperience(1, out var failure), Is.False);
            Assert.That(failure.Code, Is.EqualTo(ResourceFailureCode.AmountOverflow));
            Assert.That(profile.Experience, Is.EqualTo(long.MaxValue));
        }
    }
}
