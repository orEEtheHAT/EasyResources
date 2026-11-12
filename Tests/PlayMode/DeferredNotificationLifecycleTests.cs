using System;
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace EasyResources.Tests
{
    public sealed class DeferredNotificationLifecycleTests
    {
        [UnityTest]
        public IEnumerator DestroyedConsumerBeforeExecutionCancelsWithoutCallbacksOrCharge()
        {
            var resource = ResourceDefinition.Create("Ammo"); var go = new GameObject("Payment owner"); var owner = go.AddComponent<DeferredPaymentOwnerProbe>();
            using var store = new ResourceStore(new ResourceCost(new ResourceAmount(resource, 10))); var changed = 0; store.Changed += () => changed++;
            owner.Notifications = store.DeferNotifications(); store.TryReserve(new ResourceCost(new ResourceAmount(resource, 3)), out owner.Payment); var stale = owner.Payment;
            UnityEngine.Object.DestroyImmediate(go);
            Assert.That(changed, Is.Zero); Assert.That(store.GetTotal(resource), Is.EqualTo(10)); Assert.That(store.GetReserved(resource), Is.Zero); Assert.That(stale.Commit(), Is.False);
            UnityEngine.Object.Destroy(resource); yield return null; LogAssert.NoUnexpectedReceived();
        }

        [UnityTest]
        public IEnumerator ConsumerDestroyedByFinalPaymentEventCannotRefundOrPayAgain()
        {
            var resource = ResourceDefinition.Create("Ammo"); var go = new GameObject("Payment owner"); var owner = go.AddComponent<DeferredPaymentOwnerProbe>();
            using var store = new ResourceStore(new ResourceCost(new ResourceAmount(resource, 10)));
            owner.Notifications = store.DeferNotifications(); store.TryReserve(new ResourceCost(new ResourceAmount(resource, 3)), out owner.Payment);
            var payment = owner.Payment; var scope = owner.Notifications; var observed = false;
            store.Changed += () => UnityEngine.Object.DestroyImmediate(go);
            store.Changed += () => observed = store.GetTotal(resource) == 7;
            Assert.That(payment.TryCommit(out _), Is.True); scope.Dispose();
            Assert.That(observed, Is.True); Assert.That(payment.Commit(), Is.False); Assert.That(payment.Cancel(), Is.False); Assert.That(scope.IsActive, Is.False);
            UnityEngine.Object.Destroy(resource); yield return null; LogAssert.NoUnexpectedReceived();
        }

        [UnityTest]
        public IEnumerator WalletDestroyedDuringDeferredPublicationLivesUntilObserversFinish()
        {
            var resource = ResourceDefinition.Create("Ammo"); var go = new GameObject("Wallet"); var container = go.AddComponent<ResourceContainer>(); var store = container.Store; store.Add(resource, 10);
            var scope = store.DeferNotifications(); store.TryReserve(new ResourceCost(new ResourceAmount(resource, 3)), out var payment); var observed = false;
            store.Changed += () => UnityEngine.Object.DestroyImmediate(go); store.Changed += () => observed = store.GetTotal(resource) == 7;
            payment.Commit(); scope.Dispose(); Assert.That(observed, Is.True); Assert.That(scope.IsActive, Is.False);
            Assert.That(payment.Cancel(), Is.False); Assert.Throws<ObjectDisposedException>(() => store.GetTotal(resource));
            UnityEngine.Object.Destroy(resource); yield return null; LogAssert.NoUnexpectedReceived();
        }
    }
}
