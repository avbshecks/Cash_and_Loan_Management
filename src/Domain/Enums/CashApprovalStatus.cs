namespace CashLoanManagement.Domain.Enums;

public enum CashApprovalStatus
{
    AutoApproved = 0,  // additions & opening balances — always counted immediately
    Pending      = 1,  // disbursements awaiting checker approval
    Approved     = 2,  // disbursements approved by checker — counted in balance
    Rejected     = 3   // disbursements rejected — never counted
}
