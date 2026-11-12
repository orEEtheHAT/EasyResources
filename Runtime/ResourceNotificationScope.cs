using System;

namespace EasyResources
{
    /// <summary>
    /// A short, synchronous notification boundary. Writes remain real; explicitly commit or cancel
    /// reservations before disposing. Copies close the same boundary only once. Do not keep across frames/await.
    /// </summary>
    public readonly struct ResourceNotificationScope : IDisposable
    {
        private readonly ResourceStore store;
        private readonly long id;
        private readonly long generation;

        internal ResourceNotificationScope(ResourceStore store, long id, long generation)
        {
            this.store = store;
            this.id = id;
            this.generation = generation;
        }

        public bool IsActive => store != null && store.IsNotificationScopeActive(id, generation);
        public void Dispose() => store?.CloseNotificationScope(id, generation);
    }
}
