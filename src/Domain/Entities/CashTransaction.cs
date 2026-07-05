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

    // Reversal (corrections): a contra entry that nets a mistaken entry to zero.
    // Reversal is itself maker-checker: Cashier/Accountant/Admin requests it,
    // Manager/Admin approves — only on approval is the contra entry posted.
    public bool IsReversed { get; set; }                  // true on the ORIGINAL once reversal is approved
    public int? ReversalOfTransactionId { get; set; }     // set on the REVERSAL entry, points to original

    public CashApprovalStatus? ReversalStatus { get; set; }   // null = never requested
    public string? ReversalReason { get; set; }
    public int? ReversalRequestedByUserId { get; set; }
    public DateTime? ReversalRequestedAt { get; set; }
    public int? ReversalApprovedByUserId { get; set; }
    public DateTime? ReversalApprovedAt { get; set; }
    public string? ReversalRejectionReason { get; set; }

    // Foreign Keys
    public int CreatedByUserId { get; set; }
    public int? ApprovedByUserId { get; set; }

    // Navigation properties
    public User CreatedByUser { get; set; } = null!;
    public User? ApprovedByUser { get; set; }
    public User? ReversalRequestedByUser { get; set; }
    public User? ReversalApprovedByUser { get; set; }
}
