using System;
using EasyResources.Networking;
using NUnit.Framework;

namespace EasyResources.Tests
{
    public sealed class ResourceNetworkTests
    {
        private ResourceDefinition gold;
        private ResourceCost Cost(long amount) => new ResourceCost(new ResourceAmount(gold, amount));
        [SetUp]
        public void SetUp() => gold = ResourceDefinition.Create("Gold");
        [TearDown]
        public void TearDown() => UnityEngine.Object.DestroyImmediate(gold);
        private ResourceCommand Spend(string session, long sequence, long revision, long amount = 1) => new ResourceCommand
        {
            SessionId = session,
            Sequence = sequence,
            Source = "wallet",
            ResourceId = gold.Id,
            Amount = amount,
            ExpectedSourceRevision = revision
        };
        [Test]
        public void AuthorityAuthenticatesActorAndRepeatedCommandCannotSpendTwice()
        {
            using var wallet = new ResourceStore(Cost(10));
            var authority = new ResourceAuthority(new[] { gold });
            authority.RegisterStore("wallet", wallet, "alice");
            var session = authority.OpenSession("alice");
            var command = Spend(session, 1, 0, 3);
            Assert.That(authority.Execute("mallory", command).Failure, Is.EqualTo(ResourceFailureCode.Unauthorized));
            Assert.That(authority.Execute("alice", command).Success, Is.True);
            var replay = authority.Execute("alice", command);
            Assert.That(replay.Success, Is.True);
            replay.Success = false;
            Assert.That(authority.Execute("alice", command).Success, Is.True);
            Assert.That(wallet.GetTotal(gold), Is.EqualTo(7));
            command.Amount = 4;
            Assert.That(authority.Execute("alice", command).Failure, Is.EqualTo(ResourceFailureCode.InvalidCommand));
            Assert.That(wallet.GetTotal(gold), Is.EqualTo(7));
        }

        [Test]
        public void EvictedAndRestoredCommandWatermarksStillPreventReplay()
        {
            using var wallet = new ResourceStore(Cost(10));
            var authority = new ResourceAuthority(new[] { gold }, replayCapacity: 1);
            authority.RegisterStore("wallet", wallet, "alice");
            var session = authority.OpenSession("alice");
            var first = Spend(session, 1, 0);
            Assert.That(authority.Execute("alice", first).Success, Is.True);
            Assert.That(authority.Execute("alice", Spend(session, 2, 1)).Success, Is.True);
            Assert.That(authority.Execute("alice", first).Failure, Is.EqualTo(ResourceFailureCode.ExpiredCommand));
            var state = authority.CaptureState();
            authority.RestoreState(state);
            Assert.That(authority.Execute("alice", Spend(session, 2, 1)).Failure, Is.EqualTo(ResourceFailureCode.ExpiredCommand));
            Assert.That(wallet.GetTotal(gold), Is.EqualTo(8));
            Assert.That(authority.Execute("alice", Spend(session, 3, wallet.Revision)).Success, Is.True);
            Assert.That(wallet.GetTotal(gold), Is.EqualTo(7));
        }

        [Test]
        public void FailedAndStaleCommandsConsumeSequenceButGapsDoNot()
        {
            using var wallet = new ResourceStore(Cost(2));
            var authority = new ResourceAuthority(new[] { gold });
            authority.RegisterStore("wallet", wallet, "alice");
            var session = authority.OpenSession("alice");
            Assert.That(authority.Execute("alice", Spend(session, 2, 0)).Failure, Is.EqualTo(ResourceFailureCode.InvalidCommand));
            Assert.That(authority.Execute("alice", Spend(session, 1, 4)).Failure, Is.EqualTo(ResourceFailureCode.StaleRevision));
            Assert.That(authority.Execute("alice", Spend(session, 2, 0, 3)).Failure, Is.EqualTo(ResourceFailureCode.InsufficientResource));
            Assert.That(authority.Execute("alice", Spend(session, 3, 0, 2)).Success, Is.True);
            Assert.That(wallet.GetTotal(gold), Is.Zero);
        }

        [Test]
        public void IncomingTransferPolicyProtectsForeignStores()
        {
            using var wallet = new ResourceStore(Cost(5));
            using var target = new ResourceStore();
            var authority = new ResourceAuthority(new[] { gold });
            authority.RegisterStore("wallet", wallet, "alice");
            authority.RegisterStore("bob", target, "bob");
            var session = authority.OpenSession("alice");
            var command = Spend(session, 1, 0);
            command.Kind = ResourceCommandKind.Transfer;
            command.Destination = "bob";
            command.ExpectedDestinationRevision = 0;
            Assert.That(authority.Execute("alice", command).Failure, Is.EqualTo(ResourceFailureCode.Unauthorized));
            Assert.That(wallet.GetTotal(gold), Is.EqualTo(5));
        }

        [Test]
        public void ServerOwnedActionBuildsItsPriceAndCaptureInsideCallbackIsConsistent()
        {
            using var wallet = new ResourceStore(Cost(10));
            var authority = new ResourceAuthority(new[] { gold });
            authority.RegisterStore("wallet", wallet, "alice");
            var session = authority.OpenSession("alice");
            authority.RegisterAction("purchase", (context, plan) =>
            {
                plan.Spend(context.Source, gold, 4 * context.Amount);
                return default;
            });
            ResourceAuthorityState observed = null;
            wallet.Changed += () => observed = authority.CaptureState();
            var command = Spend(session, 1, 0);
            command.Kind = ResourceCommandKind.Action;
            command.ActionId = "purchase";
            Assert.That(authority.Execute("alice", command).Success, Is.True);
            Assert.That(wallet.GetTotal(gold), Is.EqualTo(6));
            Assert.That(observed.Sessions[0].LastSequence, Is.EqualTo(1));
            Assert.That(observed.Stores[0].State.Resources[0].Amount, Is.EqualTo(6));
        }

        [Test]
        public void PacketDeltasIncludeReservationsAndZeroBalancesAndRejectOutOfOrder()
        {
            using var store = new ResourceStore(Cost(10));
            var sender = new ResourceReplicator(store);
            using var replica = new ResourceReplica(new[] { gold });
            Assert.That(replica.TryApply(sender.Capture(), out _), Is.True);
            Assert.That(replica.Store.GetTotal(gold), Is.EqualTo(10));
            store.TryReserve(Cost(4), out var hold);
            var reserved = sender.Capture(replica.StreamId, replica.Store.Revision);
            Assert.That(reserved.IsFull, Is.False);
            Assert.That(replica.TryApply(reserved, out _), Is.True);
            Assert.That(replica.Store.GetReserved(gold), Is.EqualTo(4));
            hold.Commit();
            store.TrySpend(Cost(6));
            var spent = sender.Capture(replica.StreamId, replica.Store.Revision);
            Assert.That(replica.TryApply(spent, out _), Is.True);
            Assert.That(replica.Store.GetTotal(gold), Is.Zero);
            Assert.That(replica.TryApply(reserved, out var failure), Is.False);
            Assert.That(failure.Code, Is.EqualTo(ResourceFailureCode.StaleRevision));
            Assert.That(replica.Store.ActiveReservationCount, Is.Zero);
        }

        [Test]
        public void HistoryGapFallsBackToFullAndClientCannotMutateItsReplica()
        {
            using var store = new ResourceStore();
            var sender = new ResourceReplicator(store, 1);
            using var replica = new ResourceReplica(new[] { gold });
            replica.TryApply(sender.Capture(), out _);
            store.Add(gold, 1);
            store.Add(gold, 1);
            var packet = sender.Capture(replica.StreamId, replica.Store.Revision);
            Assert.That(packet.IsFull, Is.True);
            Assert.That(replica.TryApply(packet, out _), Is.True);
            Assert.That(replica.Store.TrySpend(Cost(1), out var failure), Is.False);
            Assert.That(failure.Code, Is.EqualTo(ResourceFailureCode.ReadOnly));
            Assert.That(replica.Store.TryReserve(Cost(1), out _), Is.False);
            Assert.Throws<InvalidOperationException>(() => replica.Store.CaptureState());
            Assert.That(replica.Store.GetTotal(gold), Is.EqualTo(2));
        }

        [Test]
        public void InvalidPacketDoesNotPartiallyApplyOrAdvanceRevision()
        {
            using var store = new ResourceStore(Cost(5));
            var sender = new ResourceReplicator(store);
            using var replica = new ResourceReplica(new[] { gold });
            replica.TryApply(sender.Capture(), out _);
            store.Add(gold, 1);
            var packet = sender.Capture(replica.StreamId, replica.Store.Revision);
            packet.Resources.Add(packet.Resources[0]);
            Assert.That(replica.TryApply(packet, out _), Is.False);
            Assert.That(replica.Store.Revision, Is.Zero);
            Assert.That(replica.Store.GetTotal(gold), Is.EqualTo(5));
            packet.Resources.RemoveAt(1);
            Assert.That(replica.TryApply(packet, out _), Is.True);
            Assert.That(replica.Store.GetTotal(gold), Is.EqualTo(6));
        }

        [Test]
        public void ReopeningAuthenticatedSessionInvalidatesOldCommands()
        {
            using var store = new ResourceStore(Cost(3));
            var authority = new ResourceAuthority(new[] { gold });
            authority.RegisterStore("wallet", store, "alice");
            var old = authority.OpenSession("alice");
            var current = authority.OpenSession("alice");
            Assert.That(authority.Execute("alice", Spend(old, 1, 0)).Failure, Is.EqualTo(ResourceFailureCode.Unauthorized));
            Assert.That(authority.Execute("alice", Spend(current, 1, 0)).Success, Is.True);
        }
    }
}
