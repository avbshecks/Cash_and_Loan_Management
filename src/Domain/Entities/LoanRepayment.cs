using System;
using CashLoanManagement.Domain.Enums;

namespace CashLoanManagement.Domain.Entities;

public class LoanRepayment : BaseEntity
{
    public decimal Amount { get; set; }
    public DateTime RepaymentDate { get; set; } = DateTime.UtcNow;
    public string Reference { get; set; } = string.Empty;

    // Maker-Checker: repayments start Pending; only count towards remaining balance/loan
    // status and the cash ledger once a checker approves them.
    public CashApprovalStatus ApprovalStatus { get; set; } = CashApprovalStatus.Pending;
    public string? RejectionReason { get; set; }
    public DateTime? ApprovedAt { get; set; }

    // Foreign Keys
    public int LoanId { get; set; }
    public int CapturedByUserId { get; set; }
    public int? ApprovedByUserId { get; set; }

    // Navigation properties
    public Loan Loan { get; set; } = null!;
    public User CapturedByUser { get; set; } = null!;
    public User? ApprovedByUser { get; set; }
}
