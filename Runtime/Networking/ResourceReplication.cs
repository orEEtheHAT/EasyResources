using System;
using System.Collections.Generic;

namespace EasyResources.Networking
{
    [Serializable]
    public sealed class ResourcePacket
    {
        public int Version = 1;
        public string StreamId;
        public bool IsFull;
        public long BaseRevision, Revision;
        public int ReservationCount;
        public List<ReplicatedBalance> Resources = new List<ReplicatedBalance>();
    }

    [Serializable]
    public struct ReplicatedBalance
    {
        public string ResourceId;
        public int DecimalPlaces;
        public long Total, Reserved;
        public ReplicatedBalance(ResourceBalance row)
        {
            ResourceId = row.Resource.Id;
            DecimalPlaces = row.Resource.DecimalPlaces;
            Total = row.Total;
            Reserved = row.Reserved;
        }
    }

    /// <summary>Transport-neutral full/delta producer. Retained history is bounded; gaps fall back to a full snapshot.</summary>
    public sealed class ResourceReplicator
    {
        private readonly ResourceStore store;
        private readonly List<ResourceChange> history = new List<ResourceChange>();
        private readonly List<ResourceBalance> balances = new List<ResourceBalance>();
        public string StreamId { get; } = Guid.NewGuid().ToString("N");

        public ResourceReplicator(ResourceStore authoritativeStore, int retainedChanges = 256)
        {
            store = authoritativeStore ?? throw new ArgumentNullException(nameof(authoritativeStore));
            if (store.IsReadOnly)
                throw new ArgumentException("Replicate from the authority.");
            if (retainedChanges < 1)
                throw new ArgumentOutOfRangeException(nameof(retainedChanges));
            store.EnableHistory(retainedChanges);
        }

        public ResourcePacket Capture(string knownStreamId = null, long afterRevision = -1)
        {
            store.CheckSnapshotBoundary();
            if (afterRevision < -1)
                throw new ArgumentOutOfRangeException(nameof(afterRevision));
            var packet = new ResourcePacket
            {
                StreamId = StreamId,
                BaseRevision = afterRevision,
                Revision = store.Revision,
                ReservationCount = store.ActiveReservationCount
            };
            packet.IsFull = knownStreamId != StreamId || afterRevision < store.DroppedHistoryThroughRevision || afterRevision > store.Revision || afterRevision < 0;
            if (packet.IsFull)
            {
                store.CopyBalancesTo(balances);
                foreach (var row in balances)
                    packet.Resources.Add(new ReplicatedBalance(row));
            }
            else
            {
                store.CopyHistoryTo(history);
                var latest = new Dictionary<string, ReplicatedBalance>(StringComparer.Ordinal);
                foreach (var change in history)
                    if (change.Revision > afterRevision && change.Resource != null)
                        latest[change.Resource.Id] = new ReplicatedBalance(change.After);
                foreach (var row in latest.Values)
                    packet.Resources.Add(row);
            }

            return packet;
        }
    }

    /// <summary>Read-only ResourceStore for UI and prediction display. Only apply packets received from your authenticated server.</summary>
    public sealed class ResourceReplica : IDisposable
    {
        private readonly DefinitionLookup<ResourceDefinition> definitions;
        private readonly List<ResourceBalance> rows = new List<ResourceBalance>();
        private bool applying, initialized;
        public ResourceStore Store { get; }
        public string StreamId { get; private set; }

        public ResourceReplica(IEnumerable<ResourceDefinition> definitions)
        {
            this.definitions = new DefinitionLookup<ResourceDefinition>(definitions);
            Store = new ResourceStore();
            Store.MakeReplica();
        }

        public bool TryApply(ResourcePacket packet, out ResourceFailure failure)
        {
            if (applying)
                throw new InvalidOperationException("Cannot apply another packet from a replica notification.");
            if (packet == null || packet.Version != 1 || string.IsNullOrWhiteSpace(packet.StreamId) || packet.Resources == null || packet.Revision < 0 || packet.ReservationCount < 0)
            {
                failure = new ResourceFailure(ResourceFailureCode.InvalidCommand);
                return false;
            }

            if (initialized && packet.StreamId != StreamId)
            {
                failure = new ResourceFailure(ResourceFailureCode.StaleRevision, detail: "Create a new replica when connecting to a different stream.");
                return false;
            }

            if ((!initialized && !packet.IsFull) || (!packet.IsFull && packet.BaseRevision != Store.Revision) || (initialized && packet.Revision < Store.Revision) || (!packet.IsFull && packet.Revision < packet.BaseRevision))
            {
                failure = new ResourceFailure(ResourceFailureCode.StaleRevision);
                return false;
            }

            rows.Clear();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in packet.Resources)
            {
                if (string.IsNullOrWhiteSpace(row.ResourceId) || !seen.Add(row.ResourceId) || row.Total < 0 || row.Reserved < 0 || row.Reserved > row.Total || !definitions.TryGet(row.ResourceId, out var definition) || definition.DecimalPlaces != row.DecimalPlaces)
                {
                    failure = new ResourceFailure(ResourceFailureCode.InvalidCommand);
                    return false;
                }

                rows.Add(new ResourceBalance(definition, row.Total, row.Reserved));
            }

            if (initialized && packet.Revision == Store.Revision)
            {
                failure = default;
                return true;
            }

            applying = true;
            var previousStream = StreamId;
            var previousInitialized = initialized;
            try
            {
                StreamId = packet.StreamId;
                initialized = true;
                Store.ApplyReplica(rows, packet.Revision, packet.ReservationCount, packet.IsFull);
                failure = default;
                return true;
            }
            catch (ArgumentException exception)
            {
                StreamId = previousStream;
                initialized = previousInitialized;
                failure = new ResourceFailure(ResourceFailureCode.InvalidCommand, detail: exception.Message);
                return false;
            }
            finally
            {
                applying = false;
            }
        }

        public void Dispose() => Store.Dispose();
    }
}
