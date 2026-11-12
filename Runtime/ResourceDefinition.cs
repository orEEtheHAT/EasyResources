using System;
using System.Globalization;
using UnityEngine;

namespace EasyResources
{
    [CreateAssetMenu(menuName = "EasyResources/Resource", fileName = "New Resource")]
    public sealed class ResourceDefinition : DefinitionAsset
    {
        [SerializeField, Range(0, 9)]
        private int decimalPlaces;
        [SerializeField]
        private string unit = "";
        [SerializeField, Min(0), Tooltip("Weight of one smallest unit; use the same weight unit for all resources.")]
        private int weightPerUnit = 1;
        [SerializeField, Min(0), Tooltip("Volume of one smallest unit; use the same volume unit for all resources.")]
        private int volumePerUnit = 1;
        public int DecimalPlaces => decimalPlaces;
        public string Unit => unit;
        public int WeightPerUnit => weightPerUnit;
        public int VolumePerUnit => volumePerUnit;

        public override void ValidateDefinition()
        {
            base.ValidateDefinition();
            if (decimalPlaces < 0 || decimalPlaces > 9 || weightPerUnit < 0 || volumePerUnit < 0)
                throw new ArgumentException("Precision must be 0–9; weight and volume must be nonnegative.");
        }

        public long UnitsPerWhole
        {
            get
            {
                long value = 1;
                for (var i = 0; i < decimalPlaces; i++)
                    value *= 10;
                return value;
            }
        }

        public long ToUnits(decimal quantity)
        {
            var scaled = quantity * UnitsPerWhole;
            if (decimal.Truncate(scaled) != scaled)
                throw new ArgumentException($"{DisplayName} supports at most {decimalPlaces} decimal places.");
            return checked((long)scaled);
        }

        public decimal FromUnits(long amount) => (decimal)amount / UnitsPerWhole;
        public string Format(long amount, bool includeUnit = true)
        {
            var format = decimalPlaces == 0 ? "0" : "0." + new string ('#', decimalPlaces);
            var value = FromUnits(amount).ToString(format, CultureInfo.CurrentCulture);
            return includeUnit && !string.IsNullOrEmpty(unit) ? value + " " + unit : value;
        }

        public static ResourceDefinition Create(string displayName, string stableId = null, Sprite icon = null, int decimalPlaces = 0, int weightPerUnit = 1, int volumePerUnit = 1, string unit = "")
        {
            if (decimalPlaces < 0 || decimalPlaces > 9 || weightPerUnit < 0 || volumePerUnit < 0)
                throw new ArgumentOutOfRangeException("Invalid resource units.");
            var result = New<ResourceDefinition>(displayName, stableId, icon);
            result.decimalPlaces = decimalPlaces;
            result.weightPerUnit = weightPerUnit;
            result.volumePerUnit = volumePerUnit;
            result.unit = unit ?? "";
            return result;
        }
    }
}
