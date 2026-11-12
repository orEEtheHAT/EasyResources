using System;
using System.Collections.Generic;

namespace EasyResources
{
    public readonly struct ResourceIdMigration
    {
        public string OldId { get; }
        public ResourceDefinition Target { get; }
        public long Numerator { get; }
        public long Denominator { get; }

        public ResourceIdMigration(string oldId, ResourceDefinition target, long numerator = 1, long denominator = 1)
        {
            if (string.IsNullOrWhiteSpace(oldId) || target == null || numerator < 0 || denominator <= 0)
                throw new ArgumentException("Invalid resource migration.");
            OldId = oldId;
            Target = target;
            Numerator = numerator;
            Denominator = denominator;
        }
    }

    public static class ResourceStateMigration
    {
        /// <summary>Returns a new snapshot. Alias amounts merge; precision conversion and rounding are explicit.</summary>
        public static ResourceContainerState Migrate(ResourceContainerState source, IEnumerable<ResourceDefinition> definitions, IEnumerable<ResourceIdMigration> aliases = null, ResourceRounding rounding = ResourceRounding.Exact)
        {
            if (source == null || (source.Version != 1 && source.Version != 2) || source.Resources == null)
                throw new ArgumentException("Unsupported or malformed source snapshot.");
            if (source.Revision < 0 || (source.Version == 1 && source.Reservations != null && source.Reservations.Count != 0))
                throw new ArgumentException("Malformed source snapshot metadata.");
            if (rounding < ResourceRounding.Exact || rounding > ResourceRounding.Nearest)
                throw new ArgumentOutOfRangeException(nameof(rounding));
            var catalog = new DefinitionLookup<ResourceDefinition>(definitions);
            var map = new Dictionary<string, ResourceIdMigration>(StringComparer.Ordinal);
            if (aliases != null)
                foreach (var alias in aliases)
                {
                    if (map.ContainsKey(alias.OldId))
                        throw new ArgumentException("Migration ID is listed twice.");
                    map.Add(alias.OldId, alias);
                }

            var result = new ResourceContainerState
            {
                Revision = source.Revision
            };
            result.Resources = ConvertRows(source.Resources, catalog, map, rounding);
            if (source.Reservations != null)
                foreach (var hold in source.Reservations)
                {
                    if (hold == null)
                        throw new ArgumentException("Null saved reservation.");
                    result.Reservations.Add(new ResourceReservationState { Id = hold.Id, OwnerId = hold.OwnerId, AgeMilliseconds = hold.AgeMilliseconds, HasExpiry = hold.HasExpiry, RemainingMilliseconds = hold.RemainingMilliseconds, Resources = ConvertRows(hold.Resources, catalog, map, rounding) });
                }

            return result;
        }

        /// <summary>Apply the same ID/precision conversion to frozen recipe, delivery, research or price rows.</summary>
        public static List<ResourceStateEntry> MigrateRows(IEnumerable<ResourceStateEntry> source, IEnumerable<ResourceDefinition> definitions, IEnumerable<ResourceIdMigration> aliases = null, ResourceRounding rounding = ResourceRounding.Exact)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));
            return Migrate(new ResourceContainerState { Resources = new List<ResourceStateEntry>(source) }, definitions, aliases, rounding).Resources;
        }

        private static List<ResourceStateEntry> ConvertRows(List<ResourceStateEntry> rows, DefinitionLookup<ResourceDefinition> catalog, Dictionary<string, ResourceIdMigration> map, ResourceRounding rounding)
        {
            if (rows == null)
                throw new ArgumentException("Saved rows are null.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var totals = new Dictionary<string, ResourceStateEntry>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                if (string.IsNullOrWhiteSpace(row.ResourceId) || !seen.Add(row.ResourceId) || row.Amount < 0 || row.DecimalPlaces < 0 || row.DecimalPlaces > 9)
                    throw new ArgumentException("Malformed or duplicate source row.");
                var hasAlias = map.TryGetValue(row.ResourceId, out var alias);
                var target = hasAlias ? alias.Target : catalog.Get(row.ResourceId);
                if (!ReferenceEquals(catalog.Get(target.Id), target))
                    throw new ArgumentException("Migration target conflicts with the catalog.");
                long oldScale = 1;
                for (var i = 0; i < row.DecimalPlaces; i++)
                    oldScale *= 10;
                // Use one rational operation so intermediate rounding cannot lose quantities.
                var numerator = new System.Numerics.BigInteger(target.UnitsPerWhole) * (hasAlias ? alias.Numerator : 1);
                var denominator = new System.Numerics.BigInteger(oldScale) * (hasAlias ? alias.Denominator : 1);
                var value = System.Numerics.BigInteger.DivRem((System.Numerics.BigInteger)row.Amount * numerator, denominator, out var remainder);
                if (!remainder.IsZero)
                {
                    if (rounding == ResourceRounding.Exact)
                        throw new ArgumentException("Migration loses precision; choose explicit rounding.");
                    if (rounding == ResourceRounding.Up || (rounding == ResourceRounding.Nearest && remainder * 2 >= denominator))
                        value++;
                }

                if (value > long.MaxValue)
                    throw new OverflowException();
                totals.TryGetValue(target.Id, out var previous);
                totals[target.Id] = new ResourceStateEntry(target.Id, checked(previous.Amount + (long)value), target.DecimalPlaces);
            }

            var result = new List<ResourceStateEntry>(totals.Values);
            result.Sort((a, b) => StringComparer.Ordinal.Compare(a.ResourceId, b.ResourceId));
            return result;
        }
    }
}
