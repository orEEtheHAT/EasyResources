using System;
using UnityEngine;
using UnityEngine.Events;

namespace EasyResources.Inventory
{
    [AddComponentMenu("EasyResources/Inventory/Inventory Container"), DisallowMultipleComponent]
    public sealed class InventoryContainer : MonoBehaviour
    {
        [SerializeField, Min(1)]
        private int slots = 24;
        [SerializeField]
        private ItemAmount[] initialContents = Array.Empty<ItemAmount>();
        [SerializeField]
        private bool limitWeight;
        [SerializeField, Min(0)]
        private long maximumWeight = 100;
        [SerializeField]
        private bool limitVolume;
        [SerializeField, Min(0)]
        private long maximumVolume = 100;
        [SerializeField]
        private UnityEvent onChanged = new UnityEvent();
        private InventoryStore store;
        public UnityEvent OnChanged => onChanged;

        public InventoryStore Store
        {
            get
            {
                if (!Application.isPlaying || this == null)
                    throw new InvalidOperationException("Inventory Container is available during Play Mode.");
                if (store != null)
                    return store;
                var candidate = new InventoryStore(slots, limitWeight ? (long? )maximumWeight : null, limitVolume ? (long? )maximumVolume : null);
                try
                {
                    foreach (var row in initialContents)
                        if (!candidate.TryAdd(row.Item, row.Count, out var failure))
                            throw new ArgumentException("Initial Contents: " + failure);
                }
                catch
                {
                    candidate.Dispose();
                    throw;
                }

                store = candidate;
                store.Changed += Notify;
                return store;
            }
        }

        private void Awake()
        {
            _ = Store;
        }

        private void Notify() => onChanged.Invoke();
        private void OnDestroy()
        {
            store?.RequestDispose();
            store = null;
        }
    }
}
