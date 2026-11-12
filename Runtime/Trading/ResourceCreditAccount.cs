using System;

namespace EasyResources.Trading
{
    /// <summary>Explicit debt, separate from nonnegative wallet balances. No automatic borrowing or hidden overdraft.</summary>
    public sealed class ResourceCreditAccount : IResourceParticipant
    {
        private readonly ResourceStore lender, borrower;
        private readonly ResourceDefinition currency;
        private readonly ResourceTransaction transaction = new ResourceTransaction();
        private readonly SafeSignal changed = new SafeSignal();
        private readonly long creditLimit;
        private long debt, nextDebt;
        private bool busy;
        public long Debt => debt;
        public long CreditLimit => creditLimit;

        public event Action Changed { add => changed.Add(value); remove => changed.Remove(value); }

        public ResourceCreditAccount(ResourceStore lender, ResourceStore borrower, ResourceDefinition currency, long creditLimit)
        {
            if (lender == null || borrower == null || currency == null || creditLimit < 0 || ReferenceEquals(lender, borrower) || lender.IsReadOnly || borrower.IsReadOnly)
                throw new ArgumentException("Supply distinct authoritative wallets, currency and nonnegative credit limit.");
            this.lender = lender;
            this.borrower = borrower;
            this.currency = currency;
            this.creditLimit = creditLimit;
        }

        public bool TryBorrow(long amount, out ResourceFailure failure)
        {
            Check();
            if (amount <= 0)
                throw new ArgumentOutOfRangeException(nameof(amount));
            if (amount > creditLimit - debt)
            {
                failure = new ResourceFailure(ResourceFailureCode.RuleRejected, detail: "Credit limit exceeded.");
                return false;
            }

            nextDebt = debt + amount;
            return transaction.Clear().Spend(lender, currency, amount).Add(borrower, currency, amount).Join(this).TryExecute(out failure, new ResourceOperationContext("borrow"), ResourceChangeKind.Transfer);
        }

        public bool TryRepay(long amount, out ResourceFailure failure)
        {
            Check();
            if (amount <= 0 || amount > debt)
                throw new ArgumentOutOfRangeException(nameof(amount), "Repay a positive amount no larger than the debt.");
            nextDebt = debt - amount;
            return transaction.Clear().Spend(borrower, currency, amount).Add(lender, currency, amount).Join(this).TryExecute(out failure, new ResourceOperationContext("repay"), ResourceChangeKind.Transfer);
        }

        /// <summary>Apply explicit interest for a caller-selected period. Interest may exceed the borrowing limit.</summary>
        public void AccrueInterest(int basisPoints)
        {
            Check();
            if (basisPoints < 0)
                throw new ArgumentOutOfRangeException(nameof(basisPoints));
            nextDebt = checked(debt + QuantityMath.Scale(debt, basisPoints, 10000, ResourceRounding.Up));
            busy = true;
            lender.EnterTransaction();
            borrower.EnterTransaction();
            try
            {
                debt = nextDebt;
                changed.Emit();
            }
            finally
            {
                lender.ExitTransaction();
                borrower.ExitTransaction();
                busy = false;
                lender.FinishDeferredDispose();
                borrower.FinishDeferredDispose();
            }
        }

        public ResourceCreditState CaptureState() => new ResourceCreditState
        {
            CurrencyId = currency.Id,
            DecimalPlaces = currency.DecimalPlaces,
            Debt = debt
        };
        public void RestoreState(ResourceCreditState state)
        {
            Check();
            if (state == null || state.Version != 1 || state.Debt < 0 || state.CurrencyId != currency.Id || state.DecimalPlaces != currency.DecimalPlaces)
                throw new ArgumentException("Invalid credit snapshot.");
            nextDebt = state.Debt;
            busy = true;
            lender.EnterTransaction();
            borrower.EnterTransaction();
            try
            {
                debt = nextDebt;
                changed.Emit();
            }
            finally
            {
                lender.ExitTransaction();
                borrower.ExitTransaction();
                busy = false;
                lender.FinishDeferredDispose();
                borrower.FinishDeferredDispose();
            }
        }

        private void Check()
        {
            if (busy)
                throw new InvalidOperationException("Cannot change credit during its notification.");
            lender.CheckMutation();
            borrower.CheckMutation();
        }

        void IResourceParticipant.Enter()
        {
            if (busy)
                throw new InvalidOperationException("Credit is busy.");
            busy = true;
        }

        bool IResourceParticipant.Prepare(out ResourceFailure failure)
        {
            failure = default;
            return true;
        }

        void IResourceParticipant.Apply() => debt = nextDebt;
        void IResourceParticipant.Publish() => changed.Emit();
        void IResourceParticipant.Exit() => busy = false;
        void IResourceParticipant.Finish()
        {
        }
    }

    [Serializable]
    public sealed class ResourceCreditState
    {
        public int Version = 1;
        public string CurrencyId;
        public int DecimalPlaces;
        public long Debt;
    }
}
