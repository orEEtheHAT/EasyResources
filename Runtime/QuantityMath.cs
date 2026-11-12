using System;
using System.Numerics;

namespace EasyResources
{
    public enum ResourceRounding
    {
        Exact,
        Down,
        Up,
        Nearest
    }

    /// <summary>Exact setup/migration arithmetic. Ordinary ledger operations do not use BigInteger.</summary>
    public static class QuantityMath
    {
        public static long Scale(long value, long numerator, long denominator = 1, ResourceRounding rounding = ResourceRounding.Exact)
        {
            if (value < 0 || numerator < 0 || denominator <= 0)
                throw new ArgumentOutOfRangeException("Use nonnegative amounts and a positive denominator.");
            if (rounding < ResourceRounding.Exact || rounding > ResourceRounding.Nearest)
                throw new ArgumentOutOfRangeException(nameof(rounding));
            var quotient = BigInteger.DivRem((BigInteger)value * numerator, denominator, out var remainder);
            if (!remainder.IsZero)
            {
                if (rounding == ResourceRounding.Exact)
                    throw new ArgumentException("Quantity is not exactly representable. Choose an explicit rounding policy.");
                if (rounding == ResourceRounding.Up || (rounding == ResourceRounding.Nearest && remainder * 2 >= denominator))
                    quotient++;
            }

            if (quotient > long.MaxValue)
                throw new OverflowException("Quantity exceeds Int64. Use LargeNumbers for idle-scale values.");
            return (long)quotient;
        }
    }
}
