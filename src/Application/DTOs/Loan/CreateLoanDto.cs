using System;

namespace CashLoanManagement.Application.DTOs.Loan;

public class CreateLoanDto
{
    public int BorrowerId { get; set; }
    public decimal Amount { get; set; }
    public string RepaymentTerms { get; set; } = string.Empty;
    public DateTime DueDate { get; set; }
}
