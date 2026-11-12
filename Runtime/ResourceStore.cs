using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace EasyResources
{
    /// <summary>Main-thread ledger. Reuse costs/transactions; state and notifications are atomic.</summary>
    public sealed partial class ResourceStore : IDisposable
    {
        internal sealed class ReservationSlot
        {
            private sealed class Metadata
            {
                internal ResourceReservationOptions Options;
                internal long AgeOffset;
            }

            private Metadata metadata;
            internal ResourceAmount[] Items = Array.Empty<ResourceAmount>();
            internal ResourceAmount[] Buffer = Array.Empty<ResourceAmount>();
            internal int Count;
            internal ResourceReservationOptions Options
            {
                get => metadata == null ? default : metadata.Options;
                set
                {
                    if (metadata == null)
                    {
                        if (value.OwnerId == null && value.CreatedAtMilliseconds == 0 && value.ExpiresAtMilliseconds == 0 && !value.HasExpiry)
                            return;
                        metadata = new Metadata();
                    }

                    metadata.Options = value;
                }
            }

            internal long AgeOffset
            {
                get => metadata == null ? 0 : metadata.AgeOffset;
                set
                {
                    if (metadata == null)
                    {
                        if (value == 0)
                            return;
                        metadata = new Metadata();
                    }

                    metadata.AgeOffset = value;
                }
            }

            internal long GetAmount(ResourceDefinition resource)
            {
                for (var i = 0; i < Count; i++)
                    if (ReferenceEquals(Items[i].Resource, resource))
                        return Items[i].Amount;
                return 0;
            }

            internal void MakeMutable()
            {
                if (ReferenceEquals(Items, Buffer))
                    return;
                if (Buffer.Length < Count)
                    Buffer = new ResourceAmount[Count];
                Array.Copy(Items, Buffer, Count);
                Items = Buffer;
            }

            internal void Clear()
            {
                Array.Clear(Buffer, 0, Buffer.Length);
                Items = Array.Empty<ResourceAmount>();
                Count = 0;
                Options = default;
                AgeOffset = 0;
            }
        }

        private Dictionary<ResourceDefinition, ResourceBalance> balances;
        private Dictionary<string, ResourceDefinition> identities;
        private Dictionary<long, ReservationSlot> reservations;
        private readonly List<ReservationSlot> freeSlots;
        private ResourceTransaction scratch;
        private ResourceTransaction Scratch => scratch ?? (scratch = new ResourceTransaction(2));

        private readonly List<ResourceChange> publication;
        private readonly List<long> reservationScratch;
        private readonly int ownerThread = Thread.CurrentThread.ManagedThreadId;
        private long generation = 1, nextReservationId, weight, volume;
        private long revision;
        private bool busy, disposed, readOnly, disposeRequested;
        private int replicaReservations;
        private ResourceLimits limits;
        private Action changed;
        private Action<ResourceChange> detailedChanged;
        private Delegate[] changedListeners = Array.Empty<Delegate>(), detailedListeners = Array.Empty<Delegate>();
        private Delegate[] publishingChanged, publishingDetailed;
        private ResourceChange[] history = Array.Empty<ResourceChange>();
        private int historyStart, historyCount;
        private long droppedThroughRevision;
        private bool hasPublicationRecords;
        internal long Generation
        {
            get
            {
                CheckAlive();
                return generation;
            }
        }

        public ResourceChangeKind LastChangeKind { get; private set; }
        public ResourceOperationContext LastContext { get; private set; }

        public event Action Changed
        {
            add
            {
                CheckAlive();
                changed += value;
                changedListeners = changed?.GetInvocationList() ?? Array.Empty<Delegate>();
            }

            remove
            {
                CheckThread();
                changed -= value;
                changedListeners = changed?.GetInvocationList() ?? Array.Empty<Delegate>();
            }
        }

        public event Action<ResourceChange> DetailedChanged
        {
            add
            {
                CheckAlive();
                detailedChanged += value;
                detailedListeners = detailedChanged?.GetInvocationList() ?? Array.Empty<Delegate>();
            }

            remove
            {
                CheckThread();
                detailedChanged -= value;
                detailedListeners = detailedChanged?.GetInvocationList() ?? Array.Empty<Delegate>();
            }
        }

        public long Revision
        {
            get
            {
                CheckAlive();
                return revision;
            }
        }

        public int ActiveReservationCount
        {
            get
            {
                CheckAlive();
                return readOnly ? replicaReservations : reservations.Count;
            }
        }

        public bool IsReadOnly
        {
            get
            {
                CheckAlive();
                return readOnly;
            }
        }

        public long UsedWeight
        {
            get
            {
                CheckAlive();
                if (limits.MaximumWeight.HasValue)
                    return weight;
                long total = 0;
                foreach (var row in balances.Values)
                    total = checked(total + checked(row.Total * row.Resource.WeightPerUnit));
                return total;
            }
        }

        public long UsedVolume
        {
            get
            {
                CheckAlive();
                if (limits.MaximumVolume.HasValue)
                    return volume;
                long total = 0;
                foreach (var row in balances.Values)
                    total = checked(total + checked(row.Total * row.Resource.VolumePerUnit));
                return total;
            }
        }

        public ResourceLimits Limits
        {
            get
            {
                CheckAlive();
                return limits;
            }
        }

        public long DroppedHistoryThroughRevision
        {
            get
            {
                CheckAlive();
                return droppedThroughRevision;
            }
        }

        public ResourceStore(ResourceCost initialContents = null, int resourceCapacity = 8, int reservationCapacity = 8, ResourceLimits limits = null, int historyCapacity = 0)
        {
            if (resourceCapacity < 0 || reservationCapacity < 0 || historyCapacity < 0)
                throw new ArgumentOutOfRangeException("Capacities must be nonnegative.");
            this.limits = limits ?? ResourceLimits.Unlimited;
            balances = new Dictionary<ResourceDefinition, ResourceBalance>(resourceCapacity, ReferenceComparer<ResourceDefinition>.Instance);
            identities = new Dictionary<string, ResourceDefinition>(resourceCapacity, StringComparer.Ordinal);
            reservations = new Dictionary<long, ReservationSlot>(reservationCapacity);
            freeSlots = new List<ReservationSlot>(reservationCapacity);
            publication = new List<ResourceChange>();
            reservationScratch = new List<long>();
            history = historyCapacity == 0 ? Array.Empty<ResourceChange>() : new ResourceChange[historyCapacity];
            if (initialContents != null)
                foreach (var row in initialContents.GetPrepared())
                {
                    ReadBalance(row.Resource);
                    EnsureEntry(row.Resource);
                    WriteBalance(new ResourceBalance(row.Resource, row.Amount, 0));
                }

            if (!ValidateContents(balances, this.limits, out weight, out volume, out var failure))
                throw new ArgumentException("Initial Contents: " + failure);
        }

        public long GetTotal(ResourceDefinition resource)
        {
            CheckAlive();
            return ReadBalance(resource).Total;
        }

        public long GetReserved(ResourceDefinition resource)
        {
            CheckAlive();
            return ReadBalance(resource).Reserved;
        }

        public long GetAvailable(ResourceDefinition resource)
        {
            CheckAlive();
            return ReadBalance(resource).Available;
        }

        public ResourceBalance GetBalance(ResourceDefinition resource)
        {
            CheckAlive();
            return ReadBalance(resource);
        }

        public void CopyBalancesTo(List<ResourceBalance> destination, bool includeZero = false)
        {
            CheckAlive();
            if (destination == null)
                throw new ArgumentNullException(nameof(destination));
            destination.Clear();
            foreach (var value in balances.Values)
                if (includeZero || value.Total != 0)
                    destination.Add(value);
        }

        public void Add(ResourceDefinition resource, long amount)
        {
            if (TryAdd(resource, amount, out var failure))
                return;
            if (failure.Code == ResourceFailureCode.AmountOverflow)
                throw new OverflowException(failure.ToString());
            throw new InvalidOperationException(failure.ToString());
        }

        public bool TryAdd(ResourceDefinition resource, long amount, out ResourceFailure failure)
        {
            CheckMutation();
            return AddSingle(resource, amount, out failure);
        }

        public long AddUpTo(ResourceDefinition resource, long requested)
        {
            CheckMutation();
            if (requested < 0)
                throw new ArgumentOutOfRangeException(nameof(requested));
            var current = ReadBalance(resource);
            if (readOnly || !limits.Accepts(resource))
                return 0;
            var accepted = Math.Min(requested, limits.GetMaximum(resource) - current.Total);
            if (limits.MaximumWeight.HasValue && resource.WeightPerUnit > 0)
                accepted = Math.Min(accepted, (limits.MaximumWeight.Value - weight) / resource.WeightPerUnit);
            if (limits.MaximumVolume.HasValue && resource.VolumePerUnit > 0)
                accepted = Math.Min(accepted, (limits.MaximumVolume.Value - volume) / resource.VolumePerUnit);
            if (accepted > 0)
                Add(resource, accepted);
            return accepted;
        }

        public bool CanAfford(ResourceCost cost) => CanAfford(cost, out _);
        public bool CanAfford(ResourceCost cost, out ResourceFailure failure)
        {
            CheckAlive();
            if (cost == null)
                throw new ArgumentNullException(nameof(cost));
            failure = default;
            foreach (var row in cost.GetPrepared())
            {
                var value = ReadBalance(row.Resource);
                if (value.Available < row.Amount && !failure.HasFailure)
                    failure = new ResourceFailure(ResourceFailureCode.InsufficientResource, row.Resource, this, row.Amount, value.Available);
            }

            return !failure.HasFailure;
        }

        public bool TrySpend(ResourceCost cost) => TrySpend(cost, out _);
        public bool TrySpend(ResourceCost cost, out ResourceFailure failure, ResourceOperationContext context = default)
        {
            CheckMutation();
            return SpendSingle(cost, out failure, context);
        }

        public bool TryTransferTo(ResourceStore destination, ResourceCost cost) => TryTransferTo(destination, cost, out _);
        public bool TryTransferTo(ResourceStore destination, ResourceCost cost, out ResourceFailure failure, ResourceOperationContext context = default)
        {
            CheckMutation();
            return Scratch.Clear().Transfer(this, destination, cost).TryExecute(out failure, context, ResourceChangeKind.Transfer);
        }

        public bool TryExchange(ResourceCost payment, ResourceCost reward) => TryExchange(payment, reward, out _);
        public bool TryExchange(ResourceCost payment, ResourceCost reward, out ResourceFailure failure, ResourceOperationContext context = default)
        {
            CheckMutation();
            return Scratch.Clear().Exchange(this, payment, reward).TryExecute(out failure, context, ResourceChangeKind.Exchange);
        }

        public bool TryReserve(ResourceCost cost, out ResourceReservation reservation) => TryReserve(cost, out reservation, out _);
        public bool TryReserve(ResourceCost cost, out ResourceReservation reservation, out ResourceFailure failure, ResourceReservationOptions options = default)
        {
            CheckMutation();
            reservation = default;
            if (readOnly)
            {
                failure = new ResourceFailure(ResourceFailureCode.ReadOnly, store: this);
                return false;
            }

            if (!CanAfford(cost, out failure))
                return false;
            if (options.HasExpiry && options.ExpiresAtMilliseconds <= options.CreatedAtMilliseconds)
            {
                failure = new ResourceFailure(ResourceFailureCode.ReservationExpired);
                return false;
            }

            if (nextReservationId == long.MaxValue || revision == long.MaxValue)
                throw new InvalidOperationException("Identifier range exhausted.");
            var items = cost.GetPrepared();
            var slot = freeSlots.Count == 0 ? new ReservationSlot() : freeSlots[freeSlots.Count - 1];
            var id = nextReservationId + 1;
            reservations.Add(id, slot);
            if (freeSlots.Count != 0)
                freeSlots.RemoveAt(freeSlots.Count - 1);
            nextReservationId = id;
            slot.Items = items;
            slot.Count = items.Length;
            slot.Options = options;
            busy = true;
            try
            {
                var context = new ResourceOperationContext("reserve", options.OwnerId, options.CreatedAtMilliseconds);
                BeginPublication(ResourceChangeKind.Reserve, context);
                foreach (var row in items)
                {
                    var before = balances[row.Resource];
                    var after = new ResourceBalance(row.Resource, before.Total, before.Reserved + row.Amount);
                    WriteBalance(after);
                    Record(before, after, ResourceChangeKind.Reserve, context);
                }

                reservation = new ResourceReservation(this, id, generation);
                Publish();
                failure = default;
                return true;
            }
            finally
            {
                busy = false;
                FinishDeferredDispose();
            }
        }

        internal bool TryGetSlot(ResourceReservation handle, out ReservationSlot slot)
        {
            CheckThread();
            slot = null;
            return !disposed && ReferenceEquals(handle.Owner, this) && handle.Generation == generation && reservations.TryGetValue(handle.Id, out slot);
        }

        public bool TryGetReservation(long id, out ResourceReservation reservation)
        {
            CheckAlive();
            reservation = default;
            if (!reservations.ContainsKey(id))
                return false;
            reservation = new ResourceReservation(this, id, generation);
            return true;
        }

        public void CopyReservationsTo(List<ResourceReservationInfo> destination)
        {
            CheckAlive();
            if (destination == null)
                throw new ArgumentNullException(nameof(destination));
            destination.Clear();
            foreach (var pair in reservations)
                destination.Add(new ResourceReservationInfo(new ResourceReservation(this, pair.Key, generation), pair.Value));
        }

        /// <summary>Rebind a saved hold only if it belongs to the exact expected persistent owner.</summary>
        public bool TryGetReservation(long id, string ownerId, out ResourceReservation reservation)
        {
            CheckAlive();
            if (string.IsNullOrWhiteSpace(ownerId))
                throw new ArgumentException("Supply the persistent reservation owner ID.", nameof(ownerId));
            reservation = default;
            if (!reservations.TryGetValue(id, out var slot) || !StringComparer.Ordinal.Equals(slot.Options.OwnerId, ownerId))
                return false;
            reservation = new ResourceReservation(this, id, generation);
            return true;
        }

        internal bool Complete(ResourceReservation reservation, ResourceCost part, bool cancel, ResourceChangeKind kind, out ResourceFailure failure)
        {
            if (!TryGetSlot(reservation, out var slot))
            {
                failure = new ResourceFailure(ResourceFailureCode.ReservationNotActive);
                return false;
            }

            CheckMutation();
            return CompleteSingle(reservation, slot, part, cancel, kind, out failure);
        }

        internal void PrepareCompletion(ResourceReservation reservation, ResourceAmount[] part)
        {
            if (part != null && part.Length != 0)
                reservations[reservation.Id].MakeMutable();
        }

        internal void ApplyCompletion(ResourceReservation reservation, ResourceAmount[] part)
        {
            var slot = reservations[reservation.Id];
            if (part != null)
            {
                if (part.Length == 0)
                    return;
                foreach (var row in part)
                    for (var i = 0; i < slot.Count; i++)
                        if (ReferenceEquals(slot.Items[i].Resource, row.Resource))
                            slot.Items[i] = new ResourceAmount(row.Resource, slot.Items[i].Amount - row.Amount);
                var write = 0;
                for (var i = 0; i < slot.Count; i++)
                    if (slot.Items[i].Amount != 0)
                        slot.Items[write++] = slot.Items[i];
                Array.Clear(slot.Items, write, slot.Count - write);
                slot.Count = write;
                if (write != 0)
                    return;
            }

            RecordDeferredReservationChange(reservation.Id);
            reservations.Remove(reservation.Id);
            slot.Clear();
            freeSlots.Add(slot);
        }

        public int CancelAll(string ownerId)
        {
            CheckThread();
            if (disposed)
                return 0;
            if (string.IsNullOrWhiteSpace(ownerId))
                throw new ArgumentException("Supply an owner ID.", nameof(ownerId));
            CheckMutation();
            reservationScratch.Clear();
            foreach (var pair in reservations)
                if (pair.Value.Options.OwnerId == ownerId)
                    reservationScratch.Add(pair.Key);
            return CancelCollected(ResourceChangeKind.Cancel);
        }

        public int ExpireReservations(long nowMilliseconds)
        {
            CheckMutation();
            if (nowMilliseconds < 0)
                throw new ArgumentOutOfRangeException(nameof(nowMilliseconds));
            reservationScratch.Clear();
            foreach (var pair in reservations)
                if (pair.Value.Options.HasExpiry && pair.Value.Options.ExpiresAtMilliseconds <= nowMilliseconds)
                    reservationScratch.Add(pair.Key);
            return CancelCollected(ResourceChangeKind.Expire);
        }

        private int CancelCollected(ResourceChangeKind kind)
        {
            if (reservationScratch.Count == 0)
                return 0;
            Scratch.Clear();
            foreach (var id in reservationScratch)
                scratch.Cancel(new ResourceReservation(this, id, generation));
            if (!scratch.TryExecute(out var failure, kind: kind))
                throw new InvalidOperationException(failure.ToString());
            return reservationScratch.Count;
        }

        internal bool Renew(ResourceReservation handle, long expiresAt, long now)
        {
            if (!TryGetSlot(handle, out var slot))
                return false;
            CheckMutation();
            if (expiresAt < -1 || now < 0 || (expiresAt >= 0 && expiresAt <= now))
                throw new ArgumentOutOfRangeException(nameof(expiresAt));
            if (revision == long.MaxValue)
                throw new InvalidOperationException("Revision range exhausted.");
            slot.Options = new ResourceReservationOptions(slot.Options.OwnerId, expiresAt, slot.Options.CreatedAtMilliseconds);
            RecordDeferredReservationChange(handle.Id);
            NotifyMarker(ResourceChangeKind.Reserve, new ResourceOperationContext("renew", slot.Options.OwnerId, now));
            return true;
        }

        internal bool Resize(ResourceReservation handle, ResourceCost remaining, out ResourceFailure failure)
        {
            if (!TryGetSlot(handle, out var slot))
            {
                failure = new ResourceFailure(ResourceFailureCode.ReservationNotActive);
                return false;
            }

            CheckMutation();
            var next = remaining.GetPrepared();
            // Validate every row before any release/increase. Existing held units remain available to this reservation.
            foreach (var row in next)
            {
                var balance = ReadBalance(row.Resource);
                var available = balance.Available + slot.GetAmount(row.Resource);
                if (row.Amount > available)
                {
                    failure = new ResourceFailure(ResourceFailureCode.InsufficientResource, row.Resource, this, row.Amount, available);
                    return false;
                }
            }

            if (revision == long.MaxValue)
                throw new InvalidOperationException("Revision range exhausted.");
            busy = true;
            try
            {
                var context = new ResourceOperationContext("resize", slot.Options.OwnerId);
                BeginPublication(ResourceChangeKind.Reserve, context);
                // Consolidate both costs into the reusable publication buffer, then publish once.
                foreach (var row in next)
                    EnsureEntry(row.Resource);
                for (var i = 0; i < slot.Count; i++)
                {
                    var row = slot.Items[i];
                    var before = balances[row.Resource];
                    var replacement = remaining.GetAmount(row.Resource);
                    var after = new ResourceBalance(row.Resource, before.Total, before.Reserved - row.Amount + replacement);
                    WriteBalance(after);
                    if (before.Reserved != after.Reserved)
                        Record(before, after, ResourceChangeKind.Reserve, context);
                }

                foreach (var row in next)
                    if (slot.GetAmount(row.Resource) == 0)
                    {
                        var before = balances[row.Resource];
                        var after = new ResourceBalance(row.Resource, before.Total, before.Reserved + row.Amount);
                        WriteBalance(after);
                        Record(before, after, ResourceChangeKind.Reserve, context);
                    }

                slot.Items = next;
                slot.Count = next.Length;
                RecordDeferredReservationChange(handle.Id);
                Publish();
                failure = default;
                return true;
            }
            finally
            {
                busy = false;
                FinishDeferredDispose();
            }
        }

        public bool TrySetLimits(ResourceLimits newLimits, out ResourceFailure failure)
        {
            CheckMutation();
            if (newLimits == null)
                throw new ArgumentNullException(nameof(newLimits));
            if (readOnly)
            {
                failure = new ResourceFailure(ResourceFailureCode.ReadOnly, store: this);
                return false;
            }

            if (!ValidateContents(balances, newLimits, out var nextWeight, out var nextVolume, out failure))
                return false;
            if (revision == long.MaxValue)
            {
                failure = new ResourceFailure(ResourceFailureCode.AmountOverflow);
                return false;
            }

            limits = newLimits;
            RecordDeferredLimitsChange();
            weight = nextWeight;
            volume = nextVolume;
            NotifyMarker(ResourceChangeKind.Limits, default);
            return true;
        }

        public void EnableHistory(int capacity)
        {
            CheckMutation();
            if (capacity < 0)
                throw new ArgumentOutOfRangeException(nameof(capacity));
            if (capacity <= history.Length)
                return;
            var replacement = new ResourceChange[capacity];
            for (var i = 0; i < historyCount; i++)
                replacement[i] = history[(historyStart + i) % history.Length];
            history = replacement;
            historyStart = 0;
        }

        public void CopyHistoryTo(List<ResourceChange> destination)
        {
            CheckAlive();
            if (destination == null)
                throw new ArgumentNullException(nameof(destination));
            destination.Clear();
            for (var i = 0; i < historyCount; i++)
                destination.Add(history[(historyStart + i) % history.Length]);
        }

        internal ResourceBalance ReadBalance(ResourceDefinition resource)
        {
            if (resource == null)
                throw new ArgumentNullException(nameof(resource), "Assign a Resource asset.");
            if (balances.TryGetValue(resource, out var value))
                return value;
            if (string.IsNullOrWhiteSpace(resource.Id))
                throw new ArgumentException("Resource ID is blank.");
            if (resource.DecimalPlaces < 0 || resource.DecimalPlaces > 9 || resource.WeightPerUnit < 0 || resource.VolumePerUnit < 0)
                throw new ArgumentException("Resource precision, weight or volume is invalid. Correct the Resource asset.");
            if (identities.TryGetValue(resource.Id, out var existing) && !ReferenceEquals(existing, resource))
                throw new ArgumentException($"Duplicate resource ID '{resource.Id}'.");
            return new ResourceBalance(resource, 0, 0);
        }

        internal void EnsureEntry(ResourceDefinition resource)
        {
            if (balances.ContainsKey(resource))
                return;
            ReadBalance(resource);
            identities.Add(resource.Id, resource);
            balances.Add(resource, new ResourceBalance(resource, 0, 0));
        }

        internal void WriteBalance(ResourceBalance value) => balances[value.Resource] = value;
        internal bool PrepareLine(ref ResourceTransaction.Line line, out ResourceFailure failure)
        {
            line.Before = ReadBalance(line.Resource);
            if (line.RequiredAvailable > line.Before.Available)
            {
                failure = new ResourceFailure(ResourceFailureCode.InsufficientResource, line.Resource, this, line.RequiredAvailable, line.Before.Available);
                return false;
            }

            long total, reserved;
            try
            {
                total = checked(line.Before.Total + line.TotalDelta);
                reserved = checked(line.Before.Reserved + line.ReservedDelta);
            }
            catch (OverflowException)
            {
                failure = new ResourceFailure(ResourceFailureCode.AmountOverflow, line.Resource, this);
                return false;
            }

            if (total < 0 || reserved < 0 || total < reserved)
            {
                failure = new ResourceFailure(ResourceFailureCode.InsufficientResource, line.Resource, this, line.TotalDelta == long.MinValue ? long.MaxValue : Math.Max(0, -line.TotalDelta), line.Before.Available);
                return false;
            }

            if (total > 0 && !limits.Accepts(line.Resource))
            {
                failure = new ResourceFailure(ResourceFailureCode.ResourceNotAccepted, line.Resource, this);
                return false;
            }

            var max = limits.GetMaximum(line.Resource);
            if (total > max)
            {
                failure = new ResourceFailure(ResourceFailureCode.ResourceCapacityExceeded, line.Resource, this, limit: max);
                return false;
            }

            line.After = new ResourceBalance(line.Resource, total, reserved);
            failure = default;
            return true;
        }

        internal bool PrepareUsage(List<ResourceTransaction.Line> lines, out long newWeight, out long newVolume, out ResourceFailure failure)
        {
            newWeight = weight;
            newVolume = volume;
            try
            {
                foreach (var line in lines)
                    if (ReferenceEquals(line.Store, this))
                    {
                        if (limits.MaximumWeight.HasValue)
                            newWeight -= checked(line.Before.Total * line.Resource.WeightPerUnit);
                        if (limits.MaximumVolume.HasValue)
                            newVolume -= checked(line.Before.Total * line.Resource.VolumePerUnit);
                    }

                foreach (var line in lines)
                    if (ReferenceEquals(line.Store, this))
                    {
                        if (limits.MaximumWeight.HasValue)
                            newWeight = checked(newWeight + checked(line.After.Total * line.Resource.WeightPerUnit));
                        if (limits.MaximumVolume.HasValue)
                            newVolume = checked(newVolume + checked(line.After.Total * line.Resource.VolumePerUnit));
                    }
            }
            catch (OverflowException)
            {
                failure = new ResourceFailure(ResourceFailureCode.AmountOverflow, store: this);
                return false;
            }

            if (limits.MaximumWeight.HasValue && newWeight > limits.MaximumWeight.Value)
            {
                failure = new ResourceFailure(ResourceFailureCode.WeightCapacityExceeded, store: this, limit: limits.MaximumWeight.Value);
                return false;
            }

            if (limits.MaximumVolume.HasValue && newVolume > limits.MaximumVolume.Value)
            {
                failure = new ResourceFailure(ResourceFailureCode.VolumeCapacityExceeded, store: this, limit: limits.MaximumVolume.Value);
                return false;
            }

            failure = default;
            return true;
        }

        private bool ValidateContents(Dictionary<ResourceDefinition, ResourceBalance> contents, ResourceLimits rules, out long totalWeight, out long totalVolume, out ResourceFailure failure)
        {
            totalWeight = 0;
            totalVolume = 0;
            foreach (var row in contents.Values)
            {
                if (row.Total > 0 && !rules.Accepts(row.Resource))
                {
                    failure = new ResourceFailure(ResourceFailureCode.ResourceNotAccepted, row.Resource, this);
                    return false;
                }

                var max = rules.GetMaximum(row.Resource);
                if (row.Total > max)
                {
                    failure = new ResourceFailure(ResourceFailureCode.ResourceCapacityExceeded, row.Resource, this, limit: max);
                    return false;
                }

                try
                {
                    if (rules.MaximumWeight.HasValue)
                        totalWeight = checked(totalWeight + checked(row.Total * row.Resource.WeightPerUnit));
                    if (rules.MaximumVolume.HasValue)
                        totalVolume = checked(totalVolume + checked(row.Total * row.Resource.VolumePerUnit));
                }
                catch (OverflowException)
                {
                    failure = new ResourceFailure(ResourceFailureCode.AmountOverflow, row.Resource, this);
                    return false;
                }
            }

            if (rules.MaximumWeight.HasValue && totalWeight > rules.MaximumWeight.Value)
            {
                failure = new ResourceFailure(ResourceFailureCode.WeightCapacityExceeded, store: this, limit: rules.MaximumWeight.Value);
                return false;
            }

            if (rules.MaximumVolume.HasValue && totalVolume > rules.MaximumVolume.Value)
            {
                failure = new ResourceFailure(ResourceFailureCode.VolumeCapacityExceeded, store: this, limit: rules.MaximumVolume.Value);
                return false;
            }

            failure = default;
            return true;
        }

        internal void SetUsage(long currentWeight, long currentVolume)
        {
            weight = currentWeight;
            volume = currentVolume;
        }

        internal void EnterTransaction() => busy = true;
        internal void ExitTransaction()
        {
            busy = false;
            publishingChanged = publishingDetailed = null;
        }

        internal void BeginPublication(ResourceChangeKind kind, ResourceOperationContext context)
        {
            publicationDeferred = HasDeferredNotifications;
            if (publicationDeferred)
            {
                publication.Clear();
                hasPublicationRecords = false;
                publishingChanged = publishingDetailed = Array.Empty<Delegate>();
                return;
            }
            revision = checked(revision + 1);
            LastChangeKind = kind;
            LastContext = context;
            publication.Clear();
            hasPublicationRecords = false;
            publishingChanged = changedListeners;
            publishingDetailed = detailedListeners;
        }

        internal void Record(ResourceBalance before, ResourceBalance after, ResourceChangeKind kind, ResourceOperationContext context)
        {
            if (publicationDeferred)
            {
                RecordDeferred(before, after);
                return;
            }
            hasPublicationRecords = true;
            if (publishingDetailed.Length == 0 && history.Length == 0)
            {
                droppedThroughRevision = revision;
                return;
            }

            var change = new ResourceChange(this, revision, kind, context, before, after);
            if (publishingDetailed.Length != 0)
                publication.Add(change);
            AppendHistory(change);
        }

        private void AppendHistory(ResourceChange change)
        {
            if (history.Length == 0)
            {
                droppedThroughRevision = change.Revision;
                return;
            }

            if (historyCount == history.Length)
            {
                droppedThroughRevision = history[historyStart].Revision;
                history[historyStart] = change;
                historyStart = (historyStart + 1) % history.Length;
            }
            else
            {
                history[(historyStart + historyCount) % history.Length] = change;
                historyCount++;
            }
        }

        internal void Publish()
        {
            try
            {
                if (publicationDeferred)
                    return;
                if (!hasPublicationRecords)
                    AppendHistory(new ResourceChange(this, revision, LastChangeKind, LastContext, default, default));
                foreach (var change in publication)
                    foreach (var listener in publishingDetailed)
                        try
                        {
                            ((Action<ResourceChange>)listener)(change);
                        }
                        catch (Exception e)
                        {
                            SafeSignal.Log(e);
                        }

                foreach (var listener in publishingChanged)
                    try
                    {
                        ((Action)listener)();
                    }
                    catch (Exception e)
                    {
                        SafeSignal.Log(e);
                    }
            }
            finally
            {
                publicationDeferred = false;
                publishingChanged = publishingDetailed = null;
            }
        }

        private void NotifyMarker(ResourceChangeKind kind, ResourceOperationContext context)
        {
            busy = true;
            try
            {
                BeginPublication(kind, context);
                Publish();
            }
            finally
            {
                busy = false;
                FinishDeferredDispose();
            }
        }

        private void CheckThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != ownerThread)
                throw new InvalidOperationException("Create and use ResourceStore on the Unity main thread.");
        }

        internal void CheckAlive()
        {
            CheckThread();
            if (disposed)
                throw new ObjectDisposedException(nameof(ResourceStore));
        }

        internal void CheckMutation()
        {
            CheckAlive();
            if (busy)
                throw new InvalidOperationException("Cannot change participating stores during a transaction notification. Queue the operation until the notification returns.");
        }

        internal void RequestDispose()
        {
            CheckThread();
            if (disposed)
                return;
            if (busy)
                disposeRequested = true;
            else
                Dispose();
        }

        internal void FinishDeferredDispose()
        {
            if (disposeRequested && !disposed)
                Dispose();
            else if (!disposed)
                FlushDeferredNotifications();
        }

        public void Dispose()
        {
            CheckThread();
            if (disposed)
                return;
            CheckMutation();
            disposed = true;
            InvalidateDeferredNotifications();
            balances = null;
            identities = null;
            reservations = null;
            freeSlots.Clear();
            changed = null;
            detailedChanged = null;
            changedListeners = detailedListeners = Array.Empty<Delegate>();
            publishingChanged = publishingDetailed = null;
            publication.Clear();
            history = Array.Empty<ResourceChange>();
            scratch?.RequestClear();
            reservationScratch.Clear();
        }
    }
}
