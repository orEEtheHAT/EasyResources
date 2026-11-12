using System;
using System.Collections.Generic;

namespace EasyResources
{
    public sealed partial class ResourceStore
    {
        private sealed class DeferredNotifications
        {
            internal struct Row
            {
                internal ResourceBalance Before, After;
            }

            internal readonly HashSet<long> Scopes = new HashSet<long>();
            internal readonly Dictionary<ResourceDefinition, int> Indices =
                new Dictionary<ResourceDefinition, int>(ReferenceComparer<ResourceDefinition>.Instance);
            internal readonly List<Row> Rows = new List<Row>();
            internal ResourceOperationContext Context;
            internal long InitialReservationId;
            internal int InitialReservationCount;
            internal bool Pending, Marker;

            internal void Clear()
            {
                Scopes.Clear();
                Indices.Clear();
                Rows.Clear();
                Context = default;
                Pending = Marker = false;
            }
        }

        private DeferredNotifications deferredNotifications;
        private long nextNotificationScopeId;
        private bool publicationDeferred;

        /// <summary>
        /// Coalesce this store's notifications until all nested scopes close. Balances/holds change immediately.
        /// This is not rollback: cancel each rejected reservation explicitly. Use only synchronously on this thread.
        /// </summary>
        public ResourceNotificationScope DeferNotifications(ResourceOperationContext context = default)
        {
            CheckMutation();
            if (readOnly)
                throw new InvalidOperationException("Defer notifications on the authoritative store, not a replica.");
            if (nextNotificationScopeId == long.MaxValue)
                throw new InvalidOperationException("Notification scope identifiers exhausted.");
            var state = deferredNotifications ?? (deferredNotifications = new DeferredNotifications());
            var id = checked(nextNotificationScopeId + 1);
            state.Scopes.Add(id);
            nextNotificationScopeId = id;
            if (!state.Pending)
            {
                state.Context = context;
                state.InitialReservationId = nextReservationId;
                state.InitialReservationCount = reservations.Count;
                state.Pending = true;
            }
            return new ResourceNotificationScope(this, id, generation);
        }

        internal bool IsNotificationScopeActive(long id, long scopeGeneration)
        {
            CheckThread();
            return !disposed && scopeGeneration == generation &&
                deferredNotifications != null && deferredNotifications.Scopes.Contains(id);
        }

        internal void CloseNotificationScope(long id, long scopeGeneration)
        {
            CheckThread();
            if (disposed || scopeGeneration != generation || deferredNotifications == null ||
                !deferredNotifications.Scopes.Remove(id))
                return;
            // If this store participates in another transaction's callback, that transaction flushes
            // after its entire original publication, while all participants are still protected.
            if (!busy)
                FlushDeferredNotifications();
        }

        internal void CheckSnapshotBoundary()
        {
            CheckAlive();
            if (deferredNotifications != null && deferredNotifications.Pending)
                throw new InvalidOperationException("Complete all deferred notification scopes before capturing a snapshot or replication packet. Save at the completed game-command boundary.");
        }

        private bool HasDeferredNotifications => deferredNotifications != null && deferredNotifications.Pending;

        private void RecordDeferred(ResourceBalance before, ResourceBalance after)
        {
            var state = deferredNotifications;
            if (state.Indices.TryGetValue(after.Resource, out var index))
            {
                var row = state.Rows[index];
                row.After = after;
                state.Rows[index] = row;
            }
            else
            {
                state.Indices.Add(after.Resource, state.Rows.Count);
                state.Rows.Add(new DeferredNotifications.Row { Before = before, After = after });
            }
        }

        private void RecordDeferredReservationChange(long reservationId)
        {
            // New holds that are also completed/cancelled inside this boundary are transient.
            // Changing/removing an older handle is observable even when balances and hold count match.
            if (HasDeferredNotifications && reservationId <= deferredNotifications.InitialReservationId)
                deferredNotifications.Marker = true;
        }

        private void RecordDeferredLimitsChange()
        {
            if (HasDeferredNotifications)
                deferredNotifications.Marker = true;
        }

        internal bool PrepareDeferredPublication()
        {
            var state = deferredNotifications;
            if (disposed || disposeRequested || state == null || !state.Pending || state.Scopes.Count != 0)
                return false;
            var changed = state.Marker || state.InitialReservationCount != reservations.Count;
            for (var i = 0; i < state.Rows.Count; i++)
            {
                var row = state.Rows[i];
                if (row.Before.Total != row.After.Total || row.Before.Reserved != row.After.Reserved)
                    changed = true;
            }
            state.Pending = false;
            if (!changed)
            {
                state.Clear();
                return false;
            }

            BeginPublication(ResourceChangeKind.Transaction, state.Context);
            for (var i = 0; i < state.Rows.Count; i++)
            {
                var row = state.Rows[i];
                if (row.Before.Total != row.After.Total || row.Before.Reserved != row.After.Reserved)
                    Record(row.Before, row.After, ResourceChangeKind.Transaction, state.Context);
            }
            state.Clear();
            return true;
        }

        private void FlushDeferredNotifications()
        {
            if (busy || disposed || !HasDeferredNotifications || deferredNotifications.Scopes.Count != 0)
                return;
            busy = true;
            try
            {
                if (PrepareDeferredPublication())
                    Publish();
            }
            finally
            {
                busy = false;
                FinishDeferredDispose();
            }
        }

        private void InvalidateDeferredNotifications()
        {
            deferredNotifications?.Clear();
            publicationDeferred = false;
        }
    }
}
