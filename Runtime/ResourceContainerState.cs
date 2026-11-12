using System;
using System.Collections.Generic;

namespace EasyResources
{
    public enum UnknownResourcePolicy
    {
        Reject,
        Ignore
    }

    [Serializable]
    public sealed class ResourceContainerState
    {
        public const int CurrentVersion = 2;
        public int Version = CurrentVersion;
        public long Revision;
        public List<ResourceStateEntry> Resources = new List<ResourceStateEntry>();
        public List<ResourceReservationState> Reservations = new List<ResourceReservationState>();
    }

    [Serializable]
    public struct ResourceStateEntry
    {
        public string ResourceId;
        public long Amount;
        public int DecimalPlaces;
        public ResourceStateEntry(string resourceId, long amount, int decimalPlaces = 0)
        {
            ResourceId = resourceId;
            Amount = amount;
            DecimalPlaces = decimalPlaces;
        }
    }

    [Serializable]
    public sealed class ResourceReservationState
    {
        public long Id;
        public string OwnerId;
        public long AgeMilliseconds;
        public bool HasExpiry;
        public long RemainingMilliseconds;
        public List<ResourceStateEntry> Resources = new List<ResourceStateEntry>();
    }

    public readonly struct ResourceRestoreOptions
    {
        public UnknownResourcePolicy UnknownResources { get; }
        public bool ResumeReservations { get; }
        public bool ReplaceActiveReservations { get; }
        public long NowMilliseconds { get; }

        public ResourceRestoreOptions(UnknownResourcePolicy unknownResources = UnknownResourcePolicy.Reject, bool resumeReservations = false, bool replaceActiveReservations = false, long nowMilliseconds = 0)
        {
            if (nowMilliseconds < 0)
                throw new ArgumentOutOfRangeException(nameof(nowMilliseconds));
            UnknownResources = unknownResources;
            ResumeReservations = resumeReservations;
            ReplaceActiveReservations = replaceActiveReservations;
            NowMilliseconds = nowMilliseconds;
        }
    }

    public readonly struct ResourceRestoreReport
    {
        public int IgnoredResourceCount { get; }
        public int RestoredReservationCount { get; }

        internal ResourceRestoreReport(int ignored, int reservations)
        {
            IgnoredResourceCount = ignored;
            RestoredReservationCount = reservations;
        }
    }
}
