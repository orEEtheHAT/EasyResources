using System;
using System.Collections.Generic;
using UnityEngine;

namespace EasyResources.Production
{
    [AddComponentMenu("EasyResources/Production/Production Station")]
    public sealed class ProductionStation : MonoBehaviour
    {
        [SerializeField]
        private ResourceContainer input;
        [SerializeField, Tooltip("Optional; leave empty to use Input for outputs too.")]
        private ResourceContainer output;
        [SerializeField, Min(1)]
        private int queueCapacity = 8;
        [SerializeField]
        private bool cancelWhenDisabled;
        private ProductionQueue queue;
        public ProductionQueue Queue
        {
            get
            {
                if (queue != null)
                    return queue;
                if (input == null)
                    input = GetComponent<ResourceContainer>();
                if (input == null)
                    throw new InvalidOperationException("Production Station: assign Input or add a Resource Container to this object.");
                return queue = new ProductionQueue(input.Store, output == null ? input.Store : output.Store, queueCapacity);
            }
        }

        public bool TryEnqueue(ProductionRecipe recipe, long quantity = 1) => Queue.TryEnqueue(recipe, (long)(Time.unscaledTimeAsDouble * 1000), out _, quantity);
        public ProductionQueueState CaptureState() => Queue.CaptureState((long)(Time.unscaledTimeAsDouble * 1000));
        /// <summary>Restore resource containers first, then reconnect this station to its named reservations.</summary>
        public void RestoreState(ProductionQueueState state, IEnumerable<ProductionRecipe> recipes, IEnumerable<ResourceDefinition> resources, long offlineMilliseconds = 0)
        {
            var previous = Queue;
            previous.ValidateReplacement();
            var replacement = ProductionQueue.FromState(input.Store, output == null ? input.Store : output.Store, state, recipes, resources, (long)(Time.unscaledTimeAsDouble * 1000), queueCapacity, offlineMilliseconds);
            queue = replacement;
            previous.Dispose();
        }

        private void Update()
        {
            if (queue != null)
                queue.Tick((long)(Time.unscaledTimeAsDouble * 1000));
        }

        private void OnDisable()
        {
            if (cancelWhenDisabled && queue != null)
            {
                queue.RequestDispose();
                queue = null;
            }
        }

        private void OnDestroy()
        {
            queue?.RequestDispose();
            queue = null;
        }
    }
}
