using System;
using UnityEngine;

namespace EasyResources.Inventory
{
    [CreateAssetMenu(menuName = "EasyResources/Inventory/Item", fileName = "New Item")]
    public sealed class ItemDefinition : DefinitionAsset
    {
        [SerializeField, Min(1)]
        private int maximumStack = 99;
        [SerializeField]
        private bool unique;
        [SerializeField, Min(0)]
        private int maximumDurability;
        [SerializeField, Min(0)]
        private int weightPerItem = 1;
        [SerializeField, Min(0)]
        private int volumePerItem = 1;
        public bool IsUnique => unique || maximumDurability > 0;
        public int MaximumStack => IsUnique ? 1 : maximumStack;
        public int MaximumDurability => maximumDurability;
        public int WeightPerItem => weightPerItem;
        public int VolumePerItem => volumePerItem;

        public override void ValidateDefinition()
        {
            base.ValidateDefinition();
            if (maximumStack < 1 || maximumDurability < 0 || weightPerItem < 0 || volumePerItem < 0)
                throw new ArgumentException("Stack size must be positive; durability, weight and volume must be nonnegative.");
        }

        public static ItemDefinition Create(string name, int maximumStack = 99, bool unique = false, int maximumDurability = 0, int weightPerItem = 1, int volumePerItem = 1, string stableId = null)
        {
            if (maximumStack < 1 || maximumDurability < 0 || weightPerItem < 0 || volumePerItem < 0)
                throw new ArgumentException("Invalid item configuration.");
            var result = New<ItemDefinition>(name, stableId);
            result.maximumStack = maximumStack;
            result.unique = unique;
            result.maximumDurability = maximumDurability;
            result.weightPerItem = weightPerItem;
            result.volumePerItem = volumePerItem;
            return result;
        }
    }

    public readonly struct ItemProperties
    {
        public int Quality { get; }
        public int Durability { get; }
        public bool OverrideDurability { get; }
        public bool HasExpiry { get; }
        public long ExpiresAtMilliseconds { get; }

        public ItemProperties(int quality = 0, int durability = -1, long expiresAtMilliseconds = -1)
        {
            if (quality < 0 || durability < -1 || expiresAtMilliseconds < -1)
                throw new ArgumentOutOfRangeException("Invalid item properties.");
            Quality = quality;
            Durability = durability;
            OverrideDurability = durability >= 0;
            HasExpiry = expiresAtMilliseconds >= 0;
            ExpiresAtMilliseconds = expiresAtMilliseconds;
        }
    }

    public readonly struct InventorySlot
    {
        public ItemDefinition Item { get; }
        public int Count { get; }
        public string InstanceId { get; }
        public int Quality { get; }
        public int Durability { get; }
        public bool HasExpiry { get; }
        public long ExpiresAtMilliseconds { get; }
        public bool IsEmpty => Item == null || Count == 0;

        internal InventorySlot(ItemDefinition item, int count, string instanceId, int quality, int durability, bool hasExpiry, long expires)
        {
            Item = item;
            Count = count;
            InstanceId = instanceId;
            Quality = quality;
            Durability = durability;
            HasExpiry = hasExpiry;
            ExpiresAtMilliseconds = expires;
        }

        internal InventorySlot WithCount(int count) => count == 0 ? default : new InventorySlot(Item, count, InstanceId, Quality, Durability, HasExpiry, ExpiresAtMilliseconds);
        internal ItemProperties Properties => new ItemProperties(Quality, Durability, HasExpiry ? ExpiresAtMilliseconds : -1);
    }

    [Serializable]
    public struct ItemAmount
    {
        [SerializeField]
        private ItemDefinition item;
        [SerializeField, Min(0)]
        private int count;
        public ItemDefinition Item => item;
        public int Count => count;

        public ItemAmount(ItemDefinition item, int count)
        {
            this.item = item;
            this.count = count;
        }
    }

    public enum InventoryChangeKind
    {
        Add,
        Remove,
        Transfer,
        Condition,
        Expire,
        Restore,
        Resize,
        Craft
    }
}
