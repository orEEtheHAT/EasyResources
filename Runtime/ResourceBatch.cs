using System;
using System.Collections.Generic;
using System.Threading;

namespace EasyResources
{
    /// <summary>Bounded FIFO of whole atomic operations. Never splits one transaction across frames.</summary>
    public sealed class ResourceBatch
    {
        private readonly ResourceTransaction[] queue;
        private readonly int ownerThread = Thread.CurrentThread.ManagedThreadId;
        private int start, count;
        private bool processing;
        public int Count => count;
        public int Capacity => queue.Length;

        public ResourceBatch(int capacity = 64)
        {
            if (capacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(capacity));
            queue = new ResourceTransaction[capacity];
        }

        public bool TryEnqueue(ResourceTransaction transaction)
        {
            CheckThread();
            if (transaction == null)
                throw new ArgumentNullException(nameof(transaction));
            if (count == queue.Length)
                return false;
            for (var i = 0; i < count; i++)
                if (ReferenceEquals(queue[(start + i) % queue.Length], transaction))
                    throw new ArgumentException("The same mutable transaction is already queued.");
            transaction.Claim(this);
            queue[(start + count) % queue.Length] = transaction;
            count++;
            return true;
        }

        /// <summary>Returns attempted count. Failures are returned in order; enqueueing during callbacks waits for the next call.</summary>
        public int Process(int maximumOperations, List<ResourceFailure> failures = null)
        {
            CheckThread();
            if (maximumOperations < 0)
                throw new ArgumentOutOfRangeException(nameof(maximumOperations));
            if (processing)
                throw new InvalidOperationException("Cannot reenter the batch processor.");
            failures?.Clear();
            var limit = Math.Min(count, maximumOperations);
            var attempted = 0;
            processing = true;
            try
            {
                for (; attempted < limit; attempted++)
                {
                    var item = queue[start];
                    queue[start] = null;
                    start = (start + 1) % queue.Length;
                    count--;
                    item.Release(this);
                    if (!item.TryExecute(out var failure))
                        failures?.Add(failure);
                }

                return attempted;
            }
            finally
            {
                processing = false;
            }
        }

        public void Clear()
        {
            CheckThread();
            if (processing)
                throw new InvalidOperationException("Cannot clear a running batch.");
            for (var i = 0; i < count; i++)
                queue[(start + i) % queue.Length].Release(this);
            Array.Clear(queue, 0, queue.Length);
            start = count = 0;
        }

        private void CheckThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != ownerThread)
                throw new InvalidOperationException("Use a resource batch on its creating Unity thread.");
        }
    }
}
