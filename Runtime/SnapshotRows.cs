using System;
using System.Collections.Generic;

namespace EasyResources
{
    internal static class SnapshotRows
    {
        internal static List<ResourceStateEntry> Capture(ResourceAmount[] rows)
        {
            var result = new List<ResourceStateEntry>(rows.Length);
            foreach (var row in rows)
                result.Add(new ResourceStateEntry(row.Resource.Id, row.Amount, row.Resource.DecimalPlaces));
            return result;
        }

        internal static ResourceCost Restore(List<ResourceStateEntry> rows, DefinitionLookup<ResourceDefinition> definitions)
        {
            if (rows == null)
                throw new ArgumentException("Saved cost is null.");
            var result = new ResourceAmount[rows.Count];
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (string.IsNullOrWhiteSpace(row.ResourceId) || row.Amount < 0 || !seen.Add(row.ResourceId))
                    throw new ArgumentException("Malformed saved cost.");
                var definition = definitions.Get(row.ResourceId);
                if (definition.DecimalPlaces != row.DecimalPlaces)
                    throw new ArgumentException("Migrate saved quantity precision before loading this operation.");
                result[i] = new ResourceAmount(definition, row.Amount);
            }

            return new ResourceCost(result);
        }
    }
}
