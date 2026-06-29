using System;
using System.Collections.Generic;

namespace CashLoanManagement.Domain.Entities;

/// <summary>
/// A custodial account for a person who leaves cash with the company for
/// safekeeping. The money belongs to the depositor (a liability for the
/// company) and is tracked separately from operational cash.
/// </summary>
public class SafekeepingAccount : BaseEntity
{
    public string DepositorName { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string? NationalId { get; set; }
    public string? Notes { get; set; }
    public bool IsActive { get; set; } = true;

    public int CreatedByUserId { get; set; }
    public User CreatedByUser { get; set; } = null!;

    public ICollection<SafekeepingTransaction> Transactions { get; set; } = new List<SafekeepingTransaction>();
}
