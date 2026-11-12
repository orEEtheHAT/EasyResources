using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace EasyResources
{
    [AddComponentMenu("EasyResources/Resource Container")]
    [DisallowMultipleComponent]
    public sealed class ResourceContainer : MonoBehaviour
    {
        [SerializeField, Tooltip("Starting amounts. Each container gets its own runtime copy.")]
        private ResourceCost initialContents = new ResourceCost();
        [SerializeField]
        private ResourceStorageSettings storage = new ResourceStorageSettings();
        [SerializeField, Min(0)]
        private int historyCapacity;
        [SerializeField, Tooltip("Called after the complete transaction. Read balances here; queue further changes for later.")]
        private UnityEvent onChanged = new UnityEvent();
        private ResourceStore store;
        public UnityEvent OnChanged => onChanged;

        public event Action Changed
        {
            add => Store.Changed += value;
            remove
            {
                if (store != null)
                    store.Changed -= value;
            }
        }

        public int ActiveReservationCount => Store.ActiveReservationCount;

        /// <summary>The same runtime store used by all component methods. Do not dispose it manually.</summary>
        public ResourceStore Store
        {
            get
            {
                if (!Application.isPlaying)
                    throw new InvalidOperationException("Resource Container balances are available in Play Mode. Use ResourceStore for edit-time calculations.");
                if (this == null)
                    throw new ObjectDisposedException(nameof(ResourceContainer));
                if (store == null)
                {
                    store = new ResourceStore(initialContents, limits: storage.CreateLimits(), historyCapacity: historyCapacity);
                    store.Changed += NotifyInspectorEvent;
                }

                return store;
            }
        }

        private void Awake()
        {
            _ = Store;
        }

        private void OnDestroy()
        {
            store?.RequestDispose();
            store = null;
        }

        private void NotifyInspectorEvent() => onChanged.Invoke();
        public long GetTotal(ResourceDefinition resource) => Store.GetTotal(resource);
        public long GetReserved(ResourceDefinition resource) => Store.GetReserved(resource);
        public long GetAvailable(ResourceDefinition resource) => Store.GetAvailable(resource);
        public void Add(ResourceDefinition resource, long amount) => Store.Add(resource, amount);
        public bool TryAdd(ResourceDefinition resource, long amount, out ResourceFailure failure) => Store.TryAdd(resource, amount, out failure);
        public long AddUpTo(ResourceDefinition resource, long amount) => Store.AddUpTo(resource, amount);
        public bool CanAfford(ResourceCost cost) => Store.CanAfford(cost);
        public bool TrySpend(ResourceCost cost) => Store.TrySpend(cost);
        public bool TrySpend(ResourceCost cost, out ResourceFailure failure) => Store.TrySpend(cost, out failure);
        public bool CanAfford(ResourceCost cost, out ResourceFailure failure) => Store.CanAfford(cost, out failure);
        public bool TryTransferTo(ResourceContainer destination, ResourceCost cost, out ResourceFailure failure) => Store.TryTransferTo(destination.Store, cost, out failure);
        public bool TryExchange(ResourceCost payment, ResourceCost reward, out ResourceFailure failure) => Store.TryExchange(payment, reward, out failure);
        public bool TryReserve(ResourceCost cost, out ResourceReservation reservation) => Store.TryReserve(cost, out reservation);
        public bool TryReserve(ResourceCost cost, out ResourceReservation reservation, out ResourceFailure failure, ResourceReservationOptions options = default) => Store.TryReserve(cost, out reservation, out failure, options);
        public ResourceContainerState CaptureState(bool includeReservations = false, long nowMilliseconds = 0) => Store.CaptureState(includeReservations, nowMilliseconds);
        public int RestoreState(ResourceContainerState state, IEnumerable<ResourceDefinition> definitions, UnknownResourcePolicy unknownResourcePolicy = UnknownResourcePolicy.Reject) => Store.RestoreState(state, definitions, unknownResourcePolicy);
        public ResourceRestoreReport RestoreState(ResourceContainerState state, IEnumerable<ResourceDefinition> definitions, ResourceRestoreOptions options) => Store.RestoreState(state, definitions, options);
    }
}
