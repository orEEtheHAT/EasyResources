using System;
using System.Collections.Generic;

namespace EasyResources.Production
{
    /// <summary>A shipment holds stock at its origin until arrival. Destination capacity is checked atomically on arrival.</summary>
    public sealed class ResourceDelivery : IResourceParticipant, IDisposable
    {
        private readonly ResourceStore source, destination;
        private readonly ResourceCost cargo;
        private readonly ResourceTransaction transaction = new ResourceTransaction();
        private ResourceReservation payment;
        private bool busy, completed;
        private readonly List<ResourceAmount> held = new List<ResourceAmount>();
        public string OwnerId { get; }
        public long ArrivalAtMilliseconds { get; }
        public bool IsActive => payment.IsActive;
        public bool IsCompleted => completed;

        private ResourceDelivery(ResourceStore source, ResourceStore destination, ResourceCost cargo, string ownerId, long arrival)
        {
            this.source = source;
            this.destination = destination;
            this.cargo = cargo;
            OwnerId = ownerId;
            ArrivalAtMilliseconds = arrival;
        }

        public static bool TryStart(ResourceStore source, ResourceStore destination, ResourceCost cargo, string ownerId, long nowMilliseconds, long travelMilliseconds, out ResourceDelivery delivery, out ResourceFailure failure)
        {
            if (source == null || destination == null || cargo == null || string.IsNullOrWhiteSpace(ownerId) || nowMilliseconds < 0 || travelMilliseconds < 0)
                throw new ArgumentException("Supply both stores, cargo, owner and nonnegative times.");
            delivery = new ResourceDelivery(source, destination, new ResourceCost(cargo.GetPrepared()), ownerId, checked(nowMilliseconds + travelMilliseconds));
            delivery.busy = true;
            try
            {
                if (source.TryReserve(delivery.cargo, out delivery.payment, out failure, new ResourceReservationOptions(ownerId, nowMilliseconds: nowMilliseconds)))
                    return true;
                delivery = null;
                return false;
            }
            finally
            {
                if (delivery != null)
                    delivery.busy = false;
            }
        }

        public bool TryArrive(long nowMilliseconds, out ResourceFailure failure)
        {
            if (busy)
                throw new InvalidOperationException("Cannot reenter a shipment notification.");
            if (nowMilliseconds < ArrivalAtMilliseconds)
            {
                failure = new ResourceFailure(ResourceFailureCode.RuleRejected, detail: "The shipment has not arrived yet.");
                return false;
            }

            if (!IsActive)
            {
                failure = new ResourceFailure(ResourceFailureCode.ReservationNotActive);
                return false;
            }

            return transaction.Clear().Commit(payment).Add(destination, cargo).Join(this).TryExecute(out failure, new ResourceOperationContext("delivery", OwnerId, nowMilliseconds), ResourceChangeKind.Transfer);
        }

        public bool Cancel()
        {
            if (busy)
                throw new InvalidOperationException("Cancel after the shipment notification returns.");
            return payment.Cancel();
        }

        public void Dispose() => Cancel();
        void IResourceParticipant.Enter()
        {
            if (busy)
                throw new InvalidOperationException("Shipment is busy.");
            busy = true;
        }

        bool IResourceParticipant.Prepare(out ResourceFailure failure)
        {
            payment.CopyRemainingTo(held);
            if (held.Count != cargo.GetPrepared().Length)
            {
                failure = new ResourceFailure(ResourceFailureCode.RuleRejected, detail: "Shipment reservation was changed externally.");
                return false;
            }

            foreach (var row in held)
                if (cargo.GetAmount(row.Resource) != row.Amount)
                {
                    failure = new ResourceFailure(ResourceFailureCode.RuleRejected, detail: "Shipment reservation was changed externally.");
                    return false;
                }

            failure = default;
            return true;
        }

        void IResourceParticipant.Apply() => completed = true;
        void IResourceParticipant.Publish()
        {
        }

        void IResourceParticipant.Exit() => busy = false;
        void IResourceParticipant.Finish()
        {
        }

        public ResourceDeliveryState CaptureState(long nowMilliseconds)
        {
            if (nowMilliseconds < 0)
                throw new ArgumentOutOfRangeException(nameof(nowMilliseconds));
            if (!IsActive)
                throw new InvalidOperationException("Only active deliveries need a saved operation.");
            return new ResourceDeliveryState
            {
                OwnerId = OwnerId,
                ReservationId = payment.Id,
                RemainingMilliseconds = Math.Max(0, ArrivalAtMilliseconds - nowMilliseconds),
                Cargo = SnapshotRows.Capture(cargo.GetPrepared())
            };
        }

        public static ResourceDelivery FromState(ResourceStore source, ResourceStore destination, ResourceDeliveryState state, IEnumerable<ResourceDefinition> definitions, long nowMilliseconds, long offlineMilliseconds = 0)
        {
            if (source == null || destination == null || state == null || state.Version != 1 || string.IsNullOrWhiteSpace(state.OwnerId) || state.RemainingMilliseconds < 0 || nowMilliseconds < 0 || offlineMilliseconds < 0)
                throw new ArgumentException("Invalid shipment snapshot.");
            var cargo = SnapshotRows.Restore(state.Cargo, new DefinitionLookup<ResourceDefinition>(definitions));
            if (!source.TryGetReservation(state.ReservationId, out var handle) || !source.TryGetSlot(handle, out var slot) || slot.Options.OwnerId != state.OwnerId || slot.Count != cargo.GetPrepared().Length)
                throw new ArgumentException("Restore this shipment's named reservation first.");
            foreach (var row in cargo.GetPrepared())
                if (slot.GetAmount(row.Resource) != row.Amount)
                    throw new ArgumentException("Shipment cargo does not match its reservation.");
            var result = new ResourceDelivery(source, destination, cargo, state.OwnerId, checked(nowMilliseconds + Math.Max(0, state.RemainingMilliseconds - offlineMilliseconds)));
            result.payment = handle;
            return result;
        }
    }

    [Serializable]
    public sealed class ResourceDeliveryState
    {
        public int Version = 1;
        public string OwnerId;
        public long ReservationId;
        public long RemainingMilliseconds;
        public List<ResourceStateEntry> Cargo = new List<ResourceStateEntry>();
    }
}
