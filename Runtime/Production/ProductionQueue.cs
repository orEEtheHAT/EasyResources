using System;
using System.Collections.Generic;

namespace EasyResources.Production
{
    /// <summary>FIFO production. Inputs are held immediately; completion consumes and grants atomically.</summary>
    public sealed class ProductionQueue : IDisposable
    {
        private struct Job
        {
            internal long Id;
            internal PreparedProduction Plan;
            internal ResourceReservation Payment;
            internal long EndsAt;
            internal bool Blocked;
        }

        private readonly ResourceStore source, destination;
        private readonly Job[] jobs;
        private readonly SafeSignal changed = new SafeSignal();
        private readonly ResourceTransaction transaction = new ResourceTransaction();
        private readonly FinishParticipant completion;
        private readonly List<ResourceAmount> remaining = new List<ResourceAmount>();
        private int count;
        private long nextId, lastTime, pausedRemaining;
        private bool busy, disposed, disposeRequested, paused;
        public string OwnerId { get; }
        public int Count => count;
        public int Capacity => jobs.Length;
        public bool IsPaused => paused;
        public ResourceFailure LastFailure { get; private set; }

        public event Action Changed
        {
            add
            {
                CheckAlive();
                changed.Add(value);
            }

            remove
            {
                changed.Remove(value);
            }
        }

        public ProductionQueue(ResourceStore source, ResourceStore destination = null, int capacity = 8, string ownerId = null)
        {
            if (source == null || capacity <= 0)
                throw new ArgumentException("Supply a store and positive queue capacity.");
            this.source = source;
            this.destination = destination ?? source;
            jobs = new Job[capacity];
            OwnerId = string.IsNullOrWhiteSpace(ownerId) ? Guid.NewGuid().ToString("N") : ownerId;
            completion = new FinishParticipant(this);
        }

        public bool TryEnqueue(ProductionRecipe recipe, long nowMilliseconds, out ProductionTicket ticket, long quantity = 1)
        {
            if (recipe == null)
                throw new ArgumentNullException(nameof(recipe));
            return TryEnqueue(recipe.Prepare(quantity), nowMilliseconds, out ticket);
        }

        public bool TryEnqueue(PreparedProduction plan, long nowMilliseconds, out ProductionTicket ticket)
        {
            CheckMutation();
            CheckTime(nowMilliseconds);
            ticket = default;
            if (plan == null)
                throw new ArgumentNullException(nameof(plan));
            if (count == jobs.Length)
            {
                LastFailure = new ResourceFailure(ResourceFailureCode.RuleRejected, detail: "Production queue is full.");
                return false;
            }

            var id = checked(nextId + 1);
            var deadline = count == 0 ? checked(nowMilliseconds + plan.DurationMilliseconds) : 0;
            var index = count;
            var job = new Job
            {
                Id = id,
                Plan = plan,
                EndsAt = deadline
            };
            // Set visible queue state before the ledger's reservation notification.
            busy = true;
            jobs[index] = job;
            count++;
            nextId = id;
            try
            {
                if (!source.TryReserve(plan.Inputs, out jobs[index].Payment, out var failure, new ResourceReservationOptions(OwnerId, nowMilliseconds: nowMilliseconds)))
                {
                    jobs[index] = default;
                    count--;
                    LastFailure = failure;
                    return false;
                }

                ticket = new ProductionTicket(this, id);
                lastTime = nowMilliseconds;
                LastFailure = default;
                if (paused && index == 0)
                    pausedRemaining = plan.DurationMilliseconds;
                PublishStandalone();
                return true;
            }
            catch
            {
                if (count > index)
                {
                    jobs[index].Payment.Cancel();
                    jobs[index] = default;
                    count--;
                }

                throw;
            }
            finally
            {
                busy = false;
                FinishRequestedDispose();
            }
        }

        public int Tick(long nowMilliseconds, int maximumCompletions = 16)
        {
            CheckMutation();
            CheckTime(nowMilliseconds);
            if (maximumCompletions < 0)
                throw new ArgumentOutOfRangeException(nameof(maximumCompletions));
            lastTime = nowMilliseconds;
            if (paused)
                return 0;
            var completed = 0;
            while (!disposed && count > 0 && completed < maximumCompletions && jobs[0].EndsAt <= nowMilliseconds)
            {
                var job = jobs[0];
                if (!job.Payment.IsActive)
                {
                    Cancel(job.Id);
                    LastFailure = new ResourceFailure(ResourceFailureCode.ReservationNotActive);
                    continue;
                }

                completion.Now = nowMilliseconds;
                transaction.Clear().Commit(job.Payment);
                foreach (var row in job.Plan.Outputs.GetPrepared())
                    transaction.Add(destination, row.Resource, row.Amount);
                transaction.Join(completion);
                if (!transaction.TryExecute(out var failure, new ResourceOperationContext("production", OwnerId, nowMilliseconds)))
                {
                    jobs[0].Blocked = true;
                    LastFailure = failure;
                    break;
                }

                LastFailure = default;
                completed++;
            }

            return completed;
        }

        public bool Contains(long id)
        {
            if (disposed)
                return false;
            for (var i = 0; i < count; i++)
                if (jobs[i].Id == id)
                    return jobs[i].Payment.IsActive;
            return false;
        }

        public bool TryGetTicket(long id, out ProductionTicket ticket)
        {
            ticket = default;
            if (!Contains(id))
                return false;
            ticket = new ProductionTicket(this, id);
            return true;
        }

        public bool Cancel(long id)
        {
            if (disposed)
                return false;
            CheckMutation();
            var index = -1;
            for (var i = 0; i < count; i++)
                if (jobs[i].Id == id)
                {
                    index = i;
                    break;
                }

            if (index < 0)
                return false;
            busy = true;
            try
            {
                var old = jobs[index];
                RemoveAt(index, lastTime);
                old.Payment.Cancel();
                PublishStandalone();
                return true;
            }
            finally
            {
                busy = false;
                FinishRequestedDispose();
            }
        }

        public void Pause(long nowMilliseconds)
        {
            CheckMutation();
            CheckTime(nowMilliseconds);
            if (paused)
                return;
            pausedRemaining = count == 0 ? 0 : Math.Max(0, jobs[0].EndsAt - nowMilliseconds);
            paused = true;
            lastTime = nowMilliseconds;
            Notify();
        }

        public void Resume(long nowMilliseconds)
        {
            CheckMutation();
            CheckTime(nowMilliseconds);
            if (!paused)
                return;
            var deadline = count == 0 ? 0 : checked(nowMilliseconds + pausedRemaining);
            if (count > 0)
                jobs[0].EndsAt = deadline;
            paused = false;
            lastTime = nowMilliseconds;
            Notify();
        }

        public void CopyJobsTo(List<ProductionJobInfo> destination, long nowMilliseconds)
        {
            CheckAlive();
            if (destination == null)
                throw new ArgumentNullException(nameof(destination));
            destination.Clear();
            for (var i = 0; i < count; i++)
                destination.Add(new ProductionJobInfo(jobs[i].Id, jobs[i].Plan, i == 0 ? (paused ? pausedRemaining : Math.Max(0, jobs[i].EndsAt - nowMilliseconds)) : jobs[i].Plan.DurationMilliseconds));
        }

        private void RemoveAt(int index, long nextStart)
        {
            var nextDeadline = index == 0 && count > 1 ? checked(nextStart + jobs[1].Plan.DurationMilliseconds) : 0;
            for (var i = index; i < count - 1; i++)
                jobs[i] = jobs[i + 1];
            jobs[--count] = default;
            if (index == 0 && count > 0)
            {
                jobs[0].EndsAt = nextDeadline;
                if (paused)
                    pausedRemaining = jobs[0].Plan.DurationMilliseconds;
            }
        }

        private void CheckTime(long now)
        {
            if (now < 0 || now < lastTime)
                throw new ArgumentOutOfRangeException(nameof(now), "Time must be monotonic milliseconds.");
        }

        private void CheckAlive()
        {
            if (disposed)
                throw new ObjectDisposedException(nameof(ProductionQueue));
            source.CheckAlive();
            destination.CheckAlive();
        }

        private void CheckMutation()
        {
            CheckAlive();
            if (busy)
                throw new InvalidOperationException("Queue mutations are not allowed during notifications.");
            source.CheckMutation();
            destination.CheckMutation();
        }

        private void Notify()
        {
            busy = true;
            try
            {
                PublishStandalone();
            }
            finally
            {
                busy = false;
                FinishRequestedDispose();
            }
        }

        private void PublishStandalone()
        {
            source.EnterTransaction();
            if (!ReferenceEquals(source, destination))
                destination.EnterTransaction();
            try
            {
                changed.Emit();
            }
            finally
            {
                source.ExitTransaction();
                if (!ReferenceEquals(source, destination))
                    destination.ExitTransaction();
                source.FinishDeferredDispose();
                destination.FinishDeferredDispose();
            }
        }

        public void Dispose()
        {
            if (disposed)
                return;
            if (busy)
                throw new InvalidOperationException("Dispose the queue after its notification returns.");
            disposed = true;
            var oldCount = count;
            count = 0;
            for (var i = 0; i < oldCount; i++)
            {
                jobs[i].Payment.Cancel();
                jobs[i] = default;
            }

            changed.Clear();
            remaining.Clear();
            transaction.RequestClear();
        }

        internal void ValidateReplacement()
        {
            CheckMutation();
            for (var i = 0; i < count; i++)
                if (jobs[i].Payment.IsActive)
                    throw new InvalidOperationException("Restore the input store first so old queue reservations become invalid, then restore the queue.");
        }

        internal void RequestDispose()
        {
            if (busy)
                disposeRequested = true;
            else
                Dispose();
        }

        private void FinishRequestedDispose()
        {
            if (disposeRequested && !disposed)
                Dispose();
        }

        private sealed class FinishParticipant : IResourceParticipant
        {
            private readonly ProductionQueue owner;
            private long nextStart;
            internal long Now;
            internal FinishParticipant(ProductionQueue owner)
            {
                this.owner = owner;
            }

            public void Enter()
            {
                if (owner.busy || owner.disposed)
                    throw new InvalidOperationException("Production queue is busy or disposed.");
                owner.busy = true;
            }

            public bool Prepare(out ResourceFailure failure)
            {
                var job = owner.jobs[0];
                job.Payment.CopyRemainingTo(owner.remaining);
                var expected = job.Plan.Inputs.GetPrepared();
                if (owner.remaining.Count != expected.Length)
                {
                    failure = new ResourceFailure(ResourceFailureCode.RuleRejected, detail: "The job's held input was changed externally.");
                    return false;
                }

                foreach (var row in owner.remaining)
                    if (job.Plan.Inputs.GetAmount(row.Resource) != row.Amount)
                    {
                        failure = new ResourceFailure(ResourceFailureCode.RuleRejected, detail: "The job's held input was changed externally.");
                        return false;
                    }

                nextStart = job.Blocked ? Now : job.EndsAt;
                if (owner.count > 1)
                    checked
                    {
                        _ = nextStart + owner.jobs[1].Plan.DurationMilliseconds;
                    }

                failure = default;
                return true;
            }

            public void Apply()
            {
                owner.RemoveAt(0, nextStart);
                owner.LastFailure = default;
            }

            public void Publish() => owner.changed.Emit();
            public void Exit() => owner.busy = false;
            public void Finish() => owner.FinishRequestedDispose();
        }

        public ProductionQueueState CaptureState(long nowMilliseconds)
        {
            CheckAlive();
            CheckTime(nowMilliseconds);
            var state = new ProductionQueueState
            {
                OwnerId = OwnerId,
                LastTicketId = nextId,
                Paused = paused
            };
            for (var i = 0; i < count; i++)
                state.Jobs.Add(new ProductionJobState { Id = jobs[i].Id, RecipeId = jobs[i].Plan.Recipe.Id, Quantity = jobs[i].Plan.Quantity, ReservationId = jobs[i].Payment.Id, DurationMilliseconds = jobs[i].Plan.DurationMilliseconds, Blocked = jobs[i].Blocked, RemainingMilliseconds = i == 0 ? (paused ? pausedRemaining : Math.Max(0, jobs[i].EndsAt - nowMilliseconds)) : jobs[i].Plan.DurationMilliseconds, Inputs = SnapshotRows.Capture(jobs[i].Plan.Inputs.GetPrepared()), Outputs = SnapshotRows.Capture(jobs[i].Plan.Outputs.GetPrepared()) });
            return state;
        }

        public static ProductionQueue FromState(ResourceStore source, ResourceStore destination, ProductionQueueState state, IEnumerable<ProductionRecipe> recipes, IEnumerable<ResourceDefinition> resources, long nowMilliseconds, int capacity = 8, long offlineMilliseconds = 0)
        {
            if (state == null || state.Version != 1 || state.Jobs == null || string.IsNullOrWhiteSpace(state.OwnerId) || state.LastTicketId < 0 || state.Jobs.Count > capacity || nowMilliseconds < 0 || offlineMilliseconds < 0)
                throw new ArgumentException("Invalid production snapshot or insufficient queue capacity.");
            var recipeLookup = new DefinitionLookup<ProductionRecipe>(recipes);
            var resourceLookup = new DefinitionLookup<ResourceDefinition>(resources);
            var queue = new ProductionQueue(source, destination, capacity, state.OwnerId);
            var ids = new HashSet<long>();
            var payments = new HashSet<long>();
            foreach (var saved in state.Jobs)
            {
                if (saved == null || saved.Id <= 0 || !ids.Add(saved.Id) || !payments.Add(saved.ReservationId) || saved.Quantity <= 0 || saved.DurationMilliseconds < 0 || saved.RemainingMilliseconds < 0)
                    throw new ArgumentException("Malformed or duplicate production job.");
                if (!source.TryGetReservation(saved.ReservationId, out var handle) || !source.TryGetSlot(handle, out var slot) || slot.Options.OwnerId != state.OwnerId)
                    throw new ArgumentException("Restore the named input reservations before restoring this production queue.");
                var input = SnapshotRows.Restore(saved.Inputs, resourceLookup);
                var output = SnapshotRows.Restore(saved.Outputs, resourceLookup);
                if (input.GetPrepared().Length != slot.Count)
                    throw new ArgumentException("Saved input does not match the held cost.");
                foreach (var row in input.GetPrepared())
                    if (slot.GetAmount(row.Resource) != row.Amount)
                        throw new ArgumentException("Saved input does not match the held cost.");
                queue.jobs[queue.count++] = new Job
                {
                    Id = saved.Id,
                    Payment = handle,
                    Plan = new PreparedProduction(recipeLookup.Get(saved.RecipeId), saved.Quantity, input, output, saved.DurationMilliseconds),
                    EndsAt = checked(nowMilliseconds + (saved.RemainingMilliseconds - (state.Paused ? 0 : offlineMilliseconds))),
                    Blocked = saved.Blocked
                };
                queue.nextId = Math.Max(queue.nextId, saved.Id);
            }

            queue.nextId = Math.Max(queue.nextId, state.LastTicketId);
            queue.lastTime = nowMilliseconds;
            queue.paused = state.Paused;
            queue.pausedRemaining = state.Jobs.Count == 0 ? 0 : state.Jobs[0].RemainingMilliseconds;
            return queue;
        }
    }

    [Serializable]
    public sealed class ProductionQueueState
    {
        public int Version = 1;
        public string OwnerId;
        public long LastTicketId;
        public bool Paused;
        public List<ProductionJobState> Jobs = new List<ProductionJobState>();
    }

    [Serializable]
    public sealed class ProductionJobState
    {
        public long Id;
        public string RecipeId;
        public long Quantity;
        public long ReservationId;
        public long DurationMilliseconds;
        public long RemainingMilliseconds;
        public bool Blocked;
        public List<ResourceStateEntry> Inputs;
        public List<ResourceStateEntry> Outputs;
    }
}
