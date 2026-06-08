using System;

namespace CashLoanManagement.Domain.Entities;

public class LoanRepayment : BaseEntity
{
    public decimal Amount { get; set; }
    public DateTime RepaymentDate { get; set; } = DateTime.UtcNow;
    public string Reference { get; set; } = string.Empty;

    // Foreign Keys
    public int LoanId { get; set; }
    public int CapturedByUserId { get; set; }

    // Navigation properties
    public Loan Loan { get; set; } = null!;
    public User CapturedByUser { get; set; } = null!;
}
