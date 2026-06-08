namespace CashLoanManagement.Domain.Enums;

public enum TransactionType
{
    Addition = 0,       // original value — do NOT reorder
    Disbursement = 1,   // original value — do NOT reorder
    OpeningBalance = 2  // added after initial migration; explicit daily opening capture (BRD §7.3)
}
