using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using UnityEngine;

namespace EasyResources
{
    /// <summary>Inspector-friendly composite cost. Reuse a cost; its validated calculation is cached.</summary>
    [Serializable]
    public sealed class ResourceCost : ISerializationCallbackReceiver
    {
        [SerializeField]
        private ResourceAmount[] items = Array.Empty<ResourceAmount>();
        [NonSerialized]
        private ResourceAmount[] prepared;
        [NonSerialized]
        private ReadOnlyCollection<ResourceAmount> view;
        public IReadOnlyList<ResourceAmount> Items => view ?? (view = Array.AsReadOnly(items));

        public ResourceCost()
        {
        }

        public ResourceCost(params ResourceAmount[] items)
        {
            if (items == null)
                throw new ArgumentNullException(nameof(items));
            this.items = (ResourceAmount[])items.Clone();
            Prepare();
        }

        /// <summary>Validate and warm the cost before a frequent operation. Duplicate rows are summed.</summary>
        public void Prepare() => GetPrepared();
        public bool IsEmpty => GetPrepared().Length == 0;

        public ResourceCost Multiply(long count) => Scale(count);
        public ResourceCost Scale(long numerator, long denominator = 1, ResourceRounding rounding = ResourceRounding.Exact)
        {
            var source = GetPrepared();
            var result = new ResourceAmount[source.Length];
            for (var i = 0; i < source.Length; i++)
                result[i] = new ResourceAmount(source[i].Resource, QuantityMath.Scale(source[i].Amount, numerator, denominator, rounding));
            return new ResourceCost(result);
        }

        public static ResourceCost Merge(params ResourceCost[] costs)
        {
            if (costs == null)
                throw new ArgumentNullException(nameof(costs));
            var rows = new List<ResourceAmount>();
            foreach (var cost in costs)
            {
                if (cost == null)
                    throw new ArgumentNullException(nameof(costs));
                rows.AddRange(cost.GetPrepared());
            }

            return new ResourceCost(rows.ToArray());
        }

        public long GetAmount(ResourceDefinition resource)
        {
            if (resource == null)
                throw new ArgumentNullException(nameof(resource));
            foreach (var row in GetPrepared())
                if (ReferenceEquals(row.Resource, resource))
                    return row.Amount;
            return 0;
        }

        public ResourceCost Subtract(ResourceCost deduction)
        {
            if (deduction == null)
                throw new ArgumentNullException(nameof(deduction));
            foreach (var row in deduction.GetPrepared())
                if (row.Amount > GetAmount(row.Resource))
                    throw new ArgumentException("Deduction exceeds the original cost.");
            var original = GetPrepared();
            var result = new ResourceAmount[original.Length];
            for (var i = 0; i < original.Length; i++)
                result[i] = new ResourceAmount(original[i].Resource, original[i].Amount - deduction.GetAmount(original[i].Resource));
            return new ResourceCost(result);
        }

        internal ResourceAmount[] GetPrepared()
        {
            if (prepared != null)
                return prepared;
            if (items == null)
                throw new ArgumentException("Cost Items cannot be null. Use an empty cost for a free action.");
            var sums = new Dictionary<string, ResourceAmount>(items.Length, StringComparer.Ordinal);
            foreach (var item in items)
            {
                var resource = item.Resource;
                if (resource == null)
                    throw new ArgumentException("A resource row has no Resource. Assign an asset or remove the row.");
                if (string.IsNullOrWhiteSpace(resource.Id))
                    throw new ArgumentException("Resource has an empty stable ID.");
                if (item.Amount < 0)
                    throw new ArgumentException("Resource Amount must be zero or greater.");
                if (sums.TryGetValue(resource.Id, out var previous))
                {
                    if (!ReferenceEquals(previous.Resource, resource))
                        throw new ArgumentException($"Duplicate resource ID '{resource.Id}'. Give the duplicated asset a new ID.");
                    sums[resource.Id] = new ResourceAmount(resource, checked(previous.Amount + item.Amount));
                }
                else
                    sums.Add(resource.Id, item);
            }

            var nonzero = new List<ResourceAmount>(sums.Count);
            foreach (var item in sums.Values)
                if (item.Amount != 0)
                    nonzero.Add(item);
            prepared = nonzero.ToArray();
            return prepared;
        }

        public void OnBeforeSerialize()
        {
        }

        public void OnAfterDeserialize()
        {
            prepared = null;
            view = null;
        }
    }
}
