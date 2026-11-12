using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using EasyResources.Networking;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace EasyResources.Tests
{
    public sealed class DeferredNotificationTests
    {
        private ResourceDefinition ammo, energy;
        private ResourceCost Cost(long ammunition, long power = 0) => new ResourceCost(
            new ResourceAmount(ammo, ammunition), new ResourceAmount(energy, power));
        private ResourceStore Wallet(int history = 0) => new ResourceStore(Cost(20, 30), historyCapacity: history);

        [SetUp]
        public void SetUp()
        {
            ammo = ResourceDefinition.Create("Ammo");
            energy = ResourceDefinition.Create("Energy", decimalPlaces: 2);
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(ammo);
            UnityEngine.Object.DestroyImmediate(energy);
        }

        [Test]
        public void MultiResourceHoldAndCommitAreSilentUntilOutcomeIsCoherent()
        {
            using var store = Wallet(8);
            var changed = 0;
            var outcomeFinished = false;
            var observedCoherent = false;
            var details = new List<ResourceChange>();
            store.Changed += () => { changed++; observedCoherent = outcomeFinished && store.GetTotal(ammo) == 16 && store.GetTotal(energy) == 23; };
            store.DetailedChanged += details.Add;
            var scope = store.DeferNotifications(new ResourceOperationContext("combat-cost", "shot-1"));
            Assert.That(store.TryReserve(Cost(4, 7), out var payment), Is.True);
            Assert.That(store.GetTotal(ammo), Is.EqualTo(20));
            Assert.That(store.GetAvailable(ammo), Is.EqualTo(16));
            Assert.That(changed, Is.Zero);
            Assert.That(store.Revision, Is.Zero);
            Assert.That(payment.TryCommit(out var failure), Is.True);
            Assert.That(failure.HasFailure, Is.False);
            Assert.That(store.GetTotal(ammo), Is.EqualTo(16));
            Assert.That(changed, Is.Zero);
            outcomeFinished = true;
            scope.Dispose();
            Assert.That(observedCoherent, Is.True);
            Assert.That(changed, Is.EqualTo(1));
            Assert.That(store.Revision, Is.EqualTo(1));
            Assert.That(details.Count, Is.EqualTo(2));
            Assert.That(details[0].Before.Reserved, Is.Zero);
            Assert.That(details[0].After.Reserved, Is.Zero);
            Assert.That(details[0].Kind, Is.EqualTo(ResourceChangeKind.Transaction));
            Assert.That(details[0].Context.Reason, Is.EqualTo("combat-cost"));
            Assert.That(details[0].Context.OwnerId, Is.EqualTo("shot-1"));
            Assert.That(payment.TryCommit(out failure), Is.False);
            Assert.That(failure.Code, Is.EqualTo(ResourceFailureCode.ReservationNotActive));
            Assert.That(payment.Cancel(), Is.False);
            var history = new List<ResourceChange>(); store.CopyHistoryTo(history);
            Assert.That(history.Count, Is.EqualTo(2));
        }

        [Test]
        public void CancellationAndFailedAdmissionLeaveNoEventRevisionOrHistory()
        {
            using var store = Wallet(8); var changed = 0; store.Changed += () => changed++;
            using (store.DeferNotifications())
            {
                Assert.That(store.TryReserve(Cost(3, 4), out var payment), Is.True);
                Assert.That(payment.Cancel(), Is.True);
                Assert.That(payment.Cancel(), Is.False);
                Assert.That(store.TryReserve(Cost(100, 1), out _, out var failure), Is.False);
                Assert.That(failure.Code, Is.EqualTo(ResourceFailureCode.InsufficientResource));
            }
            Assert.That(store.GetTotal(ammo), Is.EqualTo(20));
            Assert.That(store.GetAvailable(energy), Is.EqualTo(30));
            Assert.That(store.ActiveReservationCount, Is.Zero);
            Assert.That(changed, Is.Zero); Assert.That(store.Revision, Is.Zero);
            var history = new List<ResourceChange>(); store.CopyHistoryTo(history); Assert.That(history, Is.Empty);
        }

        [Test]
        public void NestedImmediatePulseSeesParentHoldAndPublishesOnlyAfterParent()
        {
            using var store = Wallet(); var changed = 0; store.Changed += () => changed++;
            var parent = store.DeferNotifications(new ResourceOperationContext("ability"));
            store.TryReserve(Cost(15), out var admission);
            var child = store.DeferNotifications(new ResourceOperationContext("beam-pulse"));
            Assert.That(store.TryReserve(Cost(6), out _), Is.False);
            Assert.That(store.TryReserve(Cost(4), out var pulse), Is.True);
            Assert.That(pulse.Commit(), Is.True); child.Dispose();
            Assert.That(store.GetAvailable(ammo), Is.EqualTo(1));
            Assert.That(changed, Is.Zero);
            Assert.That(admission.Cancel(), Is.True); parent.Dispose();
            Assert.That(changed, Is.EqualTo(1)); Assert.That(store.GetTotal(ammo), Is.EqualTo(16));
            Assert.That(store.LastContext.Reason, Is.EqualTo("ability"));
        }

        [Test]
        public void CopiesAndOutOfOrderScopeCompletionDoNotFlushOtherScopes()
        {
            using var store = Wallet(); var changed = 0; store.Changed += () => changed++;
            var parent = store.DeferNotifications(); var parentCopy = parent;
            store.TryReserve(Cost(3), out var first);
            var child = store.DeferNotifications(); var childCopy = child;
            store.TryReserve(Cost(4), out var second);
            first.Commit(); parent.Dispose(); parentCopy.Dispose();
            Assert.That(parent.IsActive, Is.False); Assert.That(child.IsActive, Is.True); Assert.That(changed, Is.Zero);
            second.Commit(); childCopy.Dispose(); child.Dispose();
            Assert.That(changed, Is.EqualTo(1)); Assert.That(store.GetTotal(ammo), Is.EqualTo(13));
            default(ResourceNotificationScope).Dispose();
        }

        [Test]
        public void ScopeDisposalDoesNotAutomaticallyCommitOrCancelAHold()
        {
            using var store = Wallet(); var scope = store.DeferNotifications();
            store.TryReserve(Cost(3), out var payment); scope.Dispose();
            Assert.That(payment.IsActive, Is.True); Assert.That(store.GetTotal(ammo), Is.EqualTo(20));
            Assert.That(store.GetReserved(ammo), Is.EqualTo(3)); Assert.That(store.Revision, Is.EqualTo(1));
            Assert.That(payment.Cancel(), Is.True);
        }

        [Test]
        public void SnapshotAndReplicationRequireACompletedBoundary()
        {
            using var store = Wallet(); var sender = new ResourceReplicator(store); var scope = store.DeferNotifications();
            store.TryReserve(Cost(3), out var payment, out _, new ResourceReservationOptions("shot"));
            Assert.Throws<InvalidOperationException>(() => store.CaptureState(true));
            Assert.Throws<InvalidOperationException>(() => sender.Capture());
            payment.Cancel(); scope.Dispose();
            Assert.That(store.CaptureState().Resources.Count, Is.EqualTo(2));
            Assert.That(sender.Capture().Resources.Count, Is.EqualTo(2));
            using var replica = new ResourceReplica(new[] { ammo, energy });
            Assert.Throws<InvalidOperationException>(() => replica.Store.DeferNotifications());
        }

        [Test]
        public void ExplicitRestoreInvalidatesOldScopesAndHoldsButNotNewOnes()
        {
            using var store = Wallet();
            store.TryReserve(Cost(3), out var oldPayment, out _, new ResourceReservationOptions("attack"));
            var saved = store.CaptureState(true);
            var oldScope = store.DeferNotifications(); oldPayment.Commit();
            Assert.Throws<InvalidOperationException>(() => store.RestoreState(saved, new[] { ammo, energy }, new ResourceRestoreOptions(resumeReservations: true)));
            var changes = 0; store.Changed += () => changes++;
            store.RestoreState(saved, new[] { ammo, energy }, new ResourceRestoreOptions(resumeReservations: true, replaceActiveReservations: true));
            Assert.That(changes, Is.EqualTo(1)); Assert.That(store.LastChangeKind, Is.EqualTo(ResourceChangeKind.Restore));
            Assert.That(oldScope.IsActive, Is.False); Assert.That(oldPayment.Commit(), Is.False);
            Assert.That(store.TryGetReservation(oldPayment.Id, "other-owner", out _), Is.False);
            Assert.That(store.TryGetReservation(oldPayment.Id, "attack", out var restoredPayment), Is.True);
            var newScope = store.DeferNotifications(); oldScope.Dispose();
            Assert.That(newScope.IsActive, Is.True); Assert.That(restoredPayment.TryCommit(out _), Is.True);
            Assert.That(changes, Is.EqualTo(1)); newScope.Dispose(); Assert.That(changes, Is.EqualTo(2));
            Assert.That(store.GetTotal(ammo), Is.EqualTo(17)); Assert.That(store.ActiveReservationCount, Is.Zero);
        }

        [Test]
        public void InvalidRestoreLeavesCurrentBoundaryAndPaymentIntact()
        {
            using var store = Wallet(); var invalid = store.CaptureState(); invalid.Resources[0] = new ResourceStateEntry(ammo.Id, -1);
            var scope = store.DeferNotifications(); store.TryReserve(Cost(3), out var payment);
            Assert.Throws<ArgumentException>(() => store.RestoreState(invalid, new[] { ammo, energy }, new ResourceRestoreOptions(replaceActiveReservations: true)));
            Assert.That(scope.IsActive, Is.True); Assert.That(payment.IsActive, Is.True);
            payment.Cancel(); scope.Dispose(); Assert.That(store.GetTotal(ammo), Is.EqualTo(20));
        }

        [Test]
        public void ScopeClosedInsideAnotherStoreCallbackFlushesAfterAllOriginalObservers()
        {
            using var a = Wallet(); using var b = Wallet(); var scope = a.DeferNotifications();
            var order = new List<string>();
            b.Changed += () => { order.Add("b-first"); scope.Dispose(); Assert.That(a.Revision, Is.Zero); };
            b.Changed += () => order.Add("b-last");
            a.Changed += () =>
            {
                order.Add("a-final"); Assert.That(b.GetTotal(ammo), Is.EqualTo(24));
                Assert.Throws<InvalidOperationException>(() => a.Add(ammo, 1));
                Assert.Throws<InvalidOperationException>(() => b.Add(ammo, 1));
            };
            Assert.That(new ResourceTransaction().Transfer(a, b, Cost(4)).TryExecute(), Is.True);
            Assert.That(order, Is.EqualTo(new[] { "b-first", "b-last", "a-final" }));
            Assert.That(a.Revision, Is.EqualTo(1)); Assert.That(b.Revision, Is.EqualTo(1));
        }

        [Test]
        public void SeveralDeferredStoresPrepareRevisionsBeforeAnyDeferredObserver()
        {
            using var a = Wallet(); using var b = Wallet(); using var c = Wallet();
            var sa = a.DeferNotifications(); var sb = b.DeferNotifications(); var seen = 0;
            c.Changed += () => { sa.Dispose(); sb.Dispose(); };
            a.Changed += () => { Assert.That(b.Revision, Is.EqualTo(1)); Assert.That(c.GetTotal(ammo), Is.EqualTo(21)); seen++; };
            b.Changed += () => { Assert.That(a.Revision, Is.EqualTo(1)); Assert.Throws<InvalidOperationException>(() => c.Add(ammo, 1)); seen++; };
            Assert.That(new ResourceTransaction().Spend(a, Cost(3)).Add(b, Cost(2)).Add(c, Cost(1)).TryExecute(), Is.True);
            Assert.That(seen, Is.EqualTo(2));
        }

        [Test]
        public void ScopeClosedByADeferredObserverWaitsUntilThatPublicationEnds()
        {
            using var a = Wallet(); using var b = Wallet(); using var c = Wallet();
            var sa = a.DeferNotifications(); var sb = b.DeferNotifications(); var order = new List<string>();
            c.Changed += () => sa.Dispose();
            a.Changed += () => { order.Add("a-first"); sb.Dispose(); };
            a.Changed += () => order.Add("a-last"); b.Changed += () => order.Add("b-final");
            new ResourceTransaction().Add(a, Cost(1)).Add(b, Cost(1)).Add(c, Cost(1)).TryExecute();
            Assert.That(order, Is.EqualTo(new[] { "a-first", "a-last", "b-final" }));
        }

        [Test]
        public void MultiStoreFailureDoesNotSpendPaymentOrLeakNotifications()
        {
            using var source = Wallet(); using var full = new ResourceStore(Cost(long.MaxValue));
            var scope = source.DeferNotifications(); source.TryReserve(Cost(4), out var payment); var changed = 0; source.Changed += () => changed++;
            Assert.That(new ResourceTransaction().Commit(payment).Add(full, ammo, 1).TryExecute(out var failure), Is.False);
            Assert.That(failure.Code, Is.EqualTo(ResourceFailureCode.AmountOverflow)); Assert.That(payment.IsActive, Is.True);
            Assert.That(source.GetTotal(ammo), Is.EqualTo(20)); payment.Cancel(); scope.Dispose(); Assert.That(changed, Is.Zero);
        }

        [Test]
        public void ObserverMutationReentryIsRejectedAndExceptionsDoNotUndoPayment()
        {
            using var store = Wallet(); var scope = store.DeferNotifications(); store.TryReserve(Cost(3), out var payment); var finalObserved = false;
            store.Changed += () =>
            {
                Assert.Throws<InvalidOperationException>(() => store.DeferNotifications());
                Assert.Throws<InvalidOperationException>(() => store.TryReserve(Cost(1), out _));
                throw new InvalidOperationException("deferred observer failure");
            };
            store.Changed += () => finalObserved = store.GetTotal(ammo) == 17;
            LogAssert.Expect(LogType.Exception, new Regex("deferred observer failure"));
            payment.Commit(); Assert.DoesNotThrow(() => scope.Dispose()); Assert.That(finalObserved, Is.True);
        }

        [Test]
        public void ScopeFromAnotherThreadDoesNotCloseTheActiveBoundary()
        {
            using var store = Wallet(); var scope = store.DeferNotifications(); var changed = 0; store.Changed += () => changed++; store.Add(ammo, 1);
            Assert.Throws<InvalidOperationException>(() => Task.Run(() => scope.Dispose()).GetAwaiter().GetResult());
            Assert.That(scope.IsActive, Is.True); Assert.That(changed, Is.Zero); scope.Dispose(); Assert.That(changed, Is.EqualTo(1));
        }

        [Test]
        public void EmptyHoldsCancelQuietlyButReplacingAnExistingIdentityNotifies()
        {
            using var store = Wallet(); var changed = 0; store.Changed += () => changed++;
            using (store.DeferNotifications()) { store.TryReserve(new ResourceCost(), out var transient); transient.Cancel(); }
            Assert.That(changed, Is.Zero);
            store.TryReserve(Cost(3), out var old); changed = 0;
            ResourceReservation replacement;
            using (store.DeferNotifications()) { old.Cancel(); store.TryReserve(Cost(3), out replacement); }
            Assert.That(changed, Is.EqualTo(1)); Assert.That(old.IsActive, Is.False); Assert.That(replacement.IsActive, Is.True);
            replacement.Cancel();
        }

        [Test]
        public void ExistingReservationMetadataAndCapacityChangesRemainObservable()
        {
            using var store = Wallet(); store.TryReserve(Cost(3), out var existing, out _, new ResourceReservationOptions("operation"));
            var changed = 0; store.Changed += () => changed++;
            using (store.DeferNotifications()) { existing.Renew(100); existing.TryResize(Cost(3), out _); }
            Assert.That(changed, Is.EqualTo(1));
            using (store.DeferNotifications()) store.TrySetLimits(new ResourceLimits(new[] { new ResourceLimit(ammo, 20) }), out _);
            Assert.That(changed, Is.EqualTo(2)); existing.Cancel();
        }

        [Test]
        public void DisposalInvalidatesAllCopiesWithoutFlushingAbandonedState()
        {
            var store = Wallet(); var scope = store.DeferNotifications(); var copy = scope; store.TryReserve(Cost(3), out var hold);
            var changed = 0; store.Changed += () => changed++; store.Dispose();
            Assert.That(scope.IsActive, Is.False); Assert.DoesNotThrow(() => scope.Dispose()); Assert.DoesNotThrow(() => copy.Dispose());
            Assert.That(hold.TryCommit(out var failure), Is.False); Assert.That(failure.Code, Is.EqualTo(ResourceFailureCode.ReservationNotActive)); Assert.That(changed, Is.Zero);
        }

        [Test]
        public void SingleRevisionBoundaryCanReachMaximumWithoutOverflowingInternally()
        {
            using var store = Wallet(); var state = store.CaptureState(); state.Revision = long.MaxValue - 2; store.RestoreState(state, new[] { ammo, energy });
            using (store.DeferNotifications()) { store.TryReserve(Cost(2), out var first); first.Commit(); store.TryReserve(Cost(3), out var second); second.Commit(); }
            Assert.That(store.Revision, Is.EqualTo(long.MaxValue)); Assert.That(store.GetTotal(ammo), Is.EqualTo(15));
            Assert.That(store.TryAdd(ammo, 1, out var failure), Is.False); Assert.That(failure.Code, Is.EqualTo(ResourceFailureCode.AmountOverflow));
        }

        [Test]
        public void CoordinatedRestorePublishesOnlyTheNewWorldAndInvalidatesAllOldScopes()
        {
            using var a = Wallet(); using var b = Wallet(); var savedA = a.CaptureState(); var savedB = b.CaptureState();
            var sa = a.DeferNotifications(); var sb = b.DeferNotifications(); a.TrySpend(Cost(3)); b.TrySpend(Cost(4)); var events = 0;
            a.Changed += () => { Assert.That(b.GetTotal(ammo), Is.EqualTo(20)); Assert.That(sa.IsActive || sb.IsActive, Is.False); events++; };
            b.Changed += () => { Assert.That(a.GetTotal(ammo), Is.EqualTo(20)); events++; };
            ResourceSnapshots.RestoreAll(new[] { a, b }, new[] { savedA, savedB }, new[] { ammo, energy }, new ResourceRestoreOptions(replaceActiveReservations: true));
            sa.Dispose(); sb.Dispose(); Assert.That(events, Is.EqualTo(2));
        }

        [Test]
        public void RestoreFromAnIndependentOperationInvalidatesOldScopeWithoutReplayingIt()
        {
            using var wallet = Wallet(); using var trigger = Wallet(); var saved = wallet.CaptureState();
            var scope = wallet.DeferNotifications(); wallet.TryReserve(Cost(3), out var payment); payment.Commit();
            var events = 0;
            trigger.Changed += () => wallet.RestoreState(saved, new[] { ammo, energy }, new ResourceRestoreOptions(replaceActiveReservations: true));
            wallet.Changed += () =>
            {
                Assert.That(scope.IsActive, Is.False); Assert.That(wallet.GetTotal(ammo), Is.EqualTo(20)); Assert.That(trigger.GetTotal(ammo), Is.EqualTo(21));
                Assert.Throws<InvalidOperationException>(() => wallet.Add(ammo, 1));
                Assert.Throws<InvalidOperationException>(() => trigger.Add(ammo, 1)); events++;
            };
            trigger.Add(ammo, 1); scope.Dispose(); Assert.That(events, Is.EqualTo(1)); Assert.That(payment.TryCommit(out _), Is.False);
        }
    }
}
