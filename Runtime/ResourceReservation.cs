using System;
using System.Collections.Generic;

namespace EasyResources
{
    public readonly struct ResourceReservationOptions
    {
        public string OwnerId { get; }
        public long CreatedAtMilliseconds { get; }
        public long ExpiresAtMilliseconds { get; }
        public bool HasExpiry { get; }

        public ResourceReservationOptions(string ownerId, long expiresAtMilliseconds = -1, long nowMilliseconds = 0)
        {
            if (expiresAtMilliseconds < -1 || nowMilliseconds < 0)
                throw new ArgumentOutOfRangeException("Time must be nonnegative; -1 means no deadline.");
            OwnerId = ownerId;
            CreatedAtMilliseconds = nowMilliseconds;
            HasExpiry = expiresAtMilliseconds >= 0;
            ExpiresAtMilliseconds = expiresAtMilliseconds;
        }
    }

    public readonly struct ResourceReservationInfo
    {
        public ResourceReservation Reservation { get; }
        public string OwnerId { get; }
        public long CreatedAtMilliseconds { get; }
        public long ExpiresAtMilliseconds { get; }
        public bool HasExpiry { get; }
        public int ResourceCount { get; }

        internal ResourceReservationInfo(ResourceReservation handle, ResourceStore.ReservationSlot slot)
        {
            Reservation = handle;
            OwnerId = slot.Options.OwnerId;
            CreatedAtMilliseconds = slot.Options.CreatedAtMilliseconds;
            ExpiresAtMilliseconds = slot.Options.ExpiresAtMilliseconds;
            HasExpiry = slot.Options.HasExpiry;
            ResourceCount = slot.Count;
        }
    }

    public readonly struct ResourceReservation : IDisposable, IEquatable<ResourceReservation>
    {
        internal ResourceStore Owner { get; }
        public long Id { get; }
        internal long Generation { get; }

        internal ResourceReservation(ResourceStore owner, long id, long generation)
        {
            Owner = owner;
            Id = id;
            Generation = generation;
        }

        public bool IsActive => Owner != null && Owner.TryGetSlot(this, out _);

        public bool Commit() => Owner != null && Owner.Complete(this, null, false, ResourceChangeKind.Commit, out _);
        /// <summary>Consume the complete remaining reservation, returning a typed failure.</summary>
        public bool TryCommit(out ResourceFailure failure)
        {
            if (Owner != null)
                return Owner.Complete(this, null, false, ResourceChangeKind.Commit, out failure);
            failure = new ResourceFailure(ResourceFailureCode.ReservationNotActive);
            return false;
        }
        public bool TryCommit(ResourceCost part, out ResourceFailure failure)
        {
            if (part == null)
                throw new ArgumentNullException(nameof(part));
            if (Owner != null)
                return Owner.Complete(this, part, false, ResourceChangeKind.Commit, out failure);
            failure = new ResourceFailure(ResourceFailureCode.ReservationNotActive);
            return false;
        }

        public bool Cancel() => Owner != null && Owner.Complete(this, null, true, ResourceChangeKind.Cancel, out _);
        /// <summary>Replace the remaining held cost atomically, without spending it.</summary>
        public bool TryResize(ResourceCost remaining, out ResourceFailure failure)
        {
            if (remaining == null)
                throw new ArgumentNullException(nameof(remaining));
            if (Owner != null)
                return Owner.Resize(this, remaining, out failure);
            failure = new ResourceFailure(ResourceFailureCode.ReservationNotActive);
            return false;
        }

        public bool Renew(long expiresAtMilliseconds, long nowMilliseconds = 0) => Owner != null && Owner.Renew(this, expiresAtMilliseconds, nowMilliseconds);
        public void CopyRemainingTo(List<ResourceAmount> destination)
        {
            if (destination == null)
                throw new ArgumentNullException(nameof(destination));
            destination.Clear();
            if (Owner == null || !Owner.TryGetSlot(this, out var slot))
                return;
            for (var i = 0; i < slot.Count; i++)
                destination.Add(slot.Items[i]);
        }

        public void Dispose() => Cancel();
        public bool Equals(ResourceReservation other) => ReferenceEquals(Owner, other.Owner) && Id == other.Id && Generation == other.Generation;
        public override bool Equals(object obj) => obj is ResourceReservation other && Equals(other);
        public override int GetHashCode() => unchecked((Owner == null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Owner)) * 397 ^ Id.GetHashCode() * 31 ^ Generation.GetHashCode());
    }

    /// <summary>Optional lifetime owner. Dispose cancels all remaining holds belonging to this owner.</summary>
    public sealed class ResourceReservationScope : IDisposable
    {
        private ResourceStore store;
        private readonly long generation;
        public string OwnerId { get; }

        public ResourceReservationScope(ResourceStore store, string ownerId)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            if (string.IsNullOrWhiteSpace(ownerId))
                throw new ArgumentException("Choose a persistent operation/owner ID.", nameof(ownerId));
            OwnerId = ownerId;
            generation = store.Generation;
        }

        public bool TryReserve(ResourceCost cost, out ResourceReservation reservation, long expiresAtMilliseconds = -1, long nowMilliseconds = 0)
        {
            if (store == null)
                throw new ObjectDisposedException(nameof(ResourceReservationScope));
            if (store.Generation != generation)
                throw new InvalidOperationException("Create a new scope after restoring the store.");
            return store.TryReserve(cost, out reservation, out _, new ResourceReservationOptions(OwnerId, expiresAtMilliseconds, nowMilliseconds));
        }

        public void Dispose()
        {
            var current = store;
            if (current == null)
                return;
            try
            {
                if (current.Generation == generation)
                    current.CancelAll(OwnerId);
            }
            catch (ObjectDisposedException)
            {
            }

            store = null;
        }
    }
}
