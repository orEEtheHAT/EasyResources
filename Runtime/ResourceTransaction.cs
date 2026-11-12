using System;
using System.Collections.Generic;
using System.Threading;

namespace EasyResources
{
    // Used by the concrete inventory/progression modules. Apply only swaps already prepared state.
    internal interface IResourceParticipant
    {
        void Enter();
        bool Prepare(out ResourceFailure failure);
        void Apply();
        void Publish();
        void Exit();
        void Finish();
    }

    /// <summary>Reusable, atomic plan. All stores are updated before any observer runs.</summary>
    public sealed class ResourceTransaction
    {
        internal struct Line
        {
            internal ResourceStore Store;
            internal ResourceDefinition Resource;
            internal long TotalDelta, ReservedDelta, RequiredAvailable;
            internal ResourceBalance Before, After;
            internal bool Changed => Before.Total != After.Total || Before.Reserved != After.Reserved;
        }

        private struct Target
        {
            internal ResourceStore Store;
            internal bool Changed, Forced, DeferredPublication;
            internal long Weight, Volume;
        }

        private struct ReservationAction
        {
            internal ResourceReservation Reservation;
            internal ResourceAmount[] Part;
            internal bool Cancel;
        }

        internal readonly List<Line> Lines;
        private readonly List<Line> work;
        private readonly List<Target> targets;
        private readonly List<ReservationAction> reservations;
        private readonly List<IResourceParticipant> participants = new List<IResourceParticipant>();
        private readonly int thread = Thread.CurrentThread.ManagedThreadId;
        private bool executing, clearWhenFinished;
        private ResourceBatch queuedBy;
        public ResourceFailure LastFailure { get; private set; }

        public ResourceTransaction(int capacity = 8)
        {
            if (capacity < 0)
                throw new ArgumentOutOfRangeException(nameof(capacity));
            Lines = new List<Line>(capacity);
            work = new List<Line>(capacity);
            targets = new List<Target>(4);
            reservations = new List<ReservationAction>(4);
        }

        public ResourceTransaction Clear()
        {
            CheckBuilder();
            Lines.Clear();
            work.Clear();
            targets.Clear();
            reservations.Clear();
            participants.Clear();
            LastFailure = default;
            return this;
        }

        public ResourceTransaction Add(ResourceStore store, ResourceDefinition resource, long amount)
        {
            if (amount < 0)
                throw new ArgumentOutOfRangeException(nameof(amount));
            AddLine(store, resource, amount, 0);
            return this;
        }

        public ResourceTransaction Add(ResourceStore store, ResourceCost contents)
        {
            CheckCost(contents);
            Touch(store);
            foreach (var row in contents.GetPrepared())
                Add(store, row.Resource, row.Amount);
            return this;
        }

        public ResourceTransaction Spend(ResourceStore store, ResourceCost cost)
        {
            CheckCost(cost);
            Touch(store);
            foreach (var row in cost.GetPrepared())
                AddLine(store, row.Resource, -row.Amount, 0, row.Amount);
            return this;
        }

        public ResourceTransaction Spend(ResourceStore store, ResourceDefinition resource, long amount)
        {
            if (amount < 0)
                throw new ArgumentOutOfRangeException(nameof(amount));
            AddLine(store, resource, -amount, 0, amount);
            return this;
        }

        public ResourceTransaction Transfer(ResourceStore source, ResourceStore destination, ResourceCost cost)
        {
            Spend(source, cost);
            Add(destination, cost);
            return this;
        }

        public ResourceTransaction Exchange(ResourceStore store, ResourceCost payment, ResourceCost reward)
        {
            Spend(store, payment);
            Add(store, reward);
            return this;
        }

        /// <summary>Consume a complete held cost, optionally alongside grants to another store.</summary>
        public ResourceTransaction Commit(ResourceReservation reservation) => AddReservation(reservation, null, false);
        public ResourceTransaction Commit(ResourceReservation reservation, ResourceCost part)
        {
            CheckCost(part);
            return AddReservation(reservation, part.GetPrepared(), false);
        }

        internal ResourceTransaction Cancel(ResourceReservation reservation) => AddReservation(reservation, null, true);
        internal ResourceTransaction Join(IResourceParticipant participant)
        {
            CheckBuilder();
            if (participant == null)
                throw new ArgumentNullException(nameof(participant));
            participants.Add(participant);
            return this;
        }

        private ResourceTransaction AddReservation(ResourceReservation reservation, ResourceAmount[] part, bool cancel)
        {
            CheckBuilder();
            for (var i = 0; i < reservations.Count; i++)
                if (reservations[i].Reservation.Equals(reservation))
                    throw new ArgumentException("Use each reservation once in a transaction.");
            reservations.Add(new ReservationAction { Reservation = reservation, Part = part, Cancel = cancel });
            if (reservation.Owner != null)
                Touch(reservation.Owner);
            return this;
        }

        private void AddLine(ResourceStore store, ResourceDefinition resource, long total, long reserved, long requiredAvailable = 0)
        {
            CheckBuilder();
            Touch(store);
            if (resource == null)
                throw new ArgumentNullException(nameof(resource));
            for (var i = 0; i < Lines.Count; i++)
            {
                var line = Lines[i];
                if (!ReferenceEquals(line.Store, store))
                    continue;
                if (ReferenceEquals(line.Resource, resource))
                {
                    line.TotalDelta = checked(line.TotalDelta + total);
                    line.ReservedDelta = checked(line.ReservedDelta + reserved);
                    line.RequiredAvailable = checked(line.RequiredAvailable + requiredAvailable);
                    Lines[i] = line;
                    return;
                }

                if (line.Resource.Id == resource.Id)
                    throw new ArgumentException($"Duplicate resource ID '{resource.Id}'.");
            }

            Lines.Add(new Line { Store = store, Resource = resource, TotalDelta = total, ReservedDelta = reserved, RequiredAvailable = requiredAvailable });
        }

        private int Touch(ResourceStore store)
        {
            CheckBuilder();
            if (store == null)
                throw new ArgumentNullException(nameof(store));
            for (var i = 0; i < targets.Count; i++)
                if (ReferenceEquals(targets[i].Store, store))
                    return i;
            targets.Add(new Target { Store = store });
            return targets.Count - 1;
        }

        public bool CanExecute(out ResourceFailure failure)
        {
            CheckBuilder();
            return Prepare(out failure);
        }

        public bool TryExecute(out ResourceFailure failure, ResourceOperationContext context = default, ResourceChangeKind kind = ResourceChangeKind.Transaction)
        {
            CheckBuilder();
            // A reserved plan is consumable; use a fresh Clear/Commit when reusing the builder.
            for (var i = 0; i < targets.Count; i++)
                targets[i].Store.CheckMutation();
            var entered = 0;
            executing = true;
            try
            {
                for (var i = 0; i < targets.Count; i++)
                    targets[i].Store.EnterTransaction();
                foreach (var participant in participants)
                {
                    participant.Enter();
                    entered++;
                }

                if (!Prepare(out failure))
                {
                    LastFailure = failure;
                    return false;
                }

                foreach (var participant in participants)
                    if (!participant.Prepare(out failure))
                    {
                        LastFailure = failure;
                        return false;
                    }

                // Prepare all potentially growing dictionaries before any balance or participant state changes.
                foreach (var line in work)
                    if (line.Changed)
                        line.Store.EnsureEntry(line.Resource);
                foreach (var action in reservations)
                    action.Reservation.Owner.PrepareCompletion(action.Reservation, action.Part);
                foreach (var line in work)
                    if (line.Changed)
                        line.Store.WriteBalance(line.After);
                foreach (var action in reservations)
                    action.Reservation.Owner.ApplyCompletion(action.Reservation, action.Part);
                foreach (var participant in participants)
                    participant.Apply();
                for (var i = 0; i < targets.Count; i++)
                {
                    var target = targets[i];
                    target.Store.SetUsage(target.Weight, target.Volume);
                    if (target.Changed)
                        target.Store.BeginPublication(kind, context);
                }

                foreach (var line in work)
                    if (line.Changed)
                        line.Store.Record(line.Before, line.After, kind, context);
                for (var i = 0; i < targets.Count; i++)
                    if (targets[i].Changed)
                        targets[i].Store.Publish();
                foreach (var participant in participants)
                    participant.Publish();
                failure = default;
                LastFailure = default;
                return true;
            }
            finally
            {
                try
                {
                    try { FlushClosedNotifications(); }
                    finally
                    {
                        for (var i = entered - 1; i >= 0; i--)
                            participants[i].Exit();
                        for (var i = 0; i < targets.Count; i++)
                            targets[i].Store.ExitTransaction();
                        for (var i = 0; i < targets.Count; i++)
                            targets[i].Store.FinishDeferredDispose();
                        for (var i = 0; i < entered; i++)
                            participants[i].Finish();
                    }
                }
                finally
                {
                    executing = false;
                    if (clearWhenFinished)
                    {
                        clearWhenFinished = false;
                        Clear();
                    }
                }
            }
        }

        public bool TryExecute() => TryExecute(out _);
        private void FlushClosedNotifications()
        {
            bool prepared;
            do
            {
                prepared = false;
                for (var i = 0; i < targets.Count; i++)
                {
                    var target = targets[i];
                    target.DeferredPublication = target.Store.PrepareDeferredPublication();
                    prepared |= target.DeferredPublication;
                    targets[i] = target;
                }
                if (prepared)
                    for (var i = 0; i < targets.Count; i++)
                        if (targets[i].DeferredPublication)
                            targets[i].Store.Publish();
            } while (prepared);
        }
        private bool Prepare(out ResourceFailure failure)
        {
            work.Clear();
            work.AddRange(Lines);
            for (var i = 0; i < targets.Count; i++)
            {
                var target = targets[i];
                target.Forced = false;
                targets[i] = target;
            }

            // Build reservation debits only during execution. A completed handle is rejected before its old rows can be spent again.
            foreach (var action in reservations)
            {
                if (action.Reservation.Owner == null || !action.Reservation.Owner.TryGetSlot(action.Reservation, out var slot))
                {
                    failure = new ResourceFailure(ResourceFailureCode.ReservationNotActive);
                    return false;
                }

                if (action.Part != null)
                    foreach (var row in action.Part)
                        if (slot.GetAmount(row.Resource) < row.Amount)
                        {
                            failure = new ResourceFailure(ResourceFailureCode.ReservationAmountExceeded, row.Resource, action.Reservation.Owner, row.Amount, slot.GetAmount(row.Resource));
                            return false;
                        }

                var source = action.Part ?? slot.Items;
                var count = action.Part == null ? slot.Count : source.Length;
                for (var i = 0; i < count; i++)
                    AddPreparedLine(action.Reservation.Owner, source[i].Resource, action.Cancel ? 0 : -source[i].Amount, -source[i].Amount);
                for (var i = 0; i < targets.Count; i++)
                    if (ReferenceEquals(targets[i].Store, action.Reservation.Owner))
                    {
                        var t = targets[i];
                        t.Forced = action.Part == null || action.Part.Length != 0;
                        targets[i] = t;
                    }
            }

            for (var i = 0; i < targets.Count; i++)
            {
                var target = targets[i];
                target.Changed = target.Forced;
                target.Store.CheckAlive();
                if (target.Store.IsReadOnly)
                {
                    failure = new ResourceFailure(ResourceFailureCode.ReadOnly, store: target.Store);
                    return false;
                }

                targets[i] = target;
            }

            for (var i = 0; i < work.Count; i++)
            {
                var line = work[i];
                if (!line.Store.PrepareLine(ref line, out failure))
                    return false;
                work[i] = line;
                if (line.Changed)
                    for (var t = 0; t < targets.Count; t++)
                        if (ReferenceEquals(targets[t].Store, line.Store))
                        {
                            var target = targets[t];
                            target.Changed = true;
                            targets[t] = target;
                        }
            }

            for (var i = 0; i < targets.Count; i++)
            {
                var target = targets[i];
                if (!target.Store.PrepareUsage(work, out target.Weight, out target.Volume, out failure))
                    return false;
                if (target.Changed && target.Store.Revision == long.MaxValue)
                {
                    failure = new ResourceFailure(ResourceFailureCode.AmountOverflow);
                    return false;
                }

                targets[i] = target;
            }

            failure = default;
            return true;
        }

        private void AddPreparedLine(ResourceStore store, ResourceDefinition resource, long total, long reserved)
        {
            for (var i = 0; i < work.Count; i++)
            {
                var line = work[i];
                if (!ReferenceEquals(line.Store, store) || !ReferenceEquals(line.Resource, resource))
                    continue;
                line.TotalDelta = checked(line.TotalDelta + total);
                line.ReservedDelta = checked(line.ReservedDelta + reserved);
                work[i] = line;
                return;
            }

            work.Add(new Line { Store = store, Resource = resource, TotalDelta = total, ReservedDelta = reserved });
        }

        private static void CheckCost(ResourceCost cost)
        {
            if (cost == null)
                throw new ArgumentNullException(nameof(cost));
        }

        private void CheckBuilder()
        {
            if (Thread.CurrentThread.ManagedThreadId != thread)
                throw new InvalidOperationException("Use a transaction on its creating thread.");
            if (executing)
                throw new InvalidOperationException("Cannot change or execute a transaction from its own notification.");
            if (queuedBy != null)
                throw new InvalidOperationException("This plan is queued. Wait until it is processed before reusing it.");
        }

        internal void Claim(ResourceBatch batch)
        {
            CheckBuilder();
            queuedBy = batch;
        }

        internal void Release(ResourceBatch batch)
        {
            if (!ReferenceEquals(queuedBy, batch))
                throw new InvalidOperationException("Wrong batch owner.");
            queuedBy = null;
        }

        internal void RequestClear()
        {
            if (executing)
                clearWhenFinished = true;
            else
                Clear();
        }
    }
}
