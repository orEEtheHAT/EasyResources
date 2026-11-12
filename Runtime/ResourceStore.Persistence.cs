using System;
using System.Collections.Generic;

namespace EasyResources
{
    public sealed partial class ResourceStore
    {
        internal sealed class PreparedState
        {
            internal Dictionary<ResourceDefinition, ResourceBalance> Balances;
            internal Dictionary<string, ResourceDefinition> Identities;
            internal Dictionary<long, ReservationSlot> Reservations;
            internal long Weight, Volume, NextId, Revision, Generation;
            internal int Ignored;
        }

        public ResourceContainerState CaptureState(bool includeReservations = false, long nowMilliseconds = 0)
        {
            CheckAlive();
            CheckSnapshotBoundary();
            if (readOnly)
                throw new InvalidOperationException("Capture authoritative state on the server, not a replica.");
            if (nowMilliseconds < 0)
                throw new ArgumentOutOfRangeException(nameof(nowMilliseconds));
            if (!includeReservations && reservations.Count != 0)
                throw new InvalidOperationException("Commit/cancel reservations, or explicitly capture named active operations.");
            var state = new ResourceContainerState
            {
                Revision = revision
            };
            foreach (var value in balances.Values)
                if (value.Total != 0)
                    state.Resources.Add(new ResourceStateEntry(value.Resource.Id, value.Total, value.Resource.DecimalPlaces));
            state.Resources.Sort((a, b) => StringComparer.Ordinal.Compare(a.ResourceId, b.ResourceId));
            if (includeReservations)
                foreach (var pair in reservations)
                {
                    var slot = pair.Value;
                    if (nowMilliseconds < slot.Options.CreatedAtMilliseconds)
                        throw new ArgumentException("Capture time precedes the reservation creation time.");
                    if (string.IsNullOrWhiteSpace(slot.Options.OwnerId))
                        throw new InvalidOperationException("Give every saved reservation a persistent OwnerId so its operation can reconnect after loading.");
                    var saved = new ResourceReservationState
                    {
                        Id = pair.Key,
                        OwnerId = slot.Options.OwnerId,
                        AgeMilliseconds = checked(slot.AgeOffset + nowMilliseconds - slot.Options.CreatedAtMilliseconds),
                        HasExpiry = slot.Options.HasExpiry,
                        RemainingMilliseconds = slot.Options.HasExpiry ? Math.Max(0, slot.Options.ExpiresAtMilliseconds - nowMilliseconds) : 0
                    };
                    for (var i = 0; i < slot.Count; i++)
                        saved.Resources.Add(new ResourceStateEntry(slot.Items[i].Resource.Id, slot.Items[i].Amount, slot.Items[i].Resource.DecimalPlaces));
                    state.Reservations.Add(saved);
                }

            state.Reservations.Sort((a, b) => a.Id.CompareTo(b.Id));
            return state;
        }

        public int RestoreState(ResourceContainerState state, IEnumerable<ResourceDefinition> definitions, UnknownResourcePolicy unknownResourcePolicy = UnknownResourcePolicy.Reject) => RestoreState(state, definitions, new ResourceRestoreOptions(unknownResourcePolicy)).IgnoredResourceCount;
        public ResourceRestoreReport RestoreState(ResourceContainerState state, IEnumerable<ResourceDefinition> definitions, ResourceRestoreOptions options)
        {
            CheckMutation();
            var prepared = PrepareState(state, new DefinitionLookup<ResourceDefinition>(definitions), options);
            busy = true;
            try
            {
                ApplyState(prepared);
                Publish();
            }
            finally
            {
                busy = false;
                FinishDeferredDispose();
            }

            return new ResourceRestoreReport(prepared.Ignored, prepared.Reservations.Count);
        }

        internal PreparedState PrepareState(ResourceContainerState state, DefinitionLookup<ResourceDefinition> catalog, ResourceRestoreOptions options)
        {
            CheckMutation();
            if (readOnly)
                throw new InvalidOperationException("Cannot restore authoritative state into a replica.");
            if ((reservations.Count != 0 || HasDeferredNotifications) && !options.ReplaceActiveReservations)
                throw new InvalidOperationException("Finish active reservations or explicitly replace them when restoring.");
            if (state == null)
                throw new ArgumentNullException(nameof(state));
            if (state.Version != 1 && state.Version != ResourceContainerState.CurrentVersion)
                throw new ArgumentException($"Unsupported resource state version {state.Version}.");
            if (state.Resources == null || (state.Version == 2 && state.Reservations == null))
                throw new ArgumentException("Saved resource lists cannot be null.");
            if (state.Version == 1 && state.Reservations != null && state.Reservations.Count != 0)
                throw new ArgumentException("Version 1 cannot contain saved reservations.");
            if (state.Revision < 0 || (options.UnknownResources != UnknownResourcePolicy.Reject && options.UnknownResources != UnknownResourcePolicy.Ignore))
                throw new ArgumentException("Invalid restore options or revision.");
            if (state.Reservations != null && state.Reservations.Count > 0 && !options.ResumeReservations)
                throw new InvalidOperationException("Explicitly enable ResumeReservations to restore active operations.");
            var result = new PreparedState
            {
                Balances = new Dictionary<ResourceDefinition, ResourceBalance>(ReferenceComparer<ResourceDefinition>.Instance),
                Identities = new Dictionary<string, ResourceDefinition>(StringComparer.Ordinal),
                Reservations = new Dictionary<long, ReservationSlot>(),
                NextId = nextReservationId,
                Revision = checked(Math.Max(revision, state.Revision) + 1),
                Generation = checked(generation + 1)
            };
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in state.Resources)
            {
                ValidateSavedRow(row, ids);
                if (!catalog.TryGet(row.ResourceId, out var resource))
                {
                    if (options.UnknownResources == UnknownResourcePolicy.Reject)
                        throw new ArgumentException($"Unknown resource ID '{row.ResourceId}'.");
                    result.Ignored++;
                    continue;
                }

                CheckPrecision(row, resource);
                result.Balances.Add(resource, new ResourceBalance(resource, row.Amount, 0));
                result.Identities.Add(resource.Id, resource);
            }

            if (state.Reservations != null)
                foreach (var saved in state.Reservations)
                {
                    if (saved == null || saved.Id <= 0 || string.IsNullOrWhiteSpace(saved.OwnerId) || saved.Resources == null || saved.AgeMilliseconds < 0 || saved.RemainingMilliseconds < 0)
                        throw new ArgumentException("Malformed saved reservation.");
                    if (result.Reservations.ContainsKey(saved.Id))
                        throw new ArgumentException("Duplicate saved reservation ID.");
                    var rows = new ResourceAmount[saved.Resources.Count];
                    ids.Clear();
                    for (var i = 0; i < rows.Length; i++)
                    {
                        var row = saved.Resources[i];
                        ValidateSavedRow(row, ids);
                        // Never silently remove part of a held payment, even with Ignore for ordinary balances.
                        var resource = catalog.Get(row.ResourceId);
                        CheckPrecision(row, resource);
                        if (!result.Balances.TryGetValue(resource, out var balance))
                            balance = new ResourceBalance(resource, 0, 0);
                        var held = checked(balance.Reserved + row.Amount);
                        if (held > balance.Total)
                            throw new ArgumentException("Saved reservations exceed the saved total.");
                        result.Balances[resource] = new ResourceBalance(resource, balance.Total, held);
                        result.Identities[resource.Id] = resource;
                        rows[i] = new ResourceAmount(resource, row.Amount);
                    }

                    var slot = new ReservationSlot
                    {
                        Items = rows,
                        Count = rows.Length,
                        AgeOffset = saved.AgeMilliseconds,
                        Options = new ResourceReservationOptions(saved.OwnerId, saved.HasExpiry ? checked(options.NowMilliseconds + saved.RemainingMilliseconds) : -1, options.NowMilliseconds)
                    };
                    result.Reservations.Add(saved.Id, slot);
                    result.NextId = Math.Max(result.NextId, saved.Id);
                }

            if (!ValidateContents(result.Balances, limits, out result.Weight, out result.Volume, out var failure))
                throw new ArgumentException("Saved contents: " + failure);
            return result;
        }

        internal void ApplyState(PreparedState state)
        {
            InvalidateDeferredNotifications();
            var previous = balances;
            balances = state.Balances;
            identities = state.Identities;
            reservations = state.Reservations;
            nextReservationId = state.NextId;
            generation = state.Generation;
            weight = state.Weight;
            volume = state.Volume;
            revision = state.Revision - 1;
            BeginPublication(ResourceChangeKind.Restore, default);
            foreach (var old in previous.Values)
            {
                balances.TryGetValue(old.Resource, out var value);
                if (value.Resource == null)
                    value = new ResourceBalance(old.Resource, 0, 0);
                if (old.Total != value.Total || old.Reserved != value.Reserved)
                    Record(old, value, ResourceChangeKind.Restore, default);
            }

            foreach (var value in balances.Values)
                if (!previous.ContainsKey(value.Resource))
                    Record(new ResourceBalance(value.Resource, 0, 0), value, ResourceChangeKind.Restore, default);
        }

        private static void ValidateSavedRow(ResourceStateEntry row, HashSet<string> ids)
        {
            if (string.IsNullOrWhiteSpace(row.ResourceId) || row.Amount < 0 || row.DecimalPlaces < 0 || row.DecimalPlaces > 9)
                throw new ArgumentException("Saved rows require an ID, a nonnegative amount and valid precision.");
            if (!ids.Add(row.ResourceId))
                throw new ArgumentException($"Duplicate saved resource ID '{row.ResourceId}'.");
        }

        private static void CheckPrecision(ResourceStateEntry row, ResourceDefinition resource)
        {
            if (row.DecimalPlaces != resource.DecimalPlaces)
                throw new ArgumentException($"Unit precision changed for '{resource.Id}'. Migrate the snapshot before restoring.");
        }
    }

    /// <summary>Atomic restoration of several independent stores, including their resumed reservations.</summary>
    public static class ResourceSnapshots
    {
        public static void RestoreAll(ResourceStore[] stores, ResourceContainerState[] states, IEnumerable<ResourceDefinition> definitions, ResourceRestoreOptions options = default)
        {
            if (stores == null || states == null || stores.Length != states.Length)
                throw new ArgumentException("Supply one state per store.");
            var catalog = new DefinitionLookup<ResourceDefinition>(definitions);
            var prepared = new ResourceStore.PreparedState[stores.Length];
            for (var i = 0; i < stores.Length; i++)
            {
                if (stores[i] == null)
                    throw new ArgumentException("Store is null.");
                for (var j = 0; j < i; j++)
                    if (ReferenceEquals(stores[i], stores[j]))
                        throw new ArgumentException("Store is listed twice.");
                prepared[i] = stores[i].PrepareState(states[i], catalog, options);
            }

            foreach (var store in stores)
                store.EnterTransaction();
            try
            {
                for (var i = 0; i < stores.Length; i++)
                    stores[i].ApplyState(prepared[i]);
                foreach (var store in stores)
                    store.Publish();
            }
            finally
            {
                foreach (var store in stores)
                    store.ExitTransaction();
                foreach (var store in stores)
                    store.FinishDeferredDispose();
            }
        }
    }
}
