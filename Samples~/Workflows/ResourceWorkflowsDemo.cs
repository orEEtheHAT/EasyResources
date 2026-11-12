using System;
using System.Collections.Generic;
using System.Numerics;
using EasyResources.Inventory;
using EasyResources.LargeNumbers;
using EasyResources.Networking;
using EasyResources.Production;
using EasyResources.Progression;
using EasyResources.Trading;
using EasyResources.UI;
using UnityEngine;

namespace EasyResources.Samples
{
    /// <summary>Executable examples: each section uses the same public API available to game code.</summary>
    public sealed class ResourceWorkflowsDemo : MonoBehaviour
    {
        private readonly List<UnityEngine.Object> definitions = new List<UnityEngine.Object>();
        private readonly List<IDisposable> owned = new List<IDisposable>();
        private readonly List<string> results = new List<string>();
        private GUIStyle heading, line;
        public int CheckCount { get; private set; }
        public int FailureCount { get; private set; }
        public bool Completed { get; private set; }

        private T Asset<T>(T asset)
            where T : UnityEngine.Object
        {
            definitions.Add(asset);
            return asset;
        }

        private T Own<T>(T value)
            where T : IDisposable
        {
            owned.Add(value);
            return value;
        }

        private static ResourceCost Cost(ResourceDefinition resource, long amount) => new ResourceCost(new ResourceAmount(resource, amount));
        private void Check(bool success, string label)
        {
            CheckCount++;
            if (!success)
                FailureCount++;
            results.Add((success ? "PASS  " : "FAIL  ") + label);
        }

        private void Start()
        {
            try
            {
                Run();
            }
            catch (Exception exception)
            {
                FailureCount++;
                results.Add("FAIL  " + exception.Message);
                Debug.LogException(exception, this);
            }

            Completed = true;
            Debug.Log($"EasyResources Workflows: {CheckCount - FailureCount}/{CheckCount} checks passed.", this);
        }

        private void Run()
        {
            var wood = Asset(ResourceDefinition.Create("Wood"));
            var gold = Asset(ResourceDefinition.Create("Gold", decimalPlaces: 2));
            var fuel = Asset(ResourceDefinition.Create("Fuel", decimalPlaces: 3, unit: "L"));
            var plank = Asset(ResourceDefinition.Create("Planks"));
            var wallet = Own(new ResourceStore(new ResourceCost(new ResourceAmount(gold, gold.ToUnits(100)), new ResourceAmount(wood, 20)), historyCapacity: 64));
            var warehouse = Own(new ResourceStore(limits: new ResourceLimits(new[] { new ResourceLimit(wood, 30) })));
            Check(wallet.TryTransferTo(warehouse, Cost(wood, 5)) && warehouse.GetTotal(wood) == 5, "Transfer between independent stores");
            Check(warehouse.AddUpTo(wood, 100) == 25, "Partial acceptance with an exact remainder");
            Check(!wallet.TryTransferTo(warehouse, Cost(wood, 1)) && wallet.GetTotal(wood) == 15, "Full warehouse does not charge the sender");
            wallet.TryReserve(Cost(gold, 500), out var payment, out _, new ResourceReservationOptions("building-1", expiresAtMilliseconds: 1000));
            Check(payment.TryCommit(Cost(gold, 200), out _) && wallet.GetReserved(gold) == 300, "Partial reservation commit");
            payment.Cancel();
            wallet.Add(fuel, fuel.ToUnits(1.25m));
            Check(wallet.GetTotal(fuel) == 1250, "Exact fixed-point fuel: 1.25 L = 1250 units");
            var flow = new ResourceFlow(wallet, new ResourceRate(fuel, -100));
            flow.Tick(2500);
            Check(wallet.GetTotal(fuel) == 1000, "Fuel consumption at an exact rate");
            var recipe = Asset(ProductionRecipe.Create("Cut planks", Cost(wood, 2), Cost(plank, 3), 500));
            var queue = Own(new ProductionQueue(wallet, ownerId: "sawmill"));
            queue.TryEnqueue(recipe, 0, out _);
            Check(queue.Tick(500) == 1 && wallet.GetTotal(plank) == 3, "Queued production consumes and grants atomically");
            ResourceDelivery.TryStart(wallet, warehouse, Cost(plank, 2), "shipment-1", 0, 100, out var delivery, out _);
            Own(delivery);
            Check(!delivery.TryArrive(99, out _) && delivery.TryArrive(100, out _), "Timed delivery keeps cargo reserved until arrival");
            var catalog = Asset(TradeCatalog.Create("Market", new TradeOffer(wood, 1, Cost(gold, 300), Cost(gold, 150))));
            var till = Own(new ResourceStore());
            var taxes = Own(new ResourceStore());
            var shop = new ResourceShop(catalog, till, taxRecipient: taxes);
            var quote = shop.QuoteBuy(wood, 2, new TradeTerms(taxBasisPoints: 1000));
            Check(shop.TryExecute(wallet, quote, out _) && taxes.GetTotal(gold) == 60, "Quoted trade with a separate tax recipient");
            shop.SetPrice(wood, Cost(gold, 350), Cost(gold, 175));
            Check(!shop.TryExecute(wallet, quote, out _), "Stale price quote is rejected");
            var item = Asset(ItemDefinition.Create("Iron tool", maximumDurability: 100));
            var inventory = Own(new InventoryStore(2));
            Check(inventory.TryCraft(wallet, Cost(gold, 500), item, 1, out _, new ItemProperties(quality: 2)) && !string.IsNullOrEmpty(inventory.GetSlot(0).InstanceId), "Crafting a unique item with quality and durability");
            var node = Asset(ProgressionNode.Create("Efficient cutting", new[] { new ProgressionRank(Cost(gold, 100), new ProgressionModifier("yield", 1)) }, minimumLevel: 2, researchMilliseconds: 100));
            var tree = Asset(ProgressionDefinition.Create("Workshop skills", new long[] { 10 }, new[] { node }));
            var profile = Own(new ProgressionProfile(tree, wallet));
            profile.AddExperience(10, out _);
            profile.TryStartResearch(node, 0, "research-1", out _);
            Check(profile.TryCompleteResearch(100, out _) && profile.GetStat("yield", 1) == 2, "XP, level requirement, paid research and stat modifier");
            Check(profile.TryPrestige(new PrestigeRule(2, Cost(gold, 1000)), out _) && profile.PrestigeCount == 1, "Prestige resets progression and grants its reward");
            var authority = new ResourceAuthority(new[] { wood, gold, fuel, plank });
            authority.RegisterStore("wallet", wallet, "player");
            var session = authority.OpenSession("player");
            var command = new ResourceCommand
            {
                SessionId = session,
                Sequence = 1,
                Source = "wallet",
                ResourceId = gold.Id,
                Amount = 1,
                ExpectedSourceRevision = wallet.Revision
            };
            var before = wallet.GetTotal(gold);
            authority.Execute("player", command);
            authority.Execute("player", command);
            Check(wallet.GetTotal(gold) == before - 1, "Authority applies a repeated command only once");
            var sender = new ResourceReplicator(wallet);
            var replica = Own(new ResourceReplica(new[] { wood, gold, fuel, plank }));
            replica.TryApply(sender.Capture(), out _);
            Check(replica.Store.GetTotal(gold) == wallet.GetTotal(gold) && !replica.Store.TrySpend(Cost(gold, 1)), "Read-only client replica displays authoritative balances");
            var huge = BigInteger.Pow(10, 50);
            var large = Own(new BigResourceStore(new BigResourceCost(new BigResourceAmount(gold, huge))));
            Check(large.TrySpend(new BigResourceCost(new BigResourceAmount(gold, 1)), out _) && large.GetTotal(gold) == huge - 1, "Exact arbitrary-size economy without floating-point loss");
            var saved = wallet.CaptureState();
            wallet.Add(gold, 1);
            wallet.RestoreState(saved, new[] { wood, gold, fuel, plank });
            Check(wallet.GetTotal(gold) == before - 1, "Versioned snapshot restores the wallet");
            var panel = gameObject.AddComponent<ResourcePanel>();
            panel.Bind(wallet);
            panel.PreviewCost(Cost(gold, 350));
            Check(panel.enabled, "Ready resource UI with cost and held-balance display");
        }

        private void OnGUI()
        {
            if (heading == null)
            {
                heading = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 22
                };
                line = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 15
                };
            }

            var x = Math.Max(420, Screen.width * .36f);
            GUI.Label(new Rect(x, 20, 800, 40), "EasyResources — complete workflows", heading);
            for (var i = 0; i < results.Count; i++)
            {
                GUI.color = results[i].StartsWith("PASS") ? new Color(.55f, .95f, .75f) : new Color(1, .5f, .5f);
                GUI.Label(new Rect(x, 65 + i * 27, 900, 28), results[i], line);
            }

            GUI.color = Color.white;
        }

        private void OnDestroy()
        {
            for (var i = owned.Count - 1; i >= 0; i--)
                owned[i]?.Dispose();
            foreach (var asset in definitions)
                if (asset != null)
                    Destroy(asset);
        }
    }
}
