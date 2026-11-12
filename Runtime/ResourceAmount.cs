using System;
using UnityEngine;

namespace EasyResources
{
    [Serializable]
    public struct ResourceAmount
    {
        [SerializeField]
        private ResourceDefinition resource;
        [SerializeField, Min(0)]
        private long amount;
        public ResourceDefinition Resource => resource;
        public long Amount => amount;

        public ResourceAmount(ResourceDefinition resource, long amount)
        {
            if (resource == null)
                throw new ArgumentNullException(nameof(resource), "Assign a Resource asset.");
            if (amount < 0)
                throw new ArgumentOutOfRangeException(nameof(amount), "Amount must be nonnegative.");
            this.resource = resource;
            this.amount = amount;
        }

        public static ResourceAmount FromQuantity(ResourceDefinition resource, decimal quantity) => new ResourceAmount(resource, resource.ToUnits(quantity));
    }
}
