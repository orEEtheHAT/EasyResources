using System;
using System.Collections.Generic;
using UnityEngine;

namespace EasyResources.Trading
{
    [Serializable]
    public sealed class TradeOffer
    {
        [SerializeField]
        private ResourceDefinition resource;
        [SerializeField, Min(1)]
        private long bundleSize = 1;
        [SerializeField]
        private ResourceCost buyPrice = new ResourceCost();
        [SerializeField]
        private ResourceCost sellPrice = new ResourceCost();
        public ResourceDefinition Resource => resource;
        public long BundleSize => bundleSize;
        public ResourceCost BuyPrice => buyPrice;
        public ResourceCost SellPrice => sellPrice;

        public TradeOffer(ResourceDefinition resource, long bundleSize, ResourceCost buyPrice, ResourceCost sellPrice)
        {
            if (resource == null || bundleSize <= 0 || buyPrice == null || sellPrice == null)
                throw new ArgumentException("Invalid trade offer.");
            this.resource = resource;
            this.bundleSize = bundleSize;
            this.buyPrice = buyPrice;
            this.sellPrice = sellPrice;
        }
    }

    public readonly struct TradeTerms
    {
        public long PriceNumerator { get; }
        public long PriceDenominator { get; }
        public int TaxBasisPoints { get; }
        public int FeeBasisPoints { get; }

        public TradeTerms(long priceNumerator = 1, long priceDenominator = 1, int taxBasisPoints = 0, int feeBasisPoints = 0)
        {
            if (priceNumerator < 0 || priceDenominator <= 0 || taxBasisPoints < 0 || taxBasisPoints > 10000 || feeBasisPoints < 0 || feeBasisPoints > 10000)
                throw new ArgumentException("Invalid price, tax or fee.");
            PriceNumerator = priceNumerator;
            PriceDenominator = priceDenominator;
            TaxBasisPoints = taxBasisPoints;
            FeeBasisPoints = feeBasisPoints;
        }

        internal long Numerator => PriceDenominator == 0 ? 1 : PriceNumerator;
        internal long Denominator => PriceDenominator == 0 ? 1 : PriceDenominator;
    }

    /// <summary>Frozen price breakdown. Create again when the shop's price revision changes.</summary>
    public sealed class TradeQuote
    {
        internal ResourceShop Shop;
        internal bool Buying;
        public ResourceDefinition Resource { get; internal set; }
        public long Quantity { get; internal set; }
        public ResourceCost BasePrice { get; internal set; }
        public ResourceCost Tax { get; internal set; }
        public ResourceCost Fee { get; internal set; }
        public ResourceCost TotalPrice { get; internal set; }
        public long PriceRevision { get; internal set; }
    }

    /// <summary>Server/game-owned prices. Stock, buyer, till and fee recipients change atomically.</summary>
    public sealed class ResourceShop
    {
        private readonly Dictionary<ResourceDefinition, TradeOffer> offers = new Dictionary<ResourceDefinition, TradeOffer>(ReferenceComparer<ResourceDefinition>.Instance);
        private readonly ResourceStore stock, till, taxRecipient, feeRecipient;
        private readonly ResourceTransaction transaction = new ResourceTransaction();
        private readonly SafeSignal changed = new SafeSignal();
        private long priceRevision;
        private bool busy;
        public long PriceRevision => priceRevision;

        public event Action PricesChanged { add => changed.Add(value); remove => changed.Remove(value); }

        /// <param name = "stock">Null explicitly means an unlimited supplier. Till is always a real store.</param>
        public ResourceShop(TradeCatalog catalog, ResourceStore till, ResourceStore stock = null, ResourceStore taxRecipient = null, ResourceStore feeRecipient = null)
        {
            if (catalog == null || till == null)
                throw new ArgumentNullException("Assign a catalog and a till.");
            this.till = till;
            this.stock = stock;
            this.taxRecipient = taxRecipient ?? till;
            this.feeRecipient = feeRecipient ?? till;
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var offer in catalog.Rows)
            {
                if (offer == null || offer.Resource == null || offer.BundleSize <= 0 || offer.BuyPrice == null || offer.SellPrice == null || !ids.Add(offer.Resource.Id))
                    throw new ArgumentException("Invalid or duplicate catalog offer.");
                offers.Add(offer.Resource, new TradeOffer(offer.Resource, offer.BundleSize, new ResourceCost(offer.BuyPrice.GetPrepared()), new ResourceCost(offer.SellPrice.GetPrepared())));
            }
        }

        public void SetPrice(ResourceDefinition resource, ResourceCost buyPrice, ResourceCost sellPrice)
        {
            till.CheckMutation();
            if (busy)
                throw new InvalidOperationException("Cannot change prices during a trade notification.");
            if (!offers.TryGetValue(resource, out var old) || buyPrice == null || sellPrice == null)
                throw new ArgumentException("Unknown offer or null price.");
            var nextRevision = checked(priceRevision + 1);
            var replacement = new TradeOffer(resource, old.BundleSize, new ResourceCost(buyPrice.GetPrepared()), new ResourceCost(sellPrice.GetPrepared()));
            offers[resource] = replacement;
            priceRevision = nextRevision;
            busy = true;
            try
            {
                changed.Emit();
            }
            finally
            {
                busy = false;
            }
        }

        public TradeQuote QuoteBuy(ResourceDefinition resource, long bundles = 1, TradeTerms terms = default) => Quote(resource, bundles, true, terms);
        public TradeQuote QuoteSell(ResourceDefinition resource, long bundles = 1, TradeTerms terms = default) => Quote(resource, bundles, false, terms);
        private TradeQuote Quote(ResourceDefinition resource, long bundles, bool buying, TradeTerms terms)
        {
            till.CheckAlive();
            if (bundles <= 0 || resource == null || !offers.TryGetValue(resource, out var offer))
                throw new ArgumentException("Choose an offered resource and positive bundle count.");
            var price = (buying ? offer.BuyPrice : offer.SellPrice).Multiply(bundles).Scale(terms.Numerator, terms.Denominator, buying ? ResourceRounding.Up : ResourceRounding.Down);
            var tax = price.Scale(terms.TaxBasisPoints, 10000, ResourceRounding.Up);
            var fee = price.Scale(terms.FeeBasisPoints, 10000, ResourceRounding.Up);
            if (!buying)
                foreach (var row in ResourceCost.Merge(tax, fee).GetPrepared())
                    if (row.Amount > price.GetAmount(row.Resource))
                        throw new ArgumentException("Sale deductions exceed the proceeds.");
            return new TradeQuote
            {
                Shop = this,
                Buying = buying,
                Resource = resource,
                Quantity = checked(bundles * offer.BundleSize),
                BasePrice = price,
                Tax = tax,
                Fee = fee,
                TotalPrice = buying ? ResourceCost.Merge(price, tax, fee) : price.Subtract(ResourceCost.Merge(tax, fee)),
                PriceRevision = priceRevision
            };
        }

        public bool TryBuy(ResourceStore buyer, ResourceDefinition resource, long bundles, out ResourceFailure failure, TradeTerms terms = default) => TryExecute(buyer, QuoteBuy(resource, bundles, terms), out failure);
        public bool TrySell(ResourceStore seller, ResourceDefinition resource, long bundles, out ResourceFailure failure, TradeTerms terms = default) => TryExecute(seller, QuoteSell(resource, bundles, terms), out failure);
        public bool TryExecute(ResourceStore customer, TradeQuote quote, out ResourceFailure failure)
        {
            if (customer == null || quote == null || !ReferenceEquals(quote.Shop, this))
                throw new ArgumentException("Quote belongs to another shop.");
            if (busy)
                throw new InvalidOperationException("Cannot reenter a trade.");
            if (quote.PriceRevision != priceRevision)
            {
                failure = new ResourceFailure(ResourceFailureCode.StaleRevision, detail: "Prices changed. Request a new quote.");
                return false;
            }

            transaction.Clear();
            if (quote.Buying)
            {
                if (stock != null)
                    transaction.Spend(stock, quote.Resource, quote.Quantity);
                transaction.Add(customer, quote.Resource, quote.Quantity);
                transaction.Transfer(customer, till, quote.BasePrice).Transfer(customer, taxRecipient, quote.Tax).Transfer(customer, feeRecipient, quote.Fee);
            }
            else
            {
                transaction.Spend(customer, quote.Resource, quote.Quantity);
                if (stock != null)
                    transaction.Add(stock, quote.Resource, quote.Quantity);
                // All deductions are paid by the till from gross proceeds; no incoming credit is used to fund a debit.
                foreach (var row in quote.BasePrice.GetPrepared())
                {
                    var tax = quote.Tax.GetAmount(row.Resource);
                    var fee = quote.Fee.GetAmount(row.Resource);
                    transaction.Spend(till, row.Resource, row.Amount).Add(customer, row.Resource, row.Amount - tax - fee);
                    if (tax != 0)
                        transaction.Add(taxRecipient, row.Resource, tax);
                    if (fee != 0)
                        transaction.Add(feeRecipient, row.Resource, fee);
                }
            }

            busy = true;
            try
            {
                return transaction.TryExecute(out failure, new ResourceOperationContext(quote.Buying ? "buy" : "sell"), ResourceChangeKind.Exchange);
            }
            finally
            {
                busy = false;
            }
        }

        public ResourceShopState CaptureState()
        {
            till.CheckAlive();
            var state = new ResourceShopState
            {
                PriceRevision = priceRevision
            };
            foreach (var offer in offers.Values)
                state.Offers.Add(new TradeOfferState { ResourceId = offer.Resource.Id, BundleSize = offer.BundleSize, BuyPrice = SnapshotRows.Capture(offer.BuyPrice.GetPrepared()), SellPrice = SnapshotRows.Capture(offer.SellPrice.GetPrepared()) });
            return state;
        }

        public void RestoreState(ResourceShopState state, IEnumerable<ResourceDefinition> definitions)
        {
            till.CheckMutation();
            if (busy)
                throw new InvalidOperationException("Shop is busy.");
            if (state == null || state.Version != 1 || state.PriceRevision < 0 || state.Offers == null || state.Offers.Count != offers.Count)
                throw new ArgumentException("Invalid shop snapshot.");
            var catalog = new DefinitionLookup<ResourceDefinition>(definitions);
            var replacement = new Dictionary<ResourceDefinition, TradeOffer>(ReferenceComparer<ResourceDefinition>.Instance);
            foreach (var row in state.Offers)
            {
                if (row == null || string.IsNullOrWhiteSpace(row.ResourceId))
                    throw new ArgumentException("Invalid offer snapshot.");
                var resource = catalog.Get(row.ResourceId);
                if (!offers.TryGetValue(resource, out var current) || row.BundleSize != current.BundleSize || replacement.ContainsKey(resource))
                    throw new ArgumentException("Shop catalog changed or contains duplicate offers.");
                replacement.Add(resource, new TradeOffer(resource, row.BundleSize, SnapshotRows.Restore(row.BuyPrice, catalog), SnapshotRows.Restore(row.SellPrice, catalog)));
            }

            var revision = checked(Math.Max(priceRevision, state.PriceRevision) + 1);
            offers.Clear();
            foreach (var pair in replacement)
                offers.Add(pair.Key, pair.Value);
            priceRevision = revision;
            busy = true;
            try
            {
                changed.Emit();
            }
            finally
            {
                busy = false;
            }
        }
    }

    [Serializable]
    public sealed class ResourceShopState
    {
        public int Version = 1;
        public long PriceRevision;
        public List<TradeOfferState> Offers = new List<TradeOfferState>();
    }

    [Serializable]
    public sealed class TradeOfferState
    {
        public string ResourceId;
        public long BundleSize;
        public List<ResourceStateEntry> BuyPrice;
        public List<ResourceStateEntry> SellPrice;
    }
}
