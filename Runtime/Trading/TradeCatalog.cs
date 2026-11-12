using System;
using System.Collections.Generic;
using UnityEngine;

namespace EasyResources.Trading
{
    [CreateAssetMenu(menuName = "EasyResources/Trading/Catalog", fileName = "New Trade Catalog")]
    public sealed class TradeCatalog : DefinitionAsset
    {
        [SerializeField]
        private TradeOffer[] offers = Array.Empty<TradeOffer>();
        [NonSerialized]
        private System.Collections.ObjectModel.ReadOnlyCollection<TradeOffer> view;
        public IReadOnlyList<TradeOffer> Offers => view ?? (view = Array.AsReadOnly(offers));
        internal TradeOffer[] Rows => offers;

        public override void ValidateDefinition()
        {
            base.ValidateDefinition();
            if (offers == null)
                throw new ArgumentException("Offers cannot be null.");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var offer in offers)
            {
                if (offer == null || offer.Resource == null || offer.BundleSize <= 0 || offer.BuyPrice == null || offer.SellPrice == null || !ids.Add(offer.Resource.Id))
                    throw new ArgumentException("Assign one valid offer per resource, with a positive bundle size and both prices.");
                offer.BuyPrice.Prepare();
                offer.SellPrice.Prepare();
            }
        }

        protected override void OnValidate()
        {
            base.OnValidate();
            view = null;
            if (offers != null)
                foreach (var offer in offers)
                {
                    offer?.BuyPrice?.OnAfterDeserialize();
                    offer?.SellPrice?.OnAfterDeserialize();
                }
        }

        public static TradeCatalog Create(string name, params TradeOffer[] offers)
        {
            if (offers == null)
                throw new ArgumentNullException(nameof(offers));
            var result = New<TradeCatalog>(name);
            result.offers = (TradeOffer[])offers.Clone();
            return result;
        }
    }
}
