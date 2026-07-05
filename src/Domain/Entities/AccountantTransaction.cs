using System;
using CashLoanManagement.Domain.Enums;

namespace CashLoanManagement.Domain.Entities;

/// <summary>
/// A movement in the accountant's cash book — a separate float/book from the
/// main operational cashbox, managed by the Accountant role.
/// </summary>
public class AccountantTransaction : BaseEntity
{
    public decimal Amount { get; set; }
    public TransactionType Type { get; set; }   // OpeningBalance / Addition / Disbursement
    public string SourceOrPurpose { get; set; } = string.Empty;
    public string Reference { get; set; } = string.Empty;
    public DateTime Date { get; set; } = DateTime.UtcNow;

    // Reversal (corrections): maker-checker. Accountant/Admin requests, Manager/Admin approves.
    public bool IsReversed { get; set; }
    public int? ReversalOfTransactionId { get; set; }
    public CashApprovalStatus? ReversalStatus { get; set; }
    public string? ReversalReason { get; set; }
    public int? ReversalRequestedByUserId { get; set; }
    public DateTime? ReversalRequestedAt { get; set; }
    public int? ReversalApprovedByUserId { get; set; }
    public DateTime? ReversalApprovedAt { get; set; }
    public string? ReversalRejectionReason { get; set; }

    public int CreatedByUserId { get; set; }
    public User CreatedByUser { get; set; } = null!;
    public User? ReversalRequestedByUser { get; set; }
    public User? ReversalApprovedByUser { get; set; }
}
