using System;
using System.Collections.Generic;
using UnityEngine;

namespace EasyResources.Production
{
    [Serializable]
    public struct ResourceRate
    {
        [SerializeField]
        private ResourceDefinition resource;
        [SerializeField, Tooltip("Storage units per second. Positive produces; negative consumes.")]
        private long unitsPerSecond;
        public ResourceDefinition Resource => resource;
        public long UnitsPerSecond => unitsPerSecond;

        public ResourceRate(ResourceDefinition resource, long unitsPerSecond)
        {
            this.resource = resource ?? throw new ArgumentNullException(nameof(resource));
            this.unitsPerSecond = unitsPerSecond;
        }

        public static ResourceRate FromQuantity(ResourceDefinition resource, decimal perSecond) => new ResourceRate(resource, resource.ToUnits(perSecond));
    }

    /// <summary>Exact rates with retained sub-unit remainders. A blocked tick changes neither resources nor remainders.</summary>
    public sealed class ResourceFlow : IResourceParticipant
    {
        private readonly ResourceStore store;
        private readonly ResourceRate[] rates;
        private readonly long[] remainder, nextRemainder;
        private readonly ResourceTransaction transaction = new ResourceTransaction();
        private bool busy;
        public ResourceFailure LastFailure { get; private set; }

        public ResourceFlow(ResourceStore store, params ResourceRate[] rates)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            if (rates == null)
                throw new ArgumentNullException(nameof(rates));
            this.rates = (ResourceRate[])rates.Clone();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var rate in rates)
                if (rate.Resource == null || !seen.Add(rate.Resource.Id))
                    throw new ArgumentException("Use one signed rate per resource.");
            remainder = new long[rates.Length];
            nextRemainder = new long[rates.Length];
        }

        public bool Tick(long elapsedMilliseconds)
        {
            store.CheckMutation();
            if (busy)
                throw new InvalidOperationException("Flow is publishing a tick.");
            if (elapsedMilliseconds < 0)
                throw new ArgumentOutOfRangeException(nameof(elapsedMilliseconds));
            if (store.IsReadOnly)
            {
                LastFailure = new ResourceFailure(ResourceFailureCode.ReadOnly);
                return false;
            }

            transaction.Clear();
            try
            {
                for (var i = 0; i < rates.Length; i++)
                {
                    var total = checked((decimal)rates[i].UnitsPerSecond * elapsedMilliseconds + remainder[i]);
                    var whole = checked((long)decimal.Truncate(total / 1000m));
                    nextRemainder[i] = (long)(total - (decimal)whole * 1000m);
                    if (whole > 0)
                        transaction.Add(store, rates[i].Resource, whole);
                    else if (whole < 0)
                        transaction.Spend(store, rates[i].Resource, checked(-whole));
                }
            }
            catch (OverflowException)
            {
                LastFailure = new ResourceFailure(ResourceFailureCode.AmountOverflow);
                return false;
            }

            if (!transaction.Join(this).TryExecute(out var failure, new ResourceOperationContext("flow")))
            {
                LastFailure = failure;
                return false;
            }

            return true;
        }

        void IResourceParticipant.Enter()
        {
            if (busy)
                throw new InvalidOperationException("Flow is busy.");
            busy = true;
        }

        bool IResourceParticipant.Prepare(out ResourceFailure failure)
        {
            failure = default;
            return true;
        }

        void IResourceParticipant.Apply()
        {
            Array.Copy(nextRemainder, remainder, remainder.Length);
            LastFailure = default;
        }

        void IResourceParticipant.Publish()
        {
        }

        void IResourceParticipant.Exit() => busy = false;
        void IResourceParticipant.Finish()
        {
        }

        public ResourceFlowState CaptureState()
        {
            store.CheckAlive();
            var state = new ResourceFlowState();
            for (var i = 0; i < rates.Length; i++)
                state.Rates.Add(new ResourceFlowRemainder { ResourceId = rates[i].Resource.Id, UnitsPerSecond = rates[i].UnitsPerSecond, DecimalPlaces = rates[i].Resource.DecimalPlaces, Remainder = remainder[i] });
            return state;
        }

        public void RestoreState(ResourceFlowState state)
        {
            store.CheckMutation();
            if (busy)
                throw new InvalidOperationException("Flow is busy.");
            if (state == null || state.Version != 1 || state.Rates == null || state.Rates.Count != rates.Length)
                throw new ArgumentException("Invalid flow snapshot.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var candidate = new long[rates.Length];
            foreach (var row in state.Rates)
            {
                if (row == null || string.IsNullOrWhiteSpace(row.ResourceId) || !seen.Add(row.ResourceId) || row.Remainder <= -1000 || row.Remainder >= 1000)
                    throw new ArgumentException("Invalid flow remainder.");
                var index = Array.FindIndex(rates, x => x.Resource.Id == row.ResourceId);
                if (index < 0 || row.UnitsPerSecond != rates[index].UnitsPerSecond || row.DecimalPlaces != rates[index].Resource.DecimalPlaces || (row.Remainder != 0 && Math.Sign(row.Remainder) != Math.Sign(row.UnitsPerSecond)))
                    throw new ArgumentException("Flow configuration changed; migrate or reset its remainder explicitly.");
                candidate[index] = row.Remainder;
            }

            Array.Copy(candidate, remainder, candidate.Length);
        }
    }

    [Serializable]
    public sealed class ResourceFlowState
    {
        public int Version = 1;
        public List<ResourceFlowRemainder> Rates = new List<ResourceFlowRemainder>();
    }

    [Serializable]
    public sealed class ResourceFlowRemainder
    {
        public string ResourceId;
        public long UnitsPerSecond;
        public int DecimalPlaces;
        public long Remainder;
    }
}
