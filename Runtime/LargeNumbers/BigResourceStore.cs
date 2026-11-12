using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Threading;

namespace EasyResources.LargeNumbers
{
    public readonly struct BigResourceAmount
    {
        public ResourceDefinition Resource { get; }
        public BigInteger Amount { get; }

        public BigResourceAmount(ResourceDefinition resource, BigInteger amount)
        {
            if (resource == null)
                throw new ArgumentNullException(nameof(resource));
            if (amount.Sign < 0)
                throw new ArgumentOutOfRangeException(nameof(amount));
            Resource = resource;
            Amount = amount;
        }
    }

    public sealed class BigResourceCost
    {
        internal readonly BigResourceAmount[] Rows;
        public int Count => Rows.Length;

        public BigResourceAmount this[int index] => Rows[index];
        public BigResourceCost(params BigResourceAmount[] amounts)
        {
            if (amounts == null)
                throw new ArgumentNullException(nameof(amounts));
            var values = new Dictionary<ResourceDefinition, BigInteger>(ReferenceComparer<ResourceDefinition>.Instance);
            var ids = new Dictionary<string, ResourceDefinition>(StringComparer.Ordinal);
            foreach (var row in amounts)
            {
                if (row.Resource == null || row.Amount.Sign < 0 || string.IsNullOrWhiteSpace(row.Resource.Id))
                    throw new ArgumentException("Invalid large resource quantity.");
                if (ids.TryGetValue(row.Resource.Id, out var old) && !ReferenceEquals(old, row.Resource))
                    throw new ArgumentException("Duplicate Resource ID.");
                ids[row.Resource.Id] = row.Resource;
                values.TryGetValue(row.Resource, out var amount);
                values[row.Resource] = amount + row.Amount;
            }

            Rows = new BigResourceAmount[values.Count];
            var i = 0;
            foreach (var pair in values)
                Rows[i++] = new BigResourceAmount(pair.Key, pair.Value);
        }

        public BigInteger GetAmount(ResourceDefinition resource)
        {
            foreach (var row in Rows)
                if (ReferenceEquals(row.Resource, resource))
                    return row.Amount;
            return BigInteger.Zero;
        }

        public BigResourceCost Multiply(BigInteger factor)
        {
            if (factor.Sign < 0)
                throw new ArgumentOutOfRangeException(nameof(factor));
            var rows = new BigResourceAmount[Rows.Length];
            for (var i = 0; i < rows.Length; i++)
                rows[i] = new BigResourceAmount(Rows[i].Resource, Rows[i].Amount * factor);
            return new BigResourceCost(rows);
        }

        public static BigResourceCost From(ResourceCost cost)
        {
            if (cost == null)
                throw new ArgumentNullException(nameof(cost));
            var source = cost.GetPrepared();
            var rows = new BigResourceAmount[source.Length];
            for (var i = 0; i < rows.Length; i++)
                rows[i] = new BigResourceAmount(source[i].Resource, source[i].Amount);
            return new BigResourceCost(rows);
        }
    }

    public readonly struct BigResourceReservation : IDisposable
    {
        private readonly BigResourceStore store;
        private readonly long generation;
        public long Id { get; }

        internal BigResourceReservation(BigResourceStore store, long id, long generation)
        {
            this.store = store;
            Id = id;
            this.generation = generation;
        }

        public bool IsActive => store != null && store.Active(Id, generation);

        public bool Commit() => store != null && store.Complete(Id, generation, true);
        public bool Cancel() => store != null && store.Complete(Id, generation, false);
        public void Dispose() => Cancel();
    }

    /// <summary>Exact arbitrary-size quantities for idle economies. BigInteger operations and snapshots allocate.</summary>
    public sealed class BigResourceStore : IDisposable, IResourceParticipant
    {
        private readonly struct Balance
        {
            internal readonly BigInteger Total, Reserved;
            internal Balance(BigInteger total, BigInteger reserved)
            {
                Total = total;
                Reserved = reserved;
            }
        }

        private sealed class Hold
        {
            internal BigResourceCost Cost;
            internal ResourceReservationOptions Options;
        }

        private Dictionary<ResourceDefinition, Balance> balances = new Dictionary<ResourceDefinition, Balance>(ReferenceComparer<ResourceDefinition>.Instance);
        private Dictionary<ResourceDefinition, Balance> candidate;
        private readonly Dictionary<long, Hold> holds = new Dictionary<long, Hold>();
        private readonly Dictionary<string, ResourceDefinition> identities = new Dictionary<string, ResourceDefinition>(StringComparer.Ordinal);
        private readonly BigResourceCost maximums;
        private readonly SafeSignal changed = new SafeSignal();
        private readonly ResourceTransaction transaction = new ResourceTransaction();
        private readonly int thread = Thread.CurrentThread.ManagedThreadId;
        private bool busy, disposed;
        private long nextId, generation = 1, removeHold;
        private Hold addHold;
        public long Revision { get; private set; }
        public int ActiveReservationCount => holds.Count;

        public event Action Changed
        {
            add
            {
                Check();
                changed.Add(value);
            }

            remove
            {
                CheckThread();
                changed.Remove(value);
            }
        }

        public BigResourceStore(BigResourceCost initial = null, BigResourceCost maximums = null)
        {
            this.maximums = maximums;
            if (initial != null && !TryAdd(initial, out var failure))
                throw new ArgumentException(failure.ToString());
            Revision = 0;
        }

        private Balance Read(ResourceDefinition resource)
        {
            Check();
            if (resource == null || string.IsNullOrWhiteSpace(resource.Id))
                throw new ArgumentException("Assign a Resource asset.");
            if (identities.TryGetValue(resource.Id, out var old) && !ReferenceEquals(old, resource))
                throw new ArgumentException("Duplicate Resource ID.");
            return balances.TryGetValue(resource, out var value) ? value : default;
        }

        public BigInteger GetTotal(ResourceDefinition resource) => Read(resource).Total;
        public BigInteger GetReserved(ResourceDefinition resource) => Read(resource).Reserved;
        public BigInteger GetAvailable(ResourceDefinition resource)
        {
            var row = Read(resource);
            return row.Total - row.Reserved;
        }

        public bool CanAfford(BigResourceCost cost)
        {
            Check();
            if (cost == null)
                throw new ArgumentNullException(nameof(cost));
            foreach (var row in cost.Rows)
                if (GetAvailable(row.Resource) < row.Amount)
                    return false;
            return true;
        }

        public bool TryAdd(BigResourceCost cost, out ResourceFailure failure)
        {
            CheckMutation();
            Begin();
            if (!Change(cost, 1, 0, false, out failure))
                return false;
            return transaction.Clear().Join(this).TryExecute(out failure);
        }

        public bool TrySpend(BigResourceCost cost, out ResourceFailure failure)
        {
            CheckMutation();
            Begin();
            if (!Change(cost, -1, 0, true, out failure))
                return false;
            return transaction.Clear().Join(this).TryExecute(out failure);
        }

        public bool TryExchange(BigResourceCost payment, BigResourceCost reward, out ResourceFailure failure)
        {
            CheckMutation();
            Begin();
            if (!Change(payment, -1, 0, true, out failure) || !Change(reward, 1, 0, false, out failure))
                return false;
            return transaction.Clear().Join(this).TryExecute(out failure);
        }

        public bool TryTransferTo(BigResourceStore destination, BigResourceCost cost, out ResourceFailure failure)
        {
            CheckMutation();
            if (destination == null)
                throw new ArgumentNullException(nameof(destination));
            destination.CheckMutation();
            if (ReferenceEquals(this, destination))
            {
                failure = CanAfford(cost) ? default : Rejected("Insufficient large resource balance.");
                return !failure.HasFailure;
            }

            Begin();
            destination.Begin();
            if (!Change(cost, -1, 0, true, out failure) || !destination.Change(cost, 1, 0, false, out failure))
                return false;
            return transaction.Clear().Join(this).Join(destination).TryExecute(out failure);
        }

        public bool TryImport(ResourceStore source, ResourceCost cost, out ResourceFailure failure)
        {
            CheckMutation();
            Begin();
            if (!Change(BigResourceCost.From(cost), 1, 0, false, out failure))
                return false;
            return transaction.Clear().Spend(source, cost).Join(this).TryExecute(out failure);
        }

        public bool TryExport(ResourceStore destination, ResourceCost cost, out ResourceFailure failure)
        {
            CheckMutation();
            Begin();
            if (!Change(BigResourceCost.From(cost), -1, 0, true, out failure))
                return false;
            return transaction.Clear().Add(destination, cost).Join(this).TryExecute(out failure);
        }

        public bool TryReserve(BigResourceCost cost, out BigResourceReservation reservation, ResourceReservationOptions options = default)
        {
            CheckMutation();
            reservation = default;
            if (options.HasExpiry && options.ExpiresAtMilliseconds <= options.CreatedAtMilliseconds)
                return false;
            Begin();
            if (nextId == long.MaxValue)
                throw new InvalidOperationException("Reservation IDs exhausted.");
            if (!Change(cost, 0, 1, true, out _))
                return false;
            addHold = new Hold
            {
                Cost = cost,
                Options = options
            };
            // Assign before callbacks so observers can see and retain the new token.
            reservation = new BigResourceReservation(this, nextId + 1, generation);
            if (transaction.Clear().Join(this).TryExecute())
                return true;
            reservation = default;
            return false;
        }

        internal bool Active(long id, long tokenGeneration)
        {
            CheckThread();
            return !disposed && generation == tokenGeneration && holds.ContainsKey(id);
        }

        internal bool Complete(long id, long tokenGeneration, bool spend)
        {
            if (!Active(id, tokenGeneration))
                return false;
            CheckMutation();
            var hold = holds[id];
            Begin();
            removeHold = id;
            if (!Change(hold.Cost, spend ? -1 : 0, -1, false, out _))
                return false;
            return transaction.Clear().Join(this).TryExecute();
        }

        public int ExpireReservations(long nowMilliseconds)
        {
            CheckMutation();
            if (nowMilliseconds < 0)
                throw new ArgumentOutOfRangeException(nameof(nowMilliseconds));
            var expired = new List<long>();
            foreach (var pair in holds)
                if (pair.Value.Options.HasExpiry && pair.Value.Options.ExpiresAtMilliseconds <= nowMilliseconds)
                    expired.Add(pair.Key);
            foreach (var id in expired)
                Complete(id, generation, false);
            return expired.Count;
        }

        public bool TryGetReservation(long id, out BigResourceReservation reservation)
        {
            Check();
            reservation = holds.ContainsKey(id) ? new BigResourceReservation(this, id, generation) : default;
            return reservation.IsActive;
        }

        private void Begin()
        {
            candidate = new Dictionary<ResourceDefinition, Balance>(balances, ReferenceComparer<ResourceDefinition>.Instance);
            removeHold = 0;
            addHold = null;
        }

        private bool Change(BigResourceCost cost, int totalSign, int reservedSign, bool requireAvailable, out ResourceFailure failure)
        {
            if (cost == null)
                throw new ArgumentNullException(nameof(cost));
            foreach (var row in cost.Rows)
            {
                var before = Read(row.Resource);
                if (requireAvailable && before.Total - before.Reserved < row.Amount)
                {
                    failure = Rejected("Insufficient large resource balance.");
                    return false;
                }

                candidate.TryGetValue(row.Resource, out var current);
                var next = new Balance(current.Total + row.Amount * totalSign, current.Reserved + row.Amount * reservedSign);
                if (next.Reserved.Sign < 0 || next.Total < next.Reserved)
                {
                    failure = Rejected("Insufficient large resource balance.");
                    return false;
                }

                candidate[row.Resource] = next;
            }

            failure = default;
            return true;
        }

        private static ResourceFailure Rejected(string detail) => new ResourceFailure(ResourceFailureCode.RuleRejected, detail: detail);
        void IResourceParticipant.Enter()
        {
            CheckMutation();
            busy = true;
        }

        bool IResourceParticipant.Prepare(out ResourceFailure failure)
        {
            if (Revision == long.MaxValue)
            {
                failure = new ResourceFailure(ResourceFailureCode.AmountOverflow);
                return false;
            }

            if (maximums != null)
                foreach (var maximum in maximums.Rows)
                    if (candidate.TryGetValue(maximum.Resource, out var balance) && balance.Total > maximum.Amount)
                    {
                        failure = Rejected("Large resource capacity exceeded.");
                        return false;
                    }

            var seen = new Dictionary<string, ResourceDefinition>(identities, StringComparer.Ordinal);
            foreach (var row in candidate)
            {
                if (seen.TryGetValue(row.Key.Id, out var old) && !ReferenceEquals(old, row.Key))
                    throw new ArgumentException("Duplicate Resource ID.");
                seen[row.Key.Id] = row.Key;
            }

            foreach (var pair in seen)
                identities[pair.Key] = pair.Value;
            if (addHold != null)
                holds.EnsureCapacity(holds.Count + 1);
            failure = default;
            return true;
        }

        void IResourceParticipant.Apply()
        {
            balances = candidate;
            candidate = null;
            if (removeHold != 0)
                holds.Remove(removeHold);
            if (addHold != null)
                holds.Add(++nextId, addHold);
            Revision++;
        }

        void IResourceParticipant.Publish() => changed.Emit();
        void IResourceParticipant.Exit() => busy = false;
        void IResourceParticipant.Finish()
        {
        }

        private void CheckThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != thread)
                throw new InvalidOperationException("Use large resources on their creating Unity thread.");
        }

        private void Check()
        {
            CheckThread();
            if (disposed)
                throw new ObjectDisposedException(nameof(BigResourceStore));
        }

        private void CheckMutation()
        {
            Check();
            if (busy)
                throw new InvalidOperationException("Queue changes until large resource notifications return.");
        }

        public void Dispose()
        {
            CheckThread();
            if (disposed)
                return;
            CheckMutation();
            disposed = true;
            balances.Clear();
            holds.Clear();
            identities.Clear();
            candidate = null;
            changed.Clear();
            transaction.RequestClear();
        }

        public BigResourceState CaptureState(bool includeReservations = false, long nowMilliseconds = 0)
        {
            Check();
            if (nowMilliseconds < 0)
                throw new ArgumentOutOfRangeException(nameof(nowMilliseconds));
            if (!includeReservations && holds.Count > 0)
                throw new InvalidOperationException("Include named reservations explicitly or finish them before saving.");
            var state = new BigResourceState
            {
                Revision = Revision
            };
            foreach (var row in balances)
                state.Resources.Add(Row(row.Key, row.Value.Total));
            if (includeReservations)
                foreach (var pair in holds)
                {
                    var options = pair.Value.Options;
                    if (string.IsNullOrWhiteSpace(options.OwnerId))
                        throw new InvalidOperationException("Persisted reservations require owner IDs.");
                    var hold = new BigReservationState
                    {
                        Id = pair.Key,
                        OwnerId = options.OwnerId,
                        HasExpiry = options.HasExpiry,
                        RemainingMilliseconds = options.HasExpiry ? Math.Max(0, options.ExpiresAtMilliseconds - nowMilliseconds) : 0
                    };
                    foreach (var row in pair.Value.Cost.Rows)
                        hold.Resources.Add(Row(row.Resource, row.Amount));
                    state.Reservations.Add(hold);
                }

            return state;
        }

        private static BigResourceRow Row(ResourceDefinition resource, BigInteger amount) => new BigResourceRow
        {
            ResourceId = resource.Id,
            DecimalPlaces = resource.DecimalPlaces,
            Amount = amount.ToString(CultureInfo.InvariantCulture)
        };
        public static BigResourceStore FromState(BigResourceState state, IEnumerable<ResourceDefinition> definitions, BigResourceCost maximums = null, long nowMilliseconds = 0)
        {
            if (state == null || state.Version != 1 || state.Revision < 0 || state.Resources == null || state.Reservations == null || nowMilliseconds < 0)
                throw new ArgumentException("Invalid large resource snapshot.");
            var lookup = new DefinitionLookup<ResourceDefinition>(definitions);
            var store = new BigResourceStore(Parse(state.Resources, lookup), maximums);
            var used = new HashSet<long>();
            foreach (var saved in state.Reservations)
            {
                if (saved == null || saved.Id <= 0 || saved.Id == long.MaxValue || !used.Add(saved.Id) || string.IsNullOrWhiteSpace(saved.OwnerId) || saved.RemainingMilliseconds < 0)
                    throw new ArgumentException("Invalid large reservation snapshot.");
                var cost = Parse(saved.Resources, lookup);
                if (!store.CanAfford(cost))
                    throw new ArgumentException("Large reservations exceed their balances.");
                foreach (var row in cost.Rows)
                {
                    store.balances.TryGetValue(row.Resource, out var before);
                    store.balances[row.Resource] = new Balance(before.Total, before.Reserved + row.Amount);
                    store.identities[row.Resource.Id] = row.Resource;
                }

                store.holds.Add(saved.Id, new Hold { Cost = cost, Options = new ResourceReservationOptions(saved.OwnerId, saved.HasExpiry ? checked(nowMilliseconds + saved.RemainingMilliseconds) : -1, nowMilliseconds) });
                store.nextId = Math.Max(store.nextId, saved.Id);
            }

            store.Revision = state.Revision;
            return store;
        }

        private static BigResourceCost Parse(List<BigResourceRow> rows, DefinitionLookup<ResourceDefinition> lookup)
        {
            if (rows == null)
                throw new ArgumentException("Missing large resource rows.");
            var result = new BigResourceAmount[rows.Count];
            var used = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (row == null || string.IsNullOrWhiteSpace(row.ResourceId) || !used.Add(row.ResourceId) || string.IsNullOrEmpty(row.Amount))
                    throw new ArgumentException("Invalid large resource row.");
                for (var c = 0; c < row.Amount.Length; c++)
                    if (row.Amount[c] < '0' || row.Amount[c] > '9')
                        throw new ArgumentException("Large quantities must contain decimal digits only.");
                var resource = lookup.Get(row.ResourceId);
                if (resource.DecimalPlaces != row.DecimalPlaces)
                    throw new ArgumentException("Migrate saved precision before loading.");
                result[i] = new BigResourceAmount(resource, BigInteger.Parse(row.Amount, CultureInfo.InvariantCulture));
            }

            return new BigResourceCost(result);
        }
    }

    [Serializable]
    public sealed class BigResourceState
    {
        public int Version = 1;
        public long Revision;
        public List<BigResourceRow> Resources = new List<BigResourceRow>();
        public List<BigReservationState> Reservations = new List<BigReservationState>();
    }

    [Serializable]
    public sealed class BigResourceRow
    {
        public string ResourceId;
        public int DecimalPlaces;
        public string Amount;
    }

    [Serializable]
    public sealed class BigReservationState
    {
        public long Id;
        public string OwnerId;
        public bool HasExpiry;
        public long RemainingMilliseconds;
        public List<BigResourceRow> Resources = new List<BigResourceRow>();
    }
}
