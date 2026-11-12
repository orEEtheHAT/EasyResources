using System;
using System.Collections.Generic;
using System.Threading;

namespace EasyResources.Progression
{
    /// <summary>XP, ranks and one timed research operation. Definitions are frozen at construction.</summary>
    public sealed class ProgressionProfile : IDisposable, IResourceParticipant
    {
        private sealed class Node
        {
            internal ProgressionNode Asset;
            internal int MinimumLevel;
            internal string Group;
            internal bool Keep;
            internal long Duration;
            internal ProgressionRequirement[] Requirements;
            internal ResourceCost[] Costs;
            internal ProgressionModifier[][] Modifiers;
        }

        private readonly ProgressionDefinition definition;
        private readonly ResourceStore wallet;
        private readonly long[] thresholds;
        private readonly Node[] nodes;
        private readonly Dictionary<ProgressionNode, int> indices = new Dictionary<ProgressionNode, int>(ReferenceComparer<ProgressionNode>.Instance);
        private readonly Dictionary<string, int> ids = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly ResourceTransaction transaction = new ResourceTransaction();
        private readonly SafeSignal changed = new SafeSignal();
        private readonly int thread = Thread.CurrentThread.ManagedThreadId;
        private int[] ranks, nextRanks;
        private long experience, prestige, nextExperience, nextPrestige;
        private ResourceReservation researchPayment;
        private ResourceCost researchCost;
        private int researchIndex = -1;
        private long researchDue;
        private string researchOwner;
        private bool busy, disposed, disposeRequested, clearResearch;
        public long Experience
        {
            get
            {
                CheckAlive();
                return experience;
            }
        }

        public int Level
        {
            get
            {
                CheckAlive();
                return LevelFor(experience);
            }
        }

        public long PrestigeCount
        {
            get
            {
                CheckAlive();
                return prestige;
            }
        }

        public ProgressionNode Researching => researchIndex < 0 ? null : nodes[researchIndex].Asset;
        public long ResearchDueMilliseconds => researchDue;

        public event Action Changed
        {
            add
            {
                CheckAlive();
                changed.Add(value);
            }

            remove
            {
                CheckThread();
                changed.Remove(value);
            }
        }

        public ProgressionProfile(ProgressionDefinition definition, ResourceStore wallet)
        {
            this.definition = definition != null ? definition : throw new ArgumentNullException(nameof(definition));
            this.wallet = wallet ?? throw new ArgumentNullException(nameof(wallet));
            if (wallet.IsReadOnly)
                throw new ArgumentException("Progression is updated on the authority.");
            if (definition.Thresholds == null || definition.Nodes == null)
                throw new ArgumentException("Progression thresholds and nodes cannot be null.");
            thresholds = (long[])definition.Thresholds.Clone();
            long previous = 0;
            foreach (var threshold in thresholds)
            {
                if (threshold <= previous)
                    throw new ArgumentException("Level thresholds must be positive and strictly increasing.");
                previous = threshold;
            }

            nodes = new Node[definition.Nodes.Length];
            ranks = new int[nodes.Length];
            nextRanks = new int[nodes.Length];
            for (var i = 0; i < nodes.Length; i++)
            {
                var asset = definition.Nodes[i];
                if (asset != null)
                    asset.ValidateDefinition();
                if (asset == null || string.IsNullOrWhiteSpace(asset.Id) || ids.ContainsKey(asset.Id) || asset.MaximumRank == 0 || asset.MinimumLevel < 1 || asset.MinimumLevel > thresholds.Length + 1 || asset.ResearchMilliseconds < 0)
                    throw new ArgumentException("Invalid or duplicate progression node, level or duration.");
                indices.Add(asset, i);
                ids.Add(asset.Id, i);
                var node = new Node
                {
                    Asset = asset,
                    MinimumLevel = asset.MinimumLevel,
                    Group = asset.ExclusiveGroup,
                    Keep = asset.KeepOnPrestige,
                    Duration = asset.ResearchMilliseconds,
                    Requirements = (ProgressionRequirement[])asset.Requirements.Clone(),
                    Costs = new ResourceCost[asset.MaximumRank],
                    Modifiers = new ProgressionModifier[asset.MaximumRank][]
                };
                for (var r = 0; r < node.Costs.Length; r++)
                {
                    var rank = asset.Ranks[r];
                    if (rank == null || rank.Cost == null || rank.Modifiers == null)
                        throw new ArgumentException("A rank is not configured.");
                    node.Costs[r] = new ResourceCost(rank.Cost.GetPrepared());
                    node.Modifiers[r] = (ProgressionModifier[])rank.Modifiers.Clone();
                    foreach (var modifier in node.Modifiers[r])
                        if (string.IsNullOrWhiteSpace(modifier.Stat) || !Enum.IsDefined(typeof(ProgressionModifierKind), modifier.Kind))
                            throw new ArgumentException("Invalid stat modifier.");
                }

                nodes[i] = node;
            }

            var visits = new byte[nodes.Length];
            for (var i = 0; i < nodes.Length; i++)
                ValidateGraph(i, visits);
        }

        private void ValidateGraph(int index, byte[] visits)
        {
            if (visits[index] == 2)
                return;
            if (visits[index] == 1)
                throw new ArgumentException("Progression prerequisites contain a cycle.");
            visits[index] = 1;
            foreach (var requirement in nodes[index].Requirements)
            {
                if (requirement.Node == null || !indices.TryGetValue(requirement.Node, out var parent) || requirement.Rank < 1 || requirement.Rank > nodes[parent].Costs.Length)
                    throw new ArgumentException("Unknown prerequisite or invalid rank.");
                ValidateGraph(parent, visits);
            }

            visits[index] = 2;
        }

        private int LevelFor(long xp)
        {
            var low = 0;
            var high = thresholds.Length;
            while (low < high)
            {
                var middle = low + (high - low) / 2;
                if (xp >= thresholds[middle])
                    low = middle + 1;
                else
                    high = middle;
            }

            return low + 1;
        }

        private int Index(ProgressionNode node)
        {
            CheckAlive();
            if (node == null || !indices.TryGetValue(node, out var index))
                throw new ArgumentException("Node does not belong to this progression.");
            return index;
        }

        public int GetRank(ProgressionNode node) => ranks[Index(node)];
        public long ExperienceToNextLevel
        {
            get
            {
                var level = Level;
                return level > thresholds.Length ? 0 : thresholds[level - 1] - experience;
            }
        }

        public decimal GetStat(string stat, decimal baseValue)
        {
            CheckAlive();
            if (string.IsNullOrWhiteSpace(stat))
                throw new ArgumentException("Stat key is blank.");
            decimal add = 0, percent = 0;
            for (var i = 0; i < nodes.Length; i++)
                for (var rank = 0; rank < ranks[i]; rank++)
                    foreach (var modifier in nodes[i].Modifiers[rank])
                        if (modifier.Stat == stat)
                        {
                            if (modifier.Kind == ProgressionModifierKind.Add)
                                add += modifier.Value;
                            else
                                percent += modifier.Value;
                        }

            return (baseValue + add) * (1 + percent / 10000m);
        }

        public bool AddExperience(long amount, out ResourceFailure failure)
        {
            CheckMutation();
            if (amount < 0)
                throw new ArgumentOutOfRangeException(nameof(amount));
            Begin();
            try
            {
                nextExperience = checked(experience + amount);
            }
            catch (OverflowException)
            {
                failure = new ResourceFailure(ResourceFailureCode.AmountOverflow);
                return false;
            }

            return transaction.Clear().Join(this).TryExecute(out failure);
        }

        public bool CanUnlock(ProgressionNode node, out ResourceFailure failure)
        {
            var index = Index(node);
            if (researchIndex >= 0)
            {
                failure = Rejected("Finish or cancel the current research first.");
                return false;
            }

            return RequirementsMet(index, out failure) && wallet.CanAfford(nodes[index].Costs[ranks[index]], out failure);
        }

        private bool RequirementsMet(int index, out ResourceFailure failure)
        {
            var node = nodes[index];
            if (ranks[index] >= node.Costs.Length)
            {
                failure = Rejected("The node is already at its maximum rank.");
                return false;
            }

            if (LevelFor(experience) < node.MinimumLevel)
            {
                failure = Rejected("The required level has not been reached.");
                return false;
            }

            foreach (var requirement in node.Requirements)
                if (ranks[indices[requirement.Node]] < requirement.Rank)
                {
                    failure = Rejected("A prerequisite rank is missing.");
                    return false;
                }

            if (!string.IsNullOrEmpty(node.Group))
                for (var i = 0; i < nodes.Length; i++)
                    if (i != index && ranks[i] > 0 && nodes[i].Group == node.Group)
                    {
                        failure = Rejected("An exclusive branch is already selected.");
                        return false;
                    }

            failure = default;
            return true;
        }

        public bool TryUnlock(ProgressionNode node, out ResourceFailure failure)
        {
            CheckMutation();
            var index = Index(node);
            if (nodes[index].Duration > 0)
            {
                failure = Rejected("This node requires timed research. Use TryStartResearch.");
                return false;
            }

            if (!CanUnlock(node, out failure))
                return false;
            Begin();
            nextRanks[index]++;
            return transaction.Clear().Spend(wallet, nodes[index].Costs[ranks[index]]).Join(this).TryExecute(out failure, new ResourceOperationContext("unlock", node.Id));
        }

        public bool TryStartResearch(ProgressionNode node, long nowMilliseconds, string ownerId, out ResourceFailure failure)
        {
            CheckMutation();
            if (nowMilliseconds < 0 || string.IsNullOrWhiteSpace(ownerId))
                throw new ArgumentException("Supply a nonnegative clock and persistent research owner ID.");
            var index = Index(node);
            if (!CanUnlock(node, out failure))
                return false;
            var due = checked(nowMilliseconds + nodes[index].Duration);
            busy = true;
            researchIndex = index;
            researchDue = due;
            researchOwner = ownerId;
            researchCost = nodes[index].Costs[ranks[index]];
            try
            {
                if (!wallet.TryReserve(researchCost, out researchPayment, out failure, new ResourceReservationOptions(ownerId, nowMilliseconds: nowMilliseconds)))
                {
                    ClearResearch();
                    return false;
                }

                changed.Emit();
                return true;
            }
            catch
            {
                ClearResearch();
                throw;
            }
            finally
            {
                busy = false;
                FinishDispose();
            }
        }

        public bool TryCompleteResearch(long nowMilliseconds, out ResourceFailure failure)
        {
            CheckMutation();
            if (nowMilliseconds < 0)
                throw new ArgumentOutOfRangeException(nameof(nowMilliseconds));
            if (researchIndex < 0 || !researchPayment.IsActive)
            {
                failure = new ResourceFailure(ResourceFailureCode.ReservationNotActive);
                return false;
            }

            if (nowMilliseconds < researchDue)
            {
                failure = Rejected("Research has not finished yet.");
                return false;
            }

            if (!RequirementsMet(researchIndex, out failure))
                return false;
            if (!Matches(researchPayment, researchCost))
            {
                failure = Rejected("The research reservation was changed externally.");
                return false;
            }

            Begin();
            nextRanks[researchIndex]++;
            clearResearch = true;
            return transaction.Clear().Commit(researchPayment).Join(this).TryExecute(out failure, new ResourceOperationContext("research", researchOwner, nowMilliseconds));
        }

        public bool CancelResearch()
        {
            CheckMutation();
            if (researchIndex < 0)
                return false;
            Begin();
            clearResearch = true;
            transaction.Clear();
            if (researchPayment.IsActive)
                transaction.Cancel(researchPayment);
            return transaction.Join(this).TryExecute();
        }

        public bool TryPrestige(PrestigeRule rule, out ResourceFailure failure)
        {
            CheckMutation();
            if (rule.Cost == null || rule.Reward == null)
                throw new ArgumentException("Configure a prestige rule.");
            if (researchIndex >= 0 || Level < rule.MinimumLevel)
            {
                failure = Rejected("Finish research and reach the required level before prestige.");
                return false;
            }

            if (prestige == long.MaxValue)
            {
                failure = new ResourceFailure(ResourceFailureCode.AmountOverflow);
                return false;
            }

            Begin();
            nextExperience = 0;
            nextPrestige++;
            for (var i = 0; i < nodes.Length; i++)
                if (!nodes[i].Keep)
                    nextRanks[i] = 0;
            return transaction.Clear().Exchange(wallet, rule.Cost, rule.Reward).Join(this).TryExecute(out failure, new ResourceOperationContext("prestige"));
        }

        private void Begin()
        {
            Array.Copy(ranks, nextRanks, ranks.Length);
            nextExperience = experience;
            nextPrestige = prestige;
            clearResearch = false;
        }

        private void ClearResearch()
        {
            researchIndex = -1;
            researchDue = 0;
            researchOwner = null;
            researchCost = null;
            researchPayment = default;
        }

        private static bool Matches(ResourceReservation hold, ResourceCost cost)
        {
            if (!hold.Owner.TryGetSlot(hold, out var slot) || slot.Count != cost.GetPrepared().Length)
                return false;
            foreach (var row in cost.GetPrepared())
                if (slot.GetAmount(row.Resource) != row.Amount)
                    return false;
            return true;
        }

        private static ResourceFailure Rejected(string detail) => new ResourceFailure(ResourceFailureCode.RuleRejected, detail: detail);
        private void CheckThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != thread)
                throw new InvalidOperationException("Use progression on its creating Unity thread.");
        }

        private void CheckAlive()
        {
            CheckThread();
            if (disposed)
                throw new ObjectDisposedException(nameof(ProgressionProfile));
        }

        private void CheckMutation()
        {
            CheckAlive();
            if (busy)
                throw new InvalidOperationException("Queue progression changes until its notification returns.");
        }

        void IResourceParticipant.Enter()
        {
            CheckMutation();
            busy = true;
        }

        bool IResourceParticipant.Prepare(out ResourceFailure failure)
        {
            failure = default;
            return true;
        }

        void IResourceParticipant.Apply()
        {
            var old = ranks;
            ranks = nextRanks;
            nextRanks = old;
            experience = nextExperience;
            prestige = nextPrestige;
            if (clearResearch)
                ClearResearch();
        }

        void IResourceParticipant.Publish() => changed.Emit();
        void IResourceParticipant.Exit() => busy = false;
        void IResourceParticipant.Finish() => FinishDispose();
        public void Dispose()
        {
            CheckThread();
            if (disposed)
                return;
            CheckMutation();
            if (researchIndex >= 0)
                CancelResearch();
            disposed = true;
            changed.Clear();
            transaction.RequestClear();
        }

        internal void ValidateReplacement()
        {
            CheckMutation();
            if (researchPayment.IsActive)
                throw new InvalidOperationException("Restore the wallet first so the old research reservation becomes invalid, then restore progression.");
        }

        internal void RequestDispose()
        {
            if (busy)
                disposeRequested = true;
            else
                Dispose();
        }

        private void FinishDispose()
        {
            if (disposeRequested && !disposed)
            {
                disposeRequested = false;
                Dispose();
            }
        }

        public ProgressionState CaptureState(long nowMilliseconds = 0)
        {
            CheckAlive();
            if (nowMilliseconds < 0)
                throw new ArgumentOutOfRangeException(nameof(nowMilliseconds));
            var state = new ProgressionState
            {
                DefinitionId = definition.Id,
                Experience = experience,
                Prestige = prestige
            };
            for (var i = 0; i < nodes.Length; i++)
                if (ranks[i] > 0)
                    state.Ranks.Add(new ProgressionRankState { NodeId = nodes[i].Asset.Id, Rank = ranks[i] });
            if (researchIndex >= 0)
            {
                if (!researchPayment.IsActive || !Matches(researchPayment, researchCost))
                    throw new InvalidOperationException("Research payment is missing or has changed.");
                state.Research = new ResearchState
                {
                    NodeId = nodes[researchIndex].Asset.Id,
                    OwnerId = researchOwner,
                    ReservationId = researchPayment.Id,
                    RemainingMilliseconds = Math.Max(0, researchDue - nowMilliseconds),
                    Cost = SnapshotRows.Capture(researchCost.GetPrepared())
                };
            }

            return state;
        }

        public static ProgressionProfile FromState(ProgressionDefinition definition, ResourceStore restoredWallet, ProgressionState state, IEnumerable<ResourceDefinition> resources, long nowMilliseconds = 0, long offlineMilliseconds = 0)
        {
            if (definition == null || state == null || state.Version != 1 || state.DefinitionId != definition.Id || state.Experience < 0 || state.Prestige < 0 || state.Ranks == null || nowMilliseconds < 0 || offlineMilliseconds < 0)
                throw new ArgumentException("Invalid progression snapshot.");
            var profile = new ProgressionProfile(definition, restoredWallet);
            var used = new HashSet<int>();
            var groups = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in state.Ranks)
            {
                if (row == null || row.NodeId == null || !profile.ids.TryGetValue(row.NodeId, out var index) || !used.Add(index) || row.Rank <= 0 || row.Rank > profile.nodes[index].Costs.Length)
                    throw new ArgumentException("Invalid saved node rank.");
                var group = profile.nodes[index].Group;
                if (!string.IsNullOrEmpty(group) && !groups.Add(group))
                    throw new ArgumentException("Saved exclusive branches conflict.");
                profile.ranks[index] = row.Rank;
            }

            profile.experience = state.Experience;
            profile.prestige = state.Prestige;
            // Persistent prestige nodes may intentionally outlive their level and prerequisites.
            if (state.Research != null)
            {
                var research = state.Research;
                if (research.NodeId == null || !profile.ids.TryGetValue(research.NodeId, out var index) || research.RemainingMilliseconds < 0 || string.IsNullOrWhiteSpace(research.OwnerId) || !profile.RequirementsMet(index, out _))
                    throw new ArgumentException("Invalid saved research.");
                var cost = SnapshotRows.Restore(research.Cost, new DefinitionLookup<ResourceDefinition>(resources));
                if (!restoredWallet.TryGetReservation(research.ReservationId, out var payment) || !Matches(payment, cost) || !payment.Owner.TryGetSlot(payment, out var slot) || slot.Options.OwnerId != research.OwnerId)
                    throw new ArgumentException("Restore the matching owned research reservation first.");
                profile.researchDue = checked(nowMilliseconds + Math.Max(0, research.RemainingMilliseconds - offlineMilliseconds));
                profile.researchIndex = index;
                profile.researchOwner = research.OwnerId;
                profile.researchCost = cost;
                profile.researchPayment = payment;
            }

            return profile;
        }
    }

    [Serializable]
    public sealed class ProgressionState
    {
        public int Version = 1;
        public string DefinitionId;
        public long Experience;
        public long Prestige;
        public List<ProgressionRankState> Ranks = new List<ProgressionRankState>();
        public ResearchState Research;
    }

    [Serializable]
    public sealed class ProgressionRankState
    {
        public string NodeId;
        public int Rank;
    }

    [Serializable]
    public sealed class ResearchState
    {
        public string NodeId;
        public string OwnerId;
        public long ReservationId;
        public long RemainingMilliseconds;
        public List<ResourceStateEntry> Cost = new List<ResourceStateEntry>();
    }
}
