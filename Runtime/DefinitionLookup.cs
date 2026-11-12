using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace EasyResources
{
    public sealed class DefinitionLookup<T>
        where T : DefinitionAsset
    {
        private readonly Dictionary<string, T> definitions = new Dictionary<string, T>(StringComparer.Ordinal);
        public DefinitionLookup(IEnumerable<T> assets)
        {
            if (assets == null)
                throw new ArgumentNullException(nameof(assets));
            foreach (var asset in assets)
            {
                if (asset == null || string.IsNullOrWhiteSpace(asset.Id))
                    throw new ArgumentException("A definition or its ID is missing.");
                if (definitions.TryGetValue(asset.Id, out var old) && !ReferenceEquals(old, asset))
                    throw new ArgumentException($"Duplicate definition ID '{asset.Id}'.");
                definitions[asset.Id] = asset;
            }
        }

        public bool TryGet(string id, out T definition) => definitions.TryGetValue(id, out definition);
        public T Get(string id) => definitions.TryGetValue(id, out var result) ? result : throw new ArgumentException($"Unknown definition ID '{id}'.");
    }

    internal sealed class ReferenceComparer<T> : IEqualityComparer<T> where T : class
    {
        internal static readonly ReferenceComparer<T> Instance = new ReferenceComparer<T>();
        public bool Equals(T a, T b) => ReferenceEquals(a, b);
        public int GetHashCode(T value) => RuntimeHelpers.GetHashCode(value);
    }
}
