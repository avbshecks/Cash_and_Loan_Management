using System;
using CashLoanManagement.Domain.Enums;

namespace CashLoanManagement.Domain.Entities;

public class CashTransaction : BaseEntity
{
    public decimal Amount { get; set; }
    public TransactionType Type { get; set; }
    public string SourceOrPurpose { get; set; } = string.Empty;
    public string Reference { get; set; } = string.Empty;
    public DateTime Date { get; set; } = DateTime.UtcNow;

    // Maker-Checker: disbursements start as Pending; additions are AutoApproved
    public CashApprovalStatus ApprovalStatus { get; set; } = CashApprovalStatus.AutoApproved;
    public string? RejectionReason { get; set; }
    public DateTime? ApprovedAt { get; set; }

    // Foreign Keys
    public int CreatedByUserId { get; set; }
    public int? ApprovedByUserId { get; set; }

    // Navigation properties
    public User CreatedByUser { get; set; } = null!;
    public User? ApprovedByUser { get; set; }
}
