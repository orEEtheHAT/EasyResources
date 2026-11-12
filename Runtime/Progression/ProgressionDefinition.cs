using System;
using UnityEngine;

namespace EasyResources.Progression
{
    public enum ProgressionModifierKind
    {
        Add,
        PercentBasisPoints
    }

    [Serializable]
    public struct ProgressionModifier
    {
        public string Stat;
        public ProgressionModifierKind Kind;
        public long Value;
        public ProgressionModifier(string stat, long value, ProgressionModifierKind kind = ProgressionModifierKind.Add)
        {
            Stat = stat;
            Value = value;
            Kind = kind;
        }
    }

    [Serializable]
    public struct ProgressionRequirement
    {
        public ProgressionNode Node;
        [Min(1)]
        public int Rank;
        public ProgressionRequirement(ProgressionNode node, int rank = 1)
        {
            Node = node;
            Rank = rank;
        }
    }

    [Serializable]
    public sealed class ProgressionRank
    {
        public ResourceCost Cost = new ResourceCost();
        public ProgressionModifier[] Modifiers = Array.Empty<ProgressionModifier>();
        public ProgressionRank()
        {
        }

        public ProgressionRank(ResourceCost cost, params ProgressionModifier[] modifiers)
        {
            Cost = cost ?? throw new ArgumentNullException(nameof(cost));
            Modifiers = modifiers ?? Array.Empty<ProgressionModifier>();
        }
    }

    [CreateAssetMenu(menuName = "EasyResources/Progression/Definition", fileName = "Progression")]
    public sealed class ProgressionDefinition : DefinitionAsset
    {
        [Tooltip("Total XP required for levels 2, 3, etc. Strictly increasing positive values.")]
        [SerializeField]
        private long[] levelThresholds =
        {
            100,
            300,
            600
        };
        [SerializeField]
        private ProgressionNode[] nodes = Array.Empty<ProgressionNode>();
        internal long[] Thresholds => levelThresholds;
        internal ProgressionNode[] Nodes => nodes;
        public int MaximumLevel => levelThresholds.Length + 1;

        public override void ValidateDefinition()
        {
            base.ValidateDefinition();
            if (levelThresholds == null || nodes == null)
                throw new ArgumentException("Level thresholds and nodes cannot be null.");
            foreach (var node in nodes)
            {
                if (node == null)
                    throw new ArgumentException("Assign every progression node.");
                node.ValidateDefinition();
            }

            using (var wallet = new ResourceStore())
            using (var profile = new ProgressionProfile(this, wallet))
            {
            }
        }

        public static ProgressionDefinition Create(string name, long[] levelThresholds, ProgressionNode[] nodes = null, string id = null)
        {
            if (levelThresholds == null)
                throw new ArgumentNullException(nameof(levelThresholds));
            var asset = New<ProgressionDefinition>(name, id, null);
            asset.levelThresholds = (long[])levelThresholds.Clone();
            asset.nodes = nodes == null ? Array.Empty<ProgressionNode>() : (ProgressionNode[])nodes.Clone();
            return asset;
        }
    }

    public readonly struct PrestigeRule
    {
        public int MinimumLevel { get; }
        public ResourceCost Cost { get; }
        public ResourceCost Reward { get; }

        public PrestigeRule(int minimumLevel, ResourceCost reward, ResourceCost cost = null)
        {
            if (minimumLevel < 1)
                throw new ArgumentOutOfRangeException(nameof(minimumLevel));
            MinimumLevel = minimumLevel;
            Reward = reward ?? throw new ArgumentNullException(nameof(reward));
            Cost = cost ?? new ResourceCost();
        }
    }
}
