using System;
using System.Collections.Generic;

namespace EasyResources
{
    public sealed partial class ResourceStore
    {
        internal void MakeReplica()
        {
            CheckMutation();
            if (reservations.Count != 0 || balances.Count != 0)
                throw new InvalidOperationException("Create an empty replica store.");
            readOnly = true;
        }

        internal void ApplyReplica(List<ResourceBalance> rows, long incomingRevision, int reservationCount, bool full)
        {
            CheckMutation();
            if (!readOnly)
                throw new InvalidOperationException("Replication applies only to read-only stores.");
            if (rows == null || incomingRevision < 0 || reservationCount < 0)
                throw new ArgumentException("Malformed replica state.");
            var next = full ? new Dictionary<ResourceDefinition, ResourceBalance>(ReferenceComparer<ResourceDefinition>.Instance) : new Dictionary<ResourceDefinition, ResourceBalance>(balances, ReferenceComparer<ResourceDefinition>.Instance);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                if (row.Resource == null || row.Total < 0 || row.Reserved < 0 || row.Reserved > row.Total || !seen.Add(row.Resource.Id))
                    throw new ArgumentException("Malformed replica balance.");
                ReadBalance(row.Resource);
                next[row.Resource] = row;
            }

            if (!ValidateContents(next, limits, out var nextWeight, out var nextVolume, out var failure))
                throw new ArgumentException(failure.ToString());
            var nextIdentities = new Dictionary<string, ResourceDefinition>(StringComparer.Ordinal);
            foreach (var row in next)
                nextIdentities.Add(row.Key.Id, row.Key);
            if (reservationCount == 0)
                foreach (var row in next.Values)
                    if (row.Reserved != 0)
                        throw new ArgumentException("Reserved balances require an active reservation count.");
            busy = true;
            try
            {
                var previous = balances;
                balances = next;
                identities = nextIdentities;
                weight = nextWeight;
                volume = nextVolume;
                replicaReservations = reservationCount;
                // A full initial snapshot may legitimately have revision zero.
                revision = incomingRevision;
                LastChangeKind = ResourceChangeKind.Replica;
                LastContext = default;
                publication.Clear();
                hasPublicationRecords = false;
                publishingChanged = changedListeners;
                publishingDetailed = detailedListeners;
                foreach (var old in previous.Values)
                {
                    var value = next.TryGetValue(old.Resource, out var current) ? current : new ResourceBalance(old.Resource, 0, 0);
                    if (old.Total != value.Total || old.Reserved != value.Reserved)
                        Record(old, value, ResourceChangeKind.Replica, default);
                }

                foreach (var value in next.Values)
                    if (!previous.ContainsKey(value.Resource))
                        Record(new ResourceBalance(value.Resource, 0, 0), value, ResourceChangeKind.Replica, default);
                Publish();
            }
            finally
            {
                busy = false;
                FinishDeferredDispose();
            }
        }
    }
}
