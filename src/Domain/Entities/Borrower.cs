using System;
using System.Collections.Generic;

namespace CashLoanManagement.Domain.Entities;

public class Borrower : BaseEntity
{
    public string FullName { get; set; } = string.Empty;
    public string NationalId { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string EmploymentStatus { get; set; } = string.Empty;
    
    // Optional details
    public string? GuarantorDetails { get; set; }
    public string? PhotoUrl { get; set; }

    // Blacklist status
    public bool IsBlacklisted { get; set; }
    public DateTime? BlacklistedAt { get; set; }
    public string? BlacklistedReason { get; set; }
    public int? BlacklistedByUserId { get; set; }

    // Navigation properties
    public User? BlacklistedByUser { get; set; }
    public ICollection<Loan> Loans { get; set; } = new List<Loan>();
}
