using System;

namespace CashLoanManagement.Domain.Entities;

public class Blacklist : BaseEntity
{
    public string Reason { get; set; } = string.Empty;
    public DateTime BlacklistedAt { get; set; } = DateTime.UtcNow;

    // Foreign Keys
    public int BorrowerId { get; set; }
    public int? BlacklistedByUserId { get; set; }  // null = system auto-blacklist

    // Navigation properties
    public Borrower Borrower { get; set; } = null!;
    public User? BlacklistedByUser { get; set; }
}
