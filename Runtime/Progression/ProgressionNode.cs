using System;
using UnityEngine;

namespace EasyResources.Progression
{
    [CreateAssetMenu(menuName = "EasyResources/Progression/Node", fileName = "ProgressionNode")]
    public sealed class ProgressionNode : DefinitionAsset
    {
        [SerializeField, Min(1)]
        private int minimumLevel = 1;
        [SerializeField]
        private string exclusiveGroup;
        [SerializeField]
        private bool keepOnPrestige;
        [SerializeField, Min(0)]
        private long researchMilliseconds;
        [SerializeField]
        private ProgressionRequirement[] requirements = Array.Empty<ProgressionRequirement>();
        [SerializeField]
        private ProgressionRank[] ranks =
        {
            new ProgressionRank()
        };
        public int MinimumLevel => minimumLevel;
        public string ExclusiveGroup => exclusiveGroup;
        public bool KeepOnPrestige => keepOnPrestige;
        public long ResearchMilliseconds => researchMilliseconds;
        public int MaximumRank => ranks.Length;
        internal ProgressionRequirement[] Requirements => requirements;
        internal ProgressionRank[] Ranks => ranks;

        public override void ValidateDefinition()
        {
            base.ValidateDefinition();
            if (minimumLevel < 1 || researchMilliseconds < 0 || ranks == null || ranks.Length == 0 || requirements == null)
                throw new ArgumentException("Configure at least one rank, a positive level and a nonnegative duration.");
            foreach (var rank in ranks)
            {
                if (rank == null || rank.Cost == null || rank.Modifiers == null)
                    throw new ArgumentException("Configure every rank's cost and modifiers.");
                rank.Cost.Prepare();
                foreach (var modifier in rank.Modifiers)
                    if (string.IsNullOrWhiteSpace(modifier.Stat) || !Enum.IsDefined(typeof(ProgressionModifierKind), modifier.Kind))
                        throw new ArgumentException("Every modifier needs a stat key and a valid operation.");
            }

            foreach (var requirement in requirements)
                if (requirement.Node == null || requirement.Rank < 1 || requirement.Rank > requirement.Node.MaximumRank)
                    throw new ArgumentException("Assign each prerequisite and an existing positive rank.");
        }

        protected override void OnValidate()
        {
            base.OnValidate();
            if (ranks != null)
                foreach (var rank in ranks)
                    rank?.Cost?.OnAfterDeserialize();
        }

        public static ProgressionNode Create(string name, ProgressionRank[] ranks, ProgressionRequirement[] requirements = null, int minimumLevel = 1, long researchMilliseconds = 0, string exclusiveGroup = null, bool keepOnPrestige = false, string id = null)
        {
            if (ranks == null || ranks.Length == 0 || minimumLevel < 1 || researchMilliseconds < 0)
                throw new ArgumentException("Invalid progression node.");
            var asset = New<ProgressionNode>(name, id, null);
            asset.ranks = (ProgressionRank[])ranks.Clone();
            asset.requirements = requirements == null ? Array.Empty<ProgressionRequirement>() : (ProgressionRequirement[])requirements.Clone();
            asset.minimumLevel = minimumLevel;
            asset.researchMilliseconds = researchMilliseconds;
            asset.exclusiveGroup = exclusiveGroup;
            asset.keepOnPrestige = keepOnPrestige;
            return asset;
        }
    }
}
