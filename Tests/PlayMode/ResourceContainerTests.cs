using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace EasyResources.Tests
{
    public sealed class ResourceContainerTests
    {
        [UnityTest]
        public IEnumerator DestroyingOwnerDuringNotificationDefersCleanupUntilObserversFinish()
        {
            var resource = ResourceDefinition.Create("Wood");
            var go = new GameObject("Destroy in observer");
            var container = go.AddComponent<ResourceContainer>();
            var store = container.Store;
            store.Add(resource, 10);
            store.TryReserve(new ResourceCost(new ResourceAmount(resource, 2)), out var held);
            store.Changed += () =>
            {
                Object.DestroyImmediate(go);
                Assert.That(store.GetTotal(resource), Is.EqualTo(11));
            };
            store.Add(resource, 1);
            Assert.That(held.IsActive, Is.False);
            Assert.Throws<System.ObjectDisposedException>(() => store.GetTotal(resource));
            Object.Destroy(resource);
            yield return null;
        }

        [UnityTest]
        public IEnumerator ComponentsKeepSeparateBalancesAndDestroyReleasesReservations()
        {
            var resource = ResourceDefinition.Create("Wood");
            var firstObject = new GameObject("First container");
            var secondObject = new GameObject("Second container");
            try
            {
                var first = firstObject.AddComponent<ResourceContainer>();
                var second = secondObject.AddComponent<ResourceContainer>();
                first.Add(resource, 10);
                second.Add(resource, 3);
                var events = 0;
                first.OnChanged.AddListener(() => events++);
                Assert.That(first.TryReserve(new ResourceCost(new ResourceAmount(resource, 4)), out var reservation), Is.True);
                Assert.That(first.GetAvailable(resource), Is.EqualTo(6));
                Assert.That(second.GetTotal(resource), Is.EqualTo(3));
                Assert.That(events, Is.EqualTo(1));
                first.enabled = false;
                yield return null;
                Assert.That(reservation.IsActive, Is.True, "Disabling the view must not silently cancel a payment.");
                Object.Destroy(firstObject);
                yield return null;
                Assert.That(reservation.IsActive, Is.False);
                Assert.That(reservation.Commit(), Is.False);
                Assert.That(reservation.Cancel(), Is.False);
                Assert.That(second.GetTotal(resource), Is.EqualTo(3));
            }
            finally
            {
                if (firstObject != null)
                    Object.Destroy(firstObject);
                if (secondObject != null)
                    Object.Destroy(secondObject);
                Object.Destroy(resource);
            }
        }

        [UnityTest]
        public IEnumerator UnityEventSeesCompleteCompoundPaymentAndReentryIsRejected()
        {
            var wood = ResourceDefinition.Create("Wood");
            var stone = ResourceDefinition.Create("Stone");
            var go = new GameObject("Container");
            try
            {
                var container = go.AddComponent<ResourceContainer>();
                container.Add(wood, 10);
                container.Add(stone, 5);
                var observed = false;
                container.OnChanged.AddListener(() =>
                {
                    Assert.That(container.GetTotal(wood), Is.EqualTo(6));
                    Assert.That(container.GetTotal(stone), Is.EqualTo(3));
                    Assert.Throws<System.InvalidOperationException>(() => container.Add(wood, 1));
                    observed = true;
                });
                Assert.That(container.TrySpend(new ResourceCost(new ResourceAmount(wood, 4), new ResourceAmount(stone, 2))), Is.True);
                Assert.That(observed, Is.True);
                yield return null;
                Assert.That(container.GetTotal(wood), Is.EqualTo(6));
            }
            finally
            {
                Object.Destroy(go);
                Object.Destroy(wood);
                Object.Destroy(stone);
            }
        }
    }
}
