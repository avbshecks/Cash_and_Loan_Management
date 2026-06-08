namespace CashLoanManagement.Application.DTOs.Loan;

public class CaptureRepaymentDto
{
    public int LoanId { get; set; }
    public decimal Amount { get; set; }
    public string Reference { get; set; } = string.Empty;
}
