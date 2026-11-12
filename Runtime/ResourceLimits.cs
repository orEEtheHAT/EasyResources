using System;
using System.Collections.Generic;
using UnityEngine;

namespace EasyResources
{
    [Serializable]
    public struct ResourceLimit
    {
        [SerializeField]
        private ResourceDefinition resource;
        [SerializeField, Min(0)]
        private long maximum;
        public ResourceDefinition Resource => resource;
        public long Maximum => maximum;

        public ResourceLimit(ResourceDefinition resource, long maximum)
        {
            if (resource == null)
                throw new ArgumentNullException(nameof(resource));
            if (maximum < 0)
                throw new ArgumentOutOfRangeException(nameof(maximum));
            this.resource = resource;
            this.maximum = maximum;
        }
    }

    [Serializable]
    public sealed class ResourceStorageSettings
    {
        [SerializeField]
        private bool restrictResources;
        [SerializeField]
        private ResourceDefinition[] acceptedResources = Array.Empty<ResourceDefinition>();
        [SerializeField]
        private ResourceLimit[] resourceLimits = Array.Empty<ResourceLimit>();
        [SerializeField]
        private bool limitWeight;
        [SerializeField, Min(0)]
        private long maximumWeight = 100;
        [SerializeField]
        private bool limitVolume;
        [SerializeField, Min(0)]
        private long maximumVolume = 100;
        public ResourceLimits CreateLimits() => new ResourceLimits(resourceLimits, restrictResources ? acceptedResources : null, limitWeight ? (long? )maximumWeight : null, limitVolume ? (long? )maximumVolume : null);
    }

    public sealed class ResourceLimits
    {
        public static readonly ResourceLimits Unlimited = new ResourceLimits();
        private readonly Dictionary<string, long> maximums;
        private readonly HashSet<string> accepted;
        public long? MaximumWeight { get; }
        public long? MaximumVolume { get; }
        public bool HasResourceFilter => accepted != null;

        public ResourceLimits(IEnumerable<ResourceLimit> limits = null, IEnumerable<ResourceDefinition> acceptedResources = null, long? maximumWeight = null, long? maximumVolume = null)
        {
            if (maximumWeight < 0 || maximumVolume < 0)
                throw new ArgumentOutOfRangeException("Capacity must be nonnegative.");
            MaximumWeight = maximumWeight;
            MaximumVolume = maximumVolume;
            maximums = new Dictionary<string, long>(StringComparer.Ordinal);
            if (limits != null)
                foreach (var limit in limits)
                {
                    if (limit.Resource == null || limit.Maximum < 0)
                        throw new ArgumentException("Assign a resource and a nonnegative maximum.");
                    if (maximums.ContainsKey(limit.Resource.Id))
                        throw new ArgumentException("A resource limit is listed twice.");
                    maximums.Add(limit.Resource.Id, limit.Maximum);
                }

            if (acceptedResources == null)
                return;
            accepted = new HashSet<string>(StringComparer.Ordinal);
            foreach (var resource in acceptedResources)
            {
                if (resource == null)
                    throw new ArgumentException("Accepted Resources has an empty reference.");
                accepted.Add(resource.Id);
            }
        }

        public bool Accepts(ResourceDefinition resource) => accepted == null || accepted.Contains(resource.Id);
        public long GetMaximum(ResourceDefinition resource) => maximums.TryGetValue(resource.Id, out var value) ? value : long.MaxValue;
    }
}
