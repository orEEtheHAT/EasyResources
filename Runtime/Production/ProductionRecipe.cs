using System;
using UnityEngine;

namespace EasyResources.Production
{
    [CreateAssetMenu(menuName = "EasyResources/Production/Recipe", fileName = "New Recipe")]
    public sealed class ProductionRecipe : DefinitionAsset
    {
        [SerializeField]
        private ResourceCost inputs = new ResourceCost();
        [SerializeField]
        private ResourceCost outputs = new ResourceCost();
        [SerializeField, Min(0)]
        private long durationMilliseconds = 1000;
        [NonSerialized]
        private PreparedProduction single;
        public ResourceCost Inputs => inputs;
        public ResourceCost Outputs => outputs;
        public long DurationMilliseconds => durationMilliseconds;

        public override void ValidateDefinition()
        {
            base.ValidateDefinition();
            if (inputs == null || outputs == null || durationMilliseconds < 0)
                throw new ArgumentException("Assign recipe inputs/outputs and a nonnegative duration.");
            inputs.Prepare();
            outputs.Prepare();
        }

        public PreparedProduction Prepare(long quantity = 1)
        {
            if (quantity <= 0)
                throw new ArgumentOutOfRangeException(nameof(quantity));
            if (durationMilliseconds < 0 || inputs == null || outputs == null)
                throw new ArgumentException("Assign recipe costs and a nonnegative duration.");
            if (quantity == 1 && single != null)
                return single;
            var prepared = new PreparedProduction(this, quantity, inputs.Multiply(quantity), outputs.Multiply(quantity), checked(durationMilliseconds * quantity));
            if (quantity == 1)
                single = prepared;
            return prepared;
        }

        protected override void OnValidate()
        {
            base.OnValidate();
            single = null;
            inputs?.OnAfterDeserialize();
            outputs?.OnAfterDeserialize();
        }

        public static ProductionRecipe Create(string name, ResourceCost inputs, ResourceCost outputs, long durationMilliseconds = 1000, string stableId = null)
        {
            if (inputs == null || outputs == null || durationMilliseconds < 0)
                throw new ArgumentException("Invalid recipe.");
            var result = New<ProductionRecipe>(name, stableId);
            result.inputs = inputs;
            result.outputs = outputs;
            result.durationMilliseconds = durationMilliseconds;
            return result;
        }
    }

    public sealed class PreparedProduction
    {
        public ProductionRecipe Recipe { get; }
        public long Quantity { get; }
        public ResourceCost Inputs { get; }
        public ResourceCost Outputs { get; }
        public long DurationMilliseconds { get; }

        internal PreparedProduction(ProductionRecipe recipe, long quantity, ResourceCost inputs, ResourceCost outputs, long duration)
        {
            Recipe = recipe;
            Quantity = quantity;
            Inputs = inputs;
            Outputs = outputs;
            DurationMilliseconds = duration;
        }
    }

    public readonly struct ProductionTicket
    {
        private readonly ProductionQueue owner;
        public long Id { get; }

        internal ProductionTicket(ProductionQueue owner, long id)
        {
            this.owner = owner;
            Id = id;
        }

        public bool IsActive => owner != null && owner.Contains(Id);

        public bool Cancel() => owner != null && owner.Cancel(Id);
    }

    public readonly struct ProductionJobInfo
    {
        public long Id { get; }
        public ProductionRecipe Recipe { get; }
        public long Quantity { get; }
        public long RemainingMilliseconds { get; }

        internal ProductionJobInfo(long id, PreparedProduction plan, long remaining)
        {
            Id = id;
            Recipe = plan.Recipe;
            Quantity = plan.Quantity;
            RemainingMilliseconds = remaining;
        }
    }
}
