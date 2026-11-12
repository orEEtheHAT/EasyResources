using System;
using UnityEngine;

namespace EasyResources.Production
{
    [AddComponentMenu("EasyResources/Production/Resource Producer")]
    public sealed class ResourceProducer : MonoBehaviour
    {
        [SerializeField]
        private ResourceContainer container;
        [SerializeField]
        private ResourceRate[] rates = Array.Empty<ResourceRate>();
        private ResourceFlow flow;
        private long last;
        public ResourceFlow Flow => flow;

        private void OnEnable()
        {
            if (container == null)
                container = GetComponent<ResourceContainer>();
            if (container == null)
            {
                Debug.LogError("Resource Producer: assign a Resource Container or add one to this object.", this);
                enabled = false;
                return;
            }

            if (flow == null)
                flow = new ResourceFlow(container.Store, rates);
            last = (long)(Time.unscaledTimeAsDouble * 1000);
        }

        private void Update()
        {
            var now = (long)(Time.unscaledTimeAsDouble * 1000);
            flow.Tick(now - last);
            last = now;
        }
    }
}
