using System;
using System.Collections;
using System.Reflection;
using EasyResources.Inventory;
using EasyResources.Production;
using EasyResources.Progression;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace EasyResources.Tests
{
    public sealed class ResourceModuleLifecycleTests
    {
        [UnityTest]
        public IEnumerator ComponentsRestoreAfterWalletWithoutCancellingNewReservations()
        {
            var gold = ResourceDefinition.Create("Gold");
            var wood = ResourceDefinition.Create("Wood");
            var recipe = ProductionRecipe.Create("Recipe", new ResourceCost(new ResourceAmount(gold, 3)), new ResourceCost(new ResourceAmount(wood, 1)), 100000);
            var node = ProgressionNode.Create("Research", new[] { new ProgressionRank(new ResourceCost(new ResourceAmount(gold, 4))) }, researchMilliseconds: 100000);
            var definition = ProgressionDefinition.Create("Tree", Array.Empty<long>(), new[] { node });
            var go = new GameObject("Restored components");
            go.SetActive(false);
            var wallet = go.AddComponent<ResourceContainer>();
            var station = go.AddComponent<ProductionStation>();
            var progression = go.AddComponent<ProgressionComponent>();
            typeof(ProgressionComponent).GetField("definition", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(progression, definition);
            wallet.Store.Add(gold, 10);
            go.SetActive(true);
            var now = (long)(Time.unscaledTimeAsDouble * 1000);
            Assert.That(station.TryEnqueue(recipe), Is.True);
            Assert.That(progression.Profile.TryStartResearch(node, now, "research", out _), Is.True);
            var funds = wallet.CaptureState(true, now);
            var production = station.CaptureState();
            var research = progression.CaptureState();
            Assert.Throws<InvalidOperationException>(() => station.RestoreState(production, new[] { recipe }, new[] { gold, wood }));
            Assert.Throws<InvalidOperationException>(() => progression.RestoreState(research, new[] { gold, wood }));
            wallet.RestoreState(funds, new[] { gold, wood }, new ResourceRestoreOptions(resumeReservations: true, replaceActiveReservations: true, nowMilliseconds: now));
            station.RestoreState(production, new[] { recipe }, new[] { gold, wood });
            progression.RestoreState(research, new[] { gold, wood });
            Assert.That(wallet.GetReserved(gold), Is.EqualTo(7));
            Assert.That(station.Queue.Count, Is.EqualTo(1));
            Assert.That(progression.Profile.Researching, Is.EqualTo(node));
            UnityEngine.Object.Destroy(go);
            yield return null;
            UnityEngine.Object.Destroy(gold);
            UnityEngine.Object.Destroy(wood);
            UnityEngine.Object.Destroy(recipe);
            UnityEngine.Object.Destroy(node);
            UnityEngine.Object.Destroy(definition);
            yield return null;
            LogAssert.NoUnexpectedReceived();
        }

        [UnityTest]
        public IEnumerator DestroyingInventoryComponentDuringPaymentPublishesThenDisposes()
        {
            var gold = ResourceDefinition.Create("Gold");
            var item = ItemDefinition.Create("Item");
            var go = new GameObject("Inventory lifecycle");
            var component = go.AddComponent<InventoryContainer>();
            var inventory = component.Store;
            using (var wallet = new ResourceStore(new ResourceCost(new ResourceAmount(gold, 10))))
            {
                wallet.Changed += () => UnityEngine.Object.DestroyImmediate(component);
                Assert.That(inventory.TryCraft(wallet, new ResourceCost(new ResourceAmount(gold, 2)), item, 1, out _), Is.True);
                Assert.That(wallet.GetTotal(gold), Is.EqualTo(8));
                Assert.Throws<ObjectDisposedException>(() => inventory.GetCount(item));
            }

            UnityEngine.Object.Destroy(go);
            UnityEngine.Object.Destroy(gold);
            UnityEngine.Object.Destroy(item);
            yield return null;
            LogAssert.NoUnexpectedReceived();
        }

        [UnityTest]
        public IEnumerator DestroyingProgressionComponentDuringPaymentDoesNotInterruptCommit()
        {
            var gold = ResourceDefinition.Create("Gold");
            var node = ProgressionNode.Create("Node", new[] { new ProgressionRank(new ResourceCost(new ResourceAmount(gold, 2))) });
            var definition = ProgressionDefinition.Create("Tree", Array.Empty<long>(), new[] { node });
            var go = new GameObject("Progression lifecycle");
            go.SetActive(false);
            var wallet = go.AddComponent<ResourceContainer>();
            var component = go.AddComponent<ProgressionComponent>();
            typeof(ProgressionComponent).GetField("definition", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(component, definition);
            wallet.Store.Add(gold, 10);
            go.SetActive(true);
            var profile = component.Profile;
            wallet.Changed += () => UnityEngine.Object.DestroyImmediate(component);
            Assert.That(profile.TryUnlock(node, out _), Is.True);
            Assert.That(wallet.GetTotal(gold), Is.EqualTo(8));
            Assert.Throws<ObjectDisposedException>(() => profile.GetRank(node));
            UnityEngine.Object.Destroy(go);
            UnityEngine.Object.Destroy(gold);
            UnityEngine.Object.Destroy(node);
            UnityEngine.Object.Destroy(definition);
            yield return null;
            LogAssert.NoUnexpectedReceived();
        }
    }
}
