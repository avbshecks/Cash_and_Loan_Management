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

    public int CreatedByUserId { get; set; }
    public User CreatedByUser { get; set; } = null!;
}
