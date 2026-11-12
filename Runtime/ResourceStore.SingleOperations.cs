using System;

namespace EasyResources
{
    public sealed partial class ResourceStore
    {
        // Common single-store operations need no mutable transaction builder or working line buffers.
        // Validate all rows first; preserve exactly the same publication and reentrancy boundary.
        private bool AddSingle(ResourceDefinition resource, long amount, out ResourceFailure failure)
        {
            if (amount < 0)
                throw new ArgumentOutOfRangeException(nameof(amount));
            if (resource == null)
                throw new ArgumentNullException(nameof(resource));
            if (readOnly)
            {
                failure = new ResourceFailure(ResourceFailureCode.ReadOnly, store: this);
                return false;
            }

            var line = new ResourceTransaction.Line
            {
                Store = this,
                Resource = resource,
                TotalDelta = amount
            };
            if (!PrepareLine(ref line, out failure))
                return false;
            if (!line.Changed)
            {
                failure = default;
                return true;
            }

            if (revision == long.MaxValue)
            {
                failure = new ResourceFailure(ResourceFailureCode.AmountOverflow);
                return false;
            }

            var nextWeight = weight;
            var nextVolume = volume;
            try
            {
                if (limits.MaximumWeight.HasValue)
                    nextWeight = checked(weight + checked(amount * resource.WeightPerUnit));
                if (limits.MaximumVolume.HasValue)
                    nextVolume = checked(volume + checked(amount * resource.VolumePerUnit));
            }
            catch (OverflowException)
            {
                failure = new ResourceFailure(ResourceFailureCode.AmountOverflow, resource, this);
                return false;
            }

            if (limits.MaximumWeight.HasValue && nextWeight > limits.MaximumWeight.Value)
            {
                failure = new ResourceFailure(ResourceFailureCode.WeightCapacityExceeded, store: this, limit: limits.MaximumWeight.Value);
                return false;
            }

            if (limits.MaximumVolume.HasValue && nextVolume > limits.MaximumVolume.Value)
            {
                failure = new ResourceFailure(ResourceFailureCode.VolumeCapacityExceeded, store: this, limit: limits.MaximumVolume.Value);
                return false;
            }

            EnsureEntry(resource);
            busy = true;
            try
            {
                WriteBalance(line.After);
                weight = nextWeight;
                volume = nextVolume;
                BeginPublication(ResourceChangeKind.Add, default);
                Record(line.Before, line.After, ResourceChangeKind.Add, default);
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

        private bool SpendSingle(ResourceCost cost, out ResourceFailure failure, ResourceOperationContext context)
        {
            if (cost == null)
                throw new ArgumentNullException(nameof(cost));
            var rows = cost.GetPrepared();
            if (readOnly)
            {
                failure = new ResourceFailure(ResourceFailureCode.ReadOnly, store: this);
                return false;
            }

            if (!CanAfford(cost, out failure))
                return false;
            if (rows.Length == 0)
                return true;
            if (revision == long.MaxValue)
            {
                failure = new ResourceFailure(ResourceFailureCode.AmountOverflow);
                return false;
            }

            if (!ConsumptionUsage(rows, rows.Length, out var nextWeight, out var nextVolume, out failure))
                return false;
            busy = true;
            try
            {
                BeginPublication(ResourceChangeKind.Spend, context);
                foreach (var row in rows)
                {
                    var before = balances[row.Resource];
                    var after = new ResourceBalance(row.Resource, before.Total - row.Amount, before.Reserved);
                    WriteBalance(after);
                    Record(before, after, ResourceChangeKind.Spend, context);
                }

                weight = nextWeight;
                volume = nextVolume;
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

        private bool CompleteSingle(ResourceReservation handle, ReservationSlot slot, ResourceCost part, bool cancel, ResourceChangeKind kind, out ResourceFailure failure)
        {
            if (readOnly)
            {
                failure = new ResourceFailure(ResourceFailureCode.ReadOnly, store: this);
                return false;
            }

            var partial = part?.GetPrepared();
            var rows = partial ?? slot.Items;
            var count = partial == null ? slot.Count : partial.Length;
            if (partial != null)
            {
                foreach (var row in partial)
                    if (slot.GetAmount(row.Resource) < row.Amount)
                    {
                        failure = new ResourceFailure(ResourceFailureCode.ReservationAmountExceeded, row.Resource, this, row.Amount, slot.GetAmount(row.Resource));
                        return false;
                    }

                if (count == 0)
                {
                    failure = default;
                    return true;
                }
            }

            if (revision == long.MaxValue)
            {
                failure = new ResourceFailure(ResourceFailureCode.AmountOverflow);
                return false;
            }

            var nextWeight = weight;
            var nextVolume = volume;
            if (!cancel && !ConsumptionUsage(rows, count, out nextWeight, out nextVolume, out failure))
                return false;
            PrepareCompletion(handle, partial);
            busy = true;
            try
            {
                BeginPublication(kind, default);
                for (var i = 0; i < count; i++)
                {
                    var row = rows[i];
                    var before = balances[row.Resource];
                    var after = new ResourceBalance(row.Resource, cancel ? before.Total : before.Total - row.Amount, before.Reserved - row.Amount);
                    WriteBalance(after);
                    Record(before, after, kind, default);
                }

                ApplyCompletion(handle, partial);
                weight = nextWeight;
                volume = nextVolume;
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

        private bool ConsumptionUsage(ResourceAmount[] rows, int count, out long nextWeight, out long nextVolume, out ResourceFailure failure)
        {
            nextWeight = weight;
            nextVolume = volume;
            try
            {
                if (limits.MaximumWeight.HasValue || limits.MaximumVolume.HasValue)
                    for (var i = 0; i < count; i++)
                    {
                        if (limits.MaximumWeight.HasValue)
                            nextWeight = checked(nextWeight - checked(rows[i].Amount * rows[i].Resource.WeightPerUnit));
                        if (limits.MaximumVolume.HasValue)
                            nextVolume = checked(nextVolume - checked(rows[i].Amount * rows[i].Resource.VolumePerUnit));
                    }
            }
            catch (OverflowException)
            {
                failure = new ResourceFailure(ResourceFailureCode.AmountOverflow, store: this);
                return false;
            }

            failure = default;
            return true;
        }
    }
}
