using System;
using System.Collections.Generic;
using EasyResources.Production;
using EasyResources.Trading;
using EasyResources.Inventory;
using NUnit.Framework;
using UnityEngine;

namespace EasyResources.Tests
{
    public sealed class ResourceModuleTests
    {
        private readonly List<UnityEngine.Object> assets = new List<UnityEngine.Object>();
        private ResourceDefinition wood, stone, gold;
        private T Keep<T>(T value)
            where T : UnityEngine.Object
        {
            assets.Add(value);
            return value;
        }

        private static ResourceCost Cost(ResourceDefinition resource, long amount) => new ResourceCost(new ResourceAmount(resource, amount));
        [SetUp]
        public void SetUp()
        {
            wood = Keep(ResourceDefinition.Create("Wood"));
            stone = Keep(ResourceDefinition.Create("Stone"));
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
        public void ProductionBlocksWithoutSpendingAndPublishesCompletedQueueWithBalances()
        {
            var recipe = Keep(ProductionRecipe.Create("Craft", Cost(wood, 2), Cost(stone, 1), 100));
            using var source = new ResourceStore(Cost(wood, 10));
            using var output = new ResourceStore(limits: new ResourceLimits(new[] { new ResourceLimit(stone, 0) }));
            using var queue = new ProductionQueue(source, output, ownerId: "factory");
            Assert.That(queue.TryEnqueue(recipe, 0, out var ticket), Is.True);
            Assert.That(queue.Tick(100), Is.Zero);
            Assert.That(source.GetTotal(wood), Is.EqualTo(10));
            Assert.That(source.GetReserved(wood), Is.EqualTo(2));
            Assert.That(ticket.IsActive, Is.True);
            output.TrySetLimits(ResourceLimits.Unlimited, out _);
            var coherent = false;
            source.Changed += () =>
            {
                coherent = queue.Count == 0 && output.GetTotal(stone) == 1 && source.GetTotal(wood) == 8;
            };
            Assert.That(queue.Tick(200), Is.EqualTo(1));
            Assert.That(coherent, Is.True);
            Assert.That(ticket.IsActive, Is.False);
        }

        [Test]
        public void ProductionCatchupIsBoundedAndPausePreservesRemainingTime()
        {
            var recipe = Keep(ProductionRecipe.Create("Craft", Cost(wood, 1), Cost(stone, 1), 100));
            using var store = new ResourceStore(Cost(wood, 10));
            using var queue = new ProductionQueue(store);
            for (var i = 0; i < 4; i++)
                Assert.That(queue.TryEnqueue(recipe, 0, out _), Is.True);
            queue.Pause(50);
            Assert.That(queue.Tick(1000), Is.Zero);
            queue.Resume(1000);
            Assert.That(queue.Tick(1049), Is.Zero);
            Assert.That(queue.Tick(2000, 2), Is.EqualTo(2));
            Assert.That(queue.Count, Is.EqualTo(2));
            Assert.That(queue.Tick(2000, 2), Is.EqualTo(2));
        }

        [Test]
        public void ProductionSnapshotRebindsHeldPaymentAndFrozenRecipe()
        {
            var recipe = Keep(ProductionRecipe.Create("Craft", Cost(wood, 2), Cost(stone, 3), 100));
            using var original = new ResourceStore(Cost(wood, 10));
            using var first = new ProductionQueue(original, ownerId: "line-1");
            first.TryEnqueue(recipe, 0, out var ticket);
            var operation = first.CaptureState(40);
            var state = original.CaptureState(true, 40);
            using var restored = new ResourceStore();
            restored.RestoreState(state, new[] { wood, stone }, new ResourceRestoreOptions(resumeReservations: true, nowMilliseconds: 1000));
            using var queue = ProductionQueue.FromState(restored, restored, operation, new[] { recipe }, new[] { wood, stone }, 1000);
            Assert.That(queue.TryGetTicket(ticket.Id, out var rebound), Is.True);
            Assert.That(queue.Tick(1059), Is.Zero);
            Assert.That(queue.Tick(1060), Is.EqualTo(1));
            Assert.That(restored.GetTotal(stone), Is.EqualTo(3));
            Assert.That(rebound.IsActive, Is.False);
        }

        [Test]
        public void ProductionRejectsExternallyResizedPayment()
        {
            var recipe = Keep(ProductionRecipe.Create("Craft", Cost(wood, 2), Cost(stone, 1), 0));
            using var store = new ResourceStore(Cost(wood, 10));
            using var queue = new ProductionQueue(store);
            queue.TryEnqueue(recipe, 0, out _);
            var holds = new List<ResourceReservationInfo>();
            store.CopyReservationsTo(holds);
            holds[0].Reservation.TryResize(Cost(wood, 1), out _);
            Assert.That(queue.Tick(0), Is.Zero);
            Assert.That(store.GetTotal(stone), Is.Zero);
            Assert.That(store.GetTotal(wood), Is.EqualTo(10));
        }

        [Test]
        public void FlowConsumesFuelAndProducesAtomicallyWithExactRemainders()
        {
            using var store = new ResourceStore();
            var flow = new ResourceFlow(store, new ResourceRate(wood, -1), new ResourceRate(stone, 3));
            Assert.That(flow.Tick(500), Is.True);
            Assert.That(store.GetTotal(stone), Is.EqualTo(1));
            Assert.That(flow.Tick(500), Is.False);
            Assert.That(store.GetTotal(stone), Is.EqualTo(1));
            store.Add(wood, 1);
            long observed = -1;
            store.Changed += () => observed = flow.CaptureState().Rates[1].Remainder;
            Assert.That(flow.Tick(500), Is.True);
            Assert.That(store.GetTotal(wood), Is.Zero);
            Assert.That(store.GetTotal(stone), Is.EqualTo(3));
            Assert.That(observed, Is.Zero);
        }

        [Test]
        public void FlowRestoreContinuesFractionWithoutDrift()
        {
            using var store = new ResourceStore();
            var flow = new ResourceFlow(store, new ResourceRate(wood, 1));
            for (var i = 0; i < 999; i++)
                Assert.That(flow.Tick(1), Is.True);
            var next = new ResourceFlow(store, new ResourceRate(wood, 1));
            next.RestoreState(flow.CaptureState());
            Assert.That(next.Tick(1), Is.True);
            Assert.That(store.GetTotal(wood), Is.EqualTo(1));
        }

        [Test]
        public void DeliveryCapacityFailureKeepsCargoAndArrivalIsIdempotent()
        {
            using var source = new ResourceStore(Cost(wood, 5));
            using var destination = new ResourceStore(limits: new ResourceLimits(new[] { new ResourceLimit(wood, 1) }));
            Assert.That(ResourceDelivery.TryStart(source, destination, Cost(wood, 3), "shipment", 0, 100, out var delivery, out _), Is.True);
            using (delivery)
            {
                Assert.That(delivery.TryArrive(99, out _), Is.False);
                Assert.That(delivery.TryArrive(100, out _), Is.False);
                Assert.That(source.GetTotal(wood), Is.EqualTo(5));
                destination.TrySetLimits(ResourceLimits.Unlimited, out _);
                var observed = false;
                source.Changed += () => observed = delivery.IsCompleted && destination.GetTotal(wood) == 3;
                Assert.That(delivery.TryArrive(101, out _), Is.True);
                Assert.That(observed, Is.True);
                Assert.That(delivery.TryArrive(102, out _), Is.False);
            }
        }

        [Test]
        public void DeliveryRestoresWithNamedReservation()
        {
            using var a = new ResourceStore(Cost(wood, 4));
            using var b = new ResourceStore();
            ResourceDelivery.TryStart(a, b, Cost(wood, 2), "delivery", 10, 100, out var original, out _);
            using (original)
            {
                var saved = original.CaptureState(50);
                using var restored = new ResourceStore();
                restored.RestoreState(a.CaptureState(true, 50), new[] { wood }, new ResourceRestoreOptions(resumeReservations: true, nowMilliseconds: 1000));
                using var delivery = ResourceDelivery.FromState(restored, b, saved, new[] { wood }, 1000);
                Assert.That(delivery.TryArrive(1060, out _), Is.True);
                Assert.That(restored.GetTotal(wood), Is.EqualTo(2));
            }
        }

        [Test]
        public void TradeChargesQuotedTaxesAndFeesAndConservesBalances()
        {
            var catalog = Keep(TradeCatalog.Create("Shop", new TradeOffer(wood, 1, Cost(gold, 3), Cost(gold, 2))));
            using var stock = new ResourceStore(Cost(wood, 10));
            using var till = new ResourceStore(Cost(gold, 200));
            using var buyer = new ResourceStore(Cost(gold, 100));
            using var tax = new ResourceStore();
            using var fee = new ResourceStore();
            var shop = new ResourceShop(catalog, till, stock, tax, fee);
            var terms = new TradeTerms(taxBasisPoints: 1000, feeBasisPoints: 1000);
            var quote = shop.QuoteBuy(wood, 2, terms);
            Assert.That(quote.TotalPrice.GetAmount(gold), Is.EqualTo(8));
            Assert.That(shop.TryExecute(buyer, quote, out _), Is.True);
            Assert.That(buyer.GetTotal(gold), Is.EqualTo(92));
            Assert.That(till.GetTotal(gold), Is.EqualTo(206));
            Assert.That(stock.GetTotal(wood), Is.EqualTo(8));
            Assert.That(tax.GetTotal(gold), Is.EqualTo(1));
            var sale = shop.QuoteSell(wood, 2, terms);
            Assert.That(sale.TotalPrice.GetAmount(gold), Is.EqualTo(2));
            Assert.That(shop.TryExecute(buyer, sale, out _), Is.True);
            Assert.That(buyer.GetTotal(gold), Is.EqualTo(94));
            Assert.That(till.GetTotal(gold) + buyer.GetTotal(gold) + tax.GetTotal(gold) + fee.GetTotal(gold), Is.EqualTo(300));
        }

        [Test]
        public void TradeCapacityAndStalePriceCannotChargeBuyer()
        {
            var catalog = Keep(TradeCatalog.Create("Shop", new TradeOffer(wood, 1, Cost(gold, 3), Cost(gold, 1))));
            using var till = new ResourceStore();
            using var buyer = new ResourceStore(Cost(gold, 10), limits: new ResourceLimits(new[] { new ResourceLimit(wood, 0) }));
            var shop = new ResourceShop(catalog, till);
            var quote = shop.QuoteBuy(wood);
            Assert.That(shop.TryExecute(buyer, quote, out _), Is.False);
            Assert.That(buyer.GetTotal(gold), Is.EqualTo(10));
            shop.SetPrice(wood, Cost(gold, 4), Cost(gold, 2));
            Assert.That(shop.TryExecute(buyer, quote, out var failure), Is.False);
            Assert.That(failure.Code, Is.EqualTo(ResourceFailureCode.StaleRevision));
            var state = shop.CaptureState();
            shop.SetPrice(wood, Cost(gold, 9), Cost(gold, 2));
            shop.RestoreState(state, new[] { wood, gold });
            Assert.That(shop.QuoteBuy(wood).TotalPrice.GetAmount(gold), Is.EqualTo(4));
        }

        [Test]
        public void CreditDebtAndMoneyPublishTogetherAndInterestIsExplicit()
        {
            using var lender = new ResourceStore(Cost(gold, 100));
            using var borrower = new ResourceStore();
            var credit = new ResourceCreditAccount(lender, borrower, gold, 50);
            long observed = 0;
            borrower.Changed += () => observed = credit.Debt;
            Assert.That(credit.TryBorrow(40, out _), Is.True);
            Assert.That(observed, Is.EqualTo(40));
            Assert.That(credit.TryBorrow(11, out _), Is.False);
            credit.AccrueInterest(1000);
            Assert.That(credit.Debt, Is.EqualTo(44));
            Assert.That(credit.TryRepay(20, out _), Is.True);
            Assert.That(credit.Debt, Is.EqualTo(24));
            Assert.That(lender.GetTotal(gold) + borrower.GetTotal(gold), Is.EqualTo(100));
        }

        [Test]
        public void InventoryStacksCompatibleItemsAndPartialAcceptanceReportsExactCount()
        {
            var item = Keep(ItemDefinition.Create("Ore", maximumStack: 5));
            using var inventory = new InventoryStore(2);
            Assert.That(inventory.TryAdd(item, 6, out _), Is.True);
            Assert.That(inventory.GetSlot(0).Count, Is.EqualTo(5));
            Assert.That(inventory.GetSlot(1).Count, Is.EqualTo(1));
            Assert.That(inventory.AddUpTo(item, 10), Is.EqualTo(4));
            Assert.That(inventory.TryAdd(item, 1, out _), Is.False);
            Assert.That(inventory.GetCount(item), Is.EqualTo(10));
            Assert.That(inventory.TryResize(1), Is.False);
        }

        [Test]
        public void InventoryCraftCapacityFailureDoesNotSpendAndSuccessPublishesBoth()
        {
            var item = Keep(ItemDefinition.Create("Sword", unique: true));
            using var inventory = new InventoryStore(1);
            using var wallet = new ResourceStore(Cost(gold, 10));
            Assert.That(inventory.TryCraft(wallet, Cost(gold, 2), item, 2, out _), Is.False);
            Assert.That(wallet.GetTotal(gold), Is.EqualTo(10));
            var coherent = false;
            wallet.Changed += () => coherent = inventory.GetCount(item) == 1;
            Assert.That(inventory.TryCraft(wallet, Cost(gold, 2), item, 1, out _), Is.True);
            Assert.That(coherent, Is.True);
        }

        [Test]
        public void InventoryUniqueTransferPreservesIdentityConditionAndAtomicFailure()
        {
            var item = Keep(ItemDefinition.Create("Sword", maximumDurability: 100));
            using var source = new InventoryStore(1);
            using var target = new InventoryStore(1);
            source.TryAdd(item, 1, out _, new ItemProperties(quality: 3, durability: 70));
            target.TryAdd(item, 1, out _);
            var id = source.GetSlot(0).InstanceId;
            Assert.That(source.TryTransferTo(target, 0, 1, out _), Is.False);
            Assert.That(source.GetSlot(0).InstanceId, Is.EqualTo(id));
            target.TryRemove(item, 1, out _);
            var coherent = false;
            source.Changed += () => coherent = target.GetSlot(0).InstanceId == id;
            Assert.That(source.TryTransferTo(target, 0, 1, out _), Is.True);
            Assert.That(coherent, Is.True);
            Assert.That(target.GetSlot(0).Durability, Is.EqualTo(70));
            Assert.That(target.GetSlot(0).Quality, Is.EqualTo(3));
            InventoryStore.ValidateUniqueItems(source, target);
        }

        [Test]
        public void InventoryQualityExpiryAndSavedOfflineTimeRemainExplicit()
        {
            var item = Keep(ItemDefinition.Create("Food", maximumStack: 10));
            using var inventory = new InventoryStore(3);
            inventory.TryAdd(item, 3, out _, new ItemProperties(quality: 1, expiresAtMilliseconds: 1000));
            inventory.TryAdd(item, 2, out _, new ItemProperties(quality: 2, expiresAtMilliseconds: 1000));
            Assert.That(inventory.GetSlot(0).Count, Is.EqualTo(3));
            Assert.That(inventory.GetSlot(1).Count, Is.EqualTo(2));
            var state = inventory.CaptureState(400);
            using var restored = new InventoryStore(3);
            restored.RestoreState(state, new[] { item }, 10000, 200);
            Assert.That(restored.RemoveExpired(10399), Is.Zero);
            Assert.That(restored.RemoveExpired(10400), Is.EqualTo(5));
            state.Slots[0].Count = 11;
            Assert.Throws<ArgumentException>(() => inventory.RestoreState(state, new[] { item }));
            Assert.That(inventory.GetCount(item), Is.EqualTo(5));
        }

        [Test]
        public void InventoryWeightAndAllowedDefinitionsConstrainAcceptance()
        {
            var ore = Keep(ItemDefinition.Create("Ore", weightPerItem: 3));
            var sword = Keep(ItemDefinition.Create("Sword"));
            using var inventory = new InventoryStore(10, maximumWeight: 10, acceptedItems: new[] { ore });
            Assert.That(inventory.AddUpTo(ore, 100), Is.EqualTo(3));
            Assert.That(inventory.UsedWeight, Is.EqualTo(9));
            Assert.That(inventory.TryAdd(sword, 1, out _), Is.False);
        }
    }
}
