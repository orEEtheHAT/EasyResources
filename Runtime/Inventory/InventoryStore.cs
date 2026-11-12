using System;
using System.Collections.Generic;
using System.Threading;

namespace EasyResources.Inventory
{
    /// <summary>Fixed slots, exact stacks and unique instance IDs. Conditions are data; combat rules remain external.</summary>
    public sealed class InventoryStore : IDisposable
    {
        private InventorySlot[] slots, buffer;
        private readonly long? maximumWeight, maximumVolume;
        private readonly HashSet<string> accepted;
        private readonly int thread = Thread.CurrentThread.ManagedThreadId;
        private readonly SafeSignal changed = new SafeSignal();
        private readonly ResourceTransaction transaction = new ResourceTransaction();
        private readonly Mutation mutation;
        private bool busy, disposed, disposeRequested;
        private long weight, volume;
        public int Capacity => slots.Length;
        public long UsedWeight => weight;
        public long UsedVolume => volume;
        public InventoryChangeKind LastChangeKind { get; private set; }

        public event Action Changed
        {
            add
            {
                CheckAlive();
                changed.Add(value);
            }

            remove
            {
                CheckThread();
                changed.Remove(value);
            }
        }

        public InventoryStore(int capacity = 24, long? maximumWeight = null, long? maximumVolume = null, IEnumerable<ItemDefinition> acceptedItems = null)
        {
            if (capacity <= 0 || maximumWeight < 0 || maximumVolume < 0)
                throw new ArgumentOutOfRangeException("Invalid inventory capacity.");
            slots = new InventorySlot[capacity];
            buffer = new InventorySlot[capacity];
            this.maximumWeight = maximumWeight;
            this.maximumVolume = maximumVolume;
            mutation = new Mutation(this);
            if (acceptedItems == null)
                return;
            accepted = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in acceptedItems)
            {
                ValidateDefinition(item);
                accepted.Add(item.Id);
            }
        }

        public InventorySlot GetSlot(int index)
        {
            CheckAlive();
            if (index < 0 || index >= slots.Length)
                throw new ArgumentOutOfRangeException(nameof(index));
            return slots[index];
        }

        public long GetCount(ItemDefinition item)
        {
            CheckAlive();
            ValidateDefinition(item);
            long total = 0;
            foreach (var slot in slots)
                if (ReferenceEquals(slot.Item, item))
                    total += slot.Count;
            return total;
        }

        public void CopySlotsTo(List<InventorySlot> destination)
        {
            CheckAlive();
            if (destination == null)
                throw new ArgumentNullException(nameof(destination));
            destination.Clear();
            destination.AddRange(slots);
        }

        public bool TryAdd(ItemDefinition item, long count, out ResourceFailure failure, ItemProperties properties = default)
        {
            CheckMutation();
            mutation.Add(item, count, properties, false, null);
            return transaction.Clear().Join(mutation).TryExecute(out failure);
        }

        public long AddUpTo(ItemDefinition item, long count, ItemProperties properties = default)
        {
            CheckMutation();
            mutation.Add(item, count, properties, true, null);
            return transaction.Clear().Join(mutation).TryExecute(out _) ? mutation.Actual : 0;
        }

        public bool TryRemove(ItemDefinition item, long count, out ResourceFailure failure)
        {
            CheckMutation();
            ValidateDefinition(item);
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count));
            mutation.Remove(item, count, -1);
            return transaction.Clear().Join(mutation).TryExecute(out failure);
        }

        public bool TryTransferTo(InventoryStore destination, int slotIndex, long count, out ResourceFailure failure)
        {
            CheckMutation();
            if (destination == null)
                throw new ArgumentNullException(nameof(destination));
            destination.CheckMutation();
            var slot = GetSlot(slotIndex);
            if (count <= 0)
                throw new ArgumentOutOfRangeException(nameof(count));
            if (slot.IsEmpty || count > slot.Count)
            {
                failure = Failure("The selected stack does not contain enough items.");
                return false;
            }

            if (ReferenceEquals(this, destination))
            {
                failure = default;
                return true;
            }

            mutation.Remove(slot.Item, count, slotIndex);
            mutation.Kind = InventoryChangeKind.Transfer;
            destination.mutation.Add(slot.Item, count, slot.Properties, false, slot.InstanceId);
            destination.mutation.Kind = InventoryChangeKind.Transfer;
            return transaction.Clear().Join(mutation).Join(destination.mutation).TryExecute(out failure, new ResourceOperationContext("item-transfer"));
        }

        public bool TryCraft(ResourceStore paymentStore, ResourceCost cost, ItemDefinition output, long count, out ResourceFailure failure, ItemProperties properties = default)
        {
            CheckMutation();
            mutation.Add(output, count, properties, false, null);
            mutation.Kind = InventoryChangeKind.Craft;
            return transaction.Clear().Spend(paymentStore, cost).Join(mutation).TryExecute(out failure, new ResourceOperationContext("item-craft"));
        }

        public bool TryReceiveReserved(ResourceReservation payment, ItemDefinition output, long count, out ResourceFailure failure, ItemProperties properties = default)
        {
            CheckMutation();
            mutation.Add(output, count, properties, false, null);
            mutation.Kind = InventoryChangeKind.Craft;
            return transaction.Clear().Commit(payment).Join(mutation).TryExecute(out failure, new ResourceOperationContext("item-craft"));
        }

        public bool TryChangeCondition(int slotIndex, int quality, int durability, out ResourceFailure failure)
        {
            CheckMutation();
            var current = GetSlot(slotIndex);
            if (current.IsEmpty)
            {
                failure = Failure("The selected slot is empty.");
                return false;
            }

            if (quality < 0 || durability < 0 || durability > current.Item.MaximumDurability)
                throw new ArgumentOutOfRangeException("Invalid item condition.");
            mutation.Condition(slotIndex, quality, durability);
            return transaction.Clear().Join(mutation).TryExecute(out failure);
        }

        public long RemoveExpired(long nowMilliseconds)
        {
            CheckMutation();
            if (nowMilliseconds < 0)
                throw new ArgumentOutOfRangeException(nameof(nowMilliseconds));
            mutation.Expire(nowMilliseconds);
            if (!transaction.Clear().Join(mutation).TryExecute(out var failure))
                throw new InvalidOperationException(failure.ToString());
            return mutation.Actual;
        }

        public bool TryResize(int capacity)
        {
            CheckMutation();
            if (capacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(capacity));
            if (capacity == slots.Length)
                return true;
            for (var i = capacity; i < slots.Length; i++)
                if (!slots[i].IsEmpty)
                    return false;
            var next = new InventorySlot[capacity];
            Array.Copy(slots, next, Math.Min(slots.Length, capacity));
            var nextBuffer = new InventorySlot[capacity];
            slots = next;
            buffer = nextBuffer;
            LastChangeKind = InventoryChangeKind.Resize;
            busy = true;
            try
            {
                changed.Emit();
            }
            finally
            {
                busy = false;
                FinishDispose();
            }

            return true;
        }

        private static ResourceFailure Failure(string message) => new ResourceFailure(ResourceFailureCode.RuleRejected, detail: message);
        private static void ValidateDefinition(ItemDefinition item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.Id) || item.MaximumStack < 1 || item.MaximumDurability < 0 || item.WeightPerItem < 0 || item.VolumePerItem < 0)
                throw new ArgumentException("Invalid Item asset.");
        }

        private void CheckThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != thread)
                throw new InvalidOperationException("Use inventory on its creating Unity thread.");
        }

        private void CheckAlive()
        {
            CheckThread();
            if (disposed)
                throw new ObjectDisposedException(nameof(InventoryStore));
        }

        private void CheckMutation()
        {
            CheckAlive();
            if (busy)
                throw new InvalidOperationException("Inventory is participating in a notification. Queue changes for later.");
        }

        public void Dispose()
        {
            CheckThread();
            if (disposed)
                return;
            CheckMutation();
            disposed = true;
            Array.Clear(slots, 0, slots.Length);
            Array.Clear(buffer, 0, buffer.Length);
            changed.Clear();
            transaction.RequestClear();
        }

        internal void RequestDispose()
        {
            if (busy)
                disposeRequested = true;
            else
                Dispose();
        }

        private void FinishDispose()
        {
            if (disposeRequested && !disposed)
                Dispose();
        }

        private sealed class Mutation : IResourceParticipant
        {
            private enum Mode
            {
                Add,
                Remove,
                Condition,
                Expire,
                Restore
            }

            private readonly InventoryStore owner;
            private Mode mode;
            private ItemDefinition item;
            private long amount, now;
            private int index, quality, durability;
            private ItemProperties properties;
            private bool partial;
            private string instanceId;
            private InventorySlot[] replacement;
            private long newWeight, newVolume;
            private bool hasChanges;
            internal long Actual;
            internal InventoryChangeKind Kind;
            internal Mutation(InventoryStore owner)
            {
                this.owner = owner;
            }

            internal void Add(ItemDefinition item, long count, ItemProperties properties, bool partial, string instanceId)
            {
                ValidateDefinition(item);
                if (count < 0)
                    throw new ArgumentOutOfRangeException(nameof(count));
                var d = properties.OverrideDurability ? properties.Durability : item.MaximumDurability;
                if (d < 0 || d > item.MaximumDurability)
                    throw new ArgumentException("Item durability exceeds its definition.");
                this.item = item;
                amount = count;
                this.properties = properties;
                this.partial = partial;
                this.instanceId = instanceId;
                mode = Mode.Add;
                Kind = InventoryChangeKind.Add;
            }

            internal void Remove(ItemDefinition item, long count, int index)
            {
                mode = Mode.Remove;
                this.item = item;
                amount = count;
                this.index = index;
                Kind = InventoryChangeKind.Remove;
            }

            internal void Condition(int index, int quality, int durability)
            {
                mode = Mode.Condition;
                this.index = index;
                this.quality = quality;
                this.durability = durability;
                Kind = InventoryChangeKind.Condition;
            }

            internal void Expire(long now)
            {
                mode = Mode.Expire;
                this.now = now;
                Kind = InventoryChangeKind.Expire;
            }

            internal void Restore(InventorySlot[] slots)
            {
                mode = Mode.Restore;
                replacement = slots;
                Kind = InventoryChangeKind.Restore;
            }

            public void Enter()
            {
                owner.CheckMutation();
                owner.busy = true;
            }

            public bool Prepare(out ResourceFailure failure)
            {
                Array.Copy(owner.slots, owner.buffer, owner.slots.Length);
                Actual = 0;
                hasChanges = false;
                if (mode == Mode.Add)
                {
                    if (owner.accepted != null && !owner.accepted.Contains(item.Id))
                    {
                        failure = Failure("This inventory does not accept that item.");
                        return false;
                    }

                    long fits = 0;
                    var d = properties.OverrideDurability ? properties.Durability : item.MaximumDurability;
                    foreach (var slot in owner.slots)
                    {
                        if (!slot.IsEmpty && slot.Item.Id == item.Id && !ReferenceEquals(slot.Item, item))
                            throw new ArgumentException("Duplicate Item ID.");
                        if (item.IsUnique && !string.IsNullOrEmpty(instanceId) && slot.InstanceId == instanceId)
                        {
                            failure = Failure("That unique item is already present.");
                            return false;
                        }

                        if (slot.IsEmpty)
                            fits += item.MaximumStack;
                        else if (CanStack(slot, item, properties, d))
                            fits += item.MaximumStack - slot.Count;
                    }

                    if (owner.maximumWeight.HasValue && item.WeightPerItem > 0)
                        fits = Math.Min(fits, (owner.maximumWeight.Value - owner.weight) / item.WeightPerItem);
                    if (owner.maximumVolume.HasValue && item.VolumePerItem > 0)
                        fits = Math.Min(fits, (owner.maximumVolume.Value - owner.volume) / item.VolumePerItem);
                    if (!partial && amount > fits)
                    {
                        failure = Failure("Not enough compatible slots, weight or volume capacity.");
                        return false;
                    }

                    Actual = Math.Min(amount, fits);
                    var left = Actual;
                    for (var i = 0; i < owner.buffer.Length && left > 0; i++)
                        if (CanStack(owner.buffer[i], item, properties, d))
                        {
                            var add = (int)Math.Min(left, item.MaximumStack - owner.buffer[i].Count);
                            owner.buffer[i] = owner.buffer[i].WithCount(owner.buffer[i].Count + add);
                            left -= add;
                        }

                    for (var i = 0; i < owner.buffer.Length && left > 0; i++)
                        if (owner.buffer[i].IsEmpty)
                        {
                            var add = (int)Math.Min(left, item.MaximumStack);
                            owner.buffer[i] = new InventorySlot(item, add, item.IsUnique ? (instanceId ?? Guid.NewGuid().ToString("N")) : null, properties.Quality, d, properties.HasExpiry, properties.ExpiresAtMilliseconds);
                            left -= add;
                        }

                    hasChanges = Actual != 0;
                }
                else if (mode == Mode.Remove)
                {
                    long available = index >= 0 ? owner.slots[index].Count : owner.GetCount(item);
                    if (available < amount)
                    {
                        failure = Failure("Not enough items.");
                        return false;
                    }

                    var left = amount;
                    for (var i = 0; i < owner.buffer.Length && left > 0; i++)
                        if ((index < 0 || index == i) && ReferenceEquals(owner.buffer[i].Item, item))
                        {
                            var take = (int)Math.Min(left, owner.buffer[i].Count);
                            owner.buffer[i] = owner.buffer[i].WithCount(owner.buffer[i].Count - take);
                            left -= take;
                        }

                    Actual = amount;
                    hasChanges = amount != 0;
                }
                else if (mode == Mode.Condition)
                {
                    var slot = owner.buffer[index];
                    hasChanges = slot.Quality != quality || slot.Durability != durability;
                    owner.buffer[index] = new InventorySlot(slot.Item, slot.Count, slot.InstanceId, quality, durability, slot.HasExpiry, slot.ExpiresAtMilliseconds);
                }
                else if (mode == Mode.Expire)
                {
                    for (var i = 0; i < owner.buffer.Length; i++)
                        if (!owner.buffer[i].IsEmpty && owner.buffer[i].HasExpiry && owner.buffer[i].ExpiresAtMilliseconds <= now)
                        {
                            Actual += owner.buffer[i].Count;
                            owner.buffer[i] = default;
                        }

                    hasChanges = Actual != 0;
                }
                else
                {
                    Array.Copy(replacement, owner.buffer, replacement.Length);
                    hasChanges = true;
                }

                newWeight = newVolume = 0;
                try
                {
                    foreach (var slot in owner.buffer)
                        if (!slot.IsEmpty)
                        {
                            newWeight = checked(newWeight + (long)slot.Count * slot.Item.WeightPerItem);
                            newVolume = checked(newVolume + (long)slot.Count * slot.Item.VolumePerItem);
                        }
                }
                catch (OverflowException)
                {
                    failure = new ResourceFailure(ResourceFailureCode.AmountOverflow);
                    return false;
                }

                if (owner.maximumWeight.HasValue && newWeight > owner.maximumWeight.Value || owner.maximumVolume.HasValue && newVolume > owner.maximumVolume.Value)
                {
                    failure = Failure("Inventory capacity exceeded.");
                    return false;
                }

                failure = default;
                return true;
            }

            private static bool CanStack(InventorySlot slot, ItemDefinition item, ItemProperties properties, int durability) => !slot.IsEmpty && !item.IsUnique && ReferenceEquals(slot.Item, item) && slot.Quality == properties.Quality && slot.Durability == durability && slot.HasExpiry == properties.HasExpiry && (!slot.HasExpiry || slot.ExpiresAtMilliseconds == properties.ExpiresAtMilliseconds);
            public void Apply()
            {
                if (!hasChanges)
                    return;
                var old = owner.slots;
                owner.slots = owner.buffer;
                owner.buffer = old;
                owner.weight = newWeight;
                owner.volume = newVolume;
                owner.LastChangeKind = Kind;
            }

            public void Publish()
            {
                if (hasChanges)
                    owner.changed.Emit();
            }

            public void Exit() => owner.busy = false;
            public void Finish() => owner.FinishDispose();
        }

        public InventoryState CaptureState(long nowMilliseconds = 0)
        {
            CheckAlive();
            if (nowMilliseconds < 0)
                throw new ArgumentOutOfRangeException(nameof(nowMilliseconds));
            var state = new InventoryState();
            for (var i = 0; i < slots.Length; i++)
                if (!slots[i].IsEmpty)
                {
                    var slot = slots[i];
                    state.Slots.Add(new InventorySlotState { Index = i, ItemId = slot.Item.Id, Count = slot.Count, InstanceId = slot.InstanceId, Quality = slot.Quality, Durability = slot.Durability, HasExpiry = slot.HasExpiry, RemainingMilliseconds = slot.HasExpiry ? Math.Max(0, slot.ExpiresAtMilliseconds - nowMilliseconds) : 0 });
                }

            return state;
        }

        public void RestoreState(InventoryState state, IEnumerable<ItemDefinition> definitions, long nowMilliseconds = 0, long offlineMilliseconds = 0)
        {
            CheckMutation();
            if (state == null || state.Version != 1 || state.Slots == null || nowMilliseconds < 0 || offlineMilliseconds < 0)
                throw new ArgumentException("Invalid inventory snapshot.");
            var lookup = new DefinitionLookup<ItemDefinition>(definitions);
            var candidate = new InventorySlot[slots.Length];
            var used = new HashSet<int>();
            var instanceIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in state.Slots)
            {
                if (row == null || row.Index < 0 || row.Index >= slots.Length || !used.Add(row.Index) || row.Count <= 0 || row.Quality < 0 || row.RemainingMilliseconds < 0)
                    throw new ArgumentException("Malformed inventory slot.");
                var item = lookup.Get(row.ItemId);
                ValidateDefinition(item);
                if (row.Count > item.MaximumStack || row.Durability < 0 || row.Durability > item.MaximumDurability || ownerRejects(item))
                    throw new ArgumentException("Saved item violates the current item or inventory rules.");
                if (item.IsUnique && (string.IsNullOrWhiteSpace(row.InstanceId) || !instanceIds.Add(row.InstanceId)) || !item.IsUnique && !string.IsNullOrEmpty(row.InstanceId))
                    throw new ArgumentException("Invalid or duplicate unique item ID.");
                if (row.HasExpiry && offlineMilliseconds > 0 && row.RemainingMilliseconds <= offlineMilliseconds)
                    continue;
                candidate[row.Index] = new InventorySlot(item, row.Count, row.InstanceId, row.Quality, row.Durability, row.HasExpiry, row.HasExpiry ? checked(nowMilliseconds + Math.Max(0, row.RemainingMilliseconds - offlineMilliseconds)) : 0);
            }

            mutation.Restore(candidate);
            if (!transaction.Clear().Join(mutation).TryExecute(out var failure))
                throw new ArgumentException(failure.ToString());
            bool ownerRejects(ItemDefinition item) => accepted != null && !accepted.Contains(item.Id);
        }

        public static void ValidateUniqueItems(params InventoryStore[] inventories)
        {
            if (inventories == null)
                throw new ArgumentNullException(nameof(inventories));
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var inventory in inventories)
            {
                inventory.CheckAlive();
                foreach (var slot in inventory.slots)
                    if (!string.IsNullOrEmpty(slot.InstanceId) && !ids.Add(slot.InstanceId))
                        throw new ArgumentException("Unique item exists in more than one inventory.");
            }
        }
    }

    [Serializable]
    public sealed class InventoryState
    {
        public int Version = 1;
        public List<InventorySlotState> Slots = new List<InventorySlotState>();
    }

    [Serializable]
    public sealed class InventorySlotState
    {
        public int Index;
        public string ItemId;
        public int Count;
        public string InstanceId;
        public int Quality;
        public int Durability;
        public bool HasExpiry;
        public long RemainingMilliseconds;
    }
}
