using System;
using UnityEngine;

namespace EasyResources.Trading
{
    [AddComponentMenu("EasyResources/Trading/Resource Trader")]
    public sealed class ResourceTrader : MonoBehaviour
    {
        [Serializable]
        private sealed class Recipients
        {
            public ResourceContainer Tax;
            public ResourceContainer Fee;
        }

        [SerializeField]
        private TradeCatalog catalog;
        [SerializeField]
        private ResourceContainer till;
        [SerializeField, Tooltip("Empty means an explicitly unlimited supplier.")]
        private ResourceContainer stock;
        [SerializeField]
        private Recipients additionalRecipients = new Recipients();
        private ResourceShop shop;
        public ResourceShop Shop
        {
            get
            {
                if (shop != null)
                    return shop;
                if (till == null)
                    till = GetComponent<ResourceContainer>();
                if (catalog == null || till == null)
                    throw new InvalidOperationException("Resource Trader: assign Catalog and Till, or add a Resource Container to this object.");
                return shop = new ResourceShop(catalog, till.Store, stock == null ? null : stock.Store, additionalRecipients.Tax == null ? null : additionalRecipients.Tax.Store, additionalRecipients.Fee == null ? null : additionalRecipients.Fee.Store);
            }
        }

        public bool TryBuy(ResourceContainer customer, ResourceDefinition resource, long bundles, out ResourceFailure failure) => Shop.TryBuy(customer.Store, resource, bundles, out failure);
        public bool TrySell(ResourceContainer customer, ResourceDefinition resource, long bundles, out ResourceFailure failure) => Shop.TrySell(customer.Store, resource, bundles, out failure);
    }
}
