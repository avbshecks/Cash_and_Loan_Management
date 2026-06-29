namespace CashLoanManagement.Domain.Enums;

public enum LoanType
{
    Standard = 0,   // normal term loan with approval workflow
    DayLoan  = 1    // same-day: borrow in the morning, repay in the evening
}
