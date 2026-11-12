namespace EasyResources
{
    public enum ResourceFailureCode
    {
        None,
        InsufficientResource,
        ResourceNotAccepted,
        ResourceCapacityExceeded,
        WeightCapacityExceeded,
        VolumeCapacityExceeded,
        AmountOverflow,
        ReservationNotActive,
        ReservationExpired,
        ReservationAmountExceeded,
        ReadOnly,
        RuleRejected,
        StaleRevision,
        InvalidCommand,
        Unauthorized,
        ExpiredCommand
    }

    public readonly struct ResourceFailure
    {
        public ResourceFailureCode Code { get; }
        public ResourceDefinition Resource { get; }
        public ResourceStore Store { get; }
        public long Requested { get; }
        public long Available { get; }
        public long Limit { get; }
        public string Detail { get; }
        public bool HasFailure => Code != ResourceFailureCode.None;

        public ResourceFailure(ResourceFailureCode code, ResourceDefinition resource = null, ResourceStore store = null, long requested = 0, long available = 0, long limit = 0, string detail = null)
        {
            Code = code;
            Resource = resource;
            Store = store;
            Requested = requested;
            Available = available;
            Limit = limit;
            Detail = detail;
        }

        public override string ToString()
        {
            if (!string.IsNullOrEmpty(Detail))
                return Detail;
            var label = Resource == null ? "resource" : Resource.DisplayName;
            switch (Code)
            {
                case ResourceFailureCode.None:
                    return "Success";
                case ResourceFailureCode.InsufficientResource:
                    return $"Not enough {label}: need {Format(Requested)}, available {Format(Available)}.";
                case ResourceFailureCode.ResourceNotAccepted:
                    return $"This container does not accept {label}.";
                case ResourceFailureCode.ResourceCapacityExceeded:
                    return $"{label} exceeds its container limit of {Format(Limit)}.";
                case ResourceFailureCode.WeightCapacityExceeded:
                    return $"The weight limit ({Limit}) would be exceeded.";
                case ResourceFailureCode.VolumeCapacityExceeded:
                    return $"The volume limit ({Limit}) would be exceeded.";
                case ResourceFailureCode.AmountOverflow:
                    return "The result exceeds the quantity range.";
                case ResourceFailureCode.ReservationNotActive:
                    return "The reservation has finished or belongs to a previous restored state.";
                case ResourceFailureCode.ReservationExpired:
                    return "The reservation deadline has expired.";
                case ResourceFailureCode.ReservationAmountExceeded:
                    return $"The reservation does not hold enough {label}.";
                case ResourceFailureCode.ReadOnly:
                    return "Read-only replica: send the operation to the authority.";
                default:
                    return Code.ToString();
            }
        }

        private string Format(long amount) => Resource == null ? amount.ToString() : Resource.Format(amount);
    }

    public enum ResourceChangeKind
    {
        Add,
        Spend,
        Reserve,
        Commit,
        Cancel,
        Expire,
        Transfer,
        Exchange,
        Transaction,
        Restore,
        Limits,
        Replica
    }

    public readonly struct ResourceOperationContext
    {
        public string Reason { get; }
        public string OwnerId { get; }
        public long TimestampMilliseconds { get; }

        public ResourceOperationContext(string reason, string ownerId = null, long timestampMilliseconds = 0)
        {
            Reason = reason;
            OwnerId = ownerId;
            TimestampMilliseconds = timestampMilliseconds;
        }
    }

    public readonly struct ResourceBalance
    {
        public ResourceDefinition Resource { get; }
        public long Total { get; }
        public long Reserved { get; }
        public long Available => Total - Reserved;

        public ResourceBalance(ResourceDefinition resource, long total, long reserved)
        {
            Resource = resource;
            Total = total;
            Reserved = reserved;
        }
    }

    public readonly struct ResourceChange
    {
        public ResourceStore Store { get; }
        public long Revision { get; }
        public ResourceChangeKind Kind { get; }
        public ResourceOperationContext Context { get; }
        public ResourceBalance Before { get; }
        public ResourceBalance After { get; }
        public ResourceDefinition Resource => After.Resource;
        public long TotalDelta => After.Total - Before.Total;
        public long ReservedDelta => After.Reserved - Before.Reserved;

        public ResourceChange(ResourceStore store, long revision, ResourceChangeKind kind, ResourceOperationContext context, ResourceBalance before, ResourceBalance after)
        {
            Store = store;
            Revision = revision;
            Kind = kind;
            Context = context;
            Before = before;
            After = after;
        }
    }
}
