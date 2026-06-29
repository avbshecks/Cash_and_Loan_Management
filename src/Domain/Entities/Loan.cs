using System;
using System.Collections.Generic;
using CashLoanManagement.Domain.Enums;

namespace CashLoanManagement.Domain.Entities;

public class Loan : BaseEntity
{
    public string ReferenceNumber { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string RepaymentTerms { get; set; } = string.Empty;
    public DateTime DueDate { get; set; }
    public LoanStatus Status { get; set; } = LoanStatus.Pending;
    public LoanType Type { get; set; } = LoanType.Standard;

    // Foreign Keys
    public int BorrowerId { get; set; }
    public int CreatedByUserId { get; set; }
    public int? ApprovedByUserId { get; set; }

    // Timestamps
    public DateTime? ApprovedAt { get; set; }
    public DateTime? DisbursedAt { get; set; }

    // Navigation properties
    public Borrower Borrower { get; set; } = null!;
    public User CreatedByUser { get; set; } = null!;
    public User? ApprovedByUser { get; set; }
    public ICollection<LoanRepayment> Repayments { get; set; } = new List<LoanRepayment>();
}
