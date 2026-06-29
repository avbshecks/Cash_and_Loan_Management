using System;
using CashLoanManagement.Domain.Enums;

namespace CashLoanManagement.Domain.Entities;

public class SafekeepingTransaction : BaseEntity
{
    public int AccountId { get; set; }
    public SafekeepingTransactionType Type { get; set; }
    public decimal Amount { get; set; }
    public DateTime Date { get; set; } = DateTime.UtcNow;
    public string Reference { get; set; } = string.Empty;
    public string? Notes { get; set; }

    // Maker-checker: deposits are AutoApproved; withdrawals start Pending.
    public CashApprovalStatus ApprovalStatus { get; set; } = CashApprovalStatus.AutoApproved;
    public string? RejectionReason { get; set; }
    public int? ApprovedByUserId { get; set; }
    public DateTime? ApprovedAt { get; set; }

    public int CreatedByUserId { get; set; }

    // Navigation
    public SafekeepingAccount Account { get; set; } = null!;
    public User CreatedByUser { get; set; } = null!;
}
