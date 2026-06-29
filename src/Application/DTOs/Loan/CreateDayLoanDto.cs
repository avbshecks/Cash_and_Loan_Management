namespace CashLoanManagement.Application.DTOs.Loan;

public class CreateDayLoanDto
{
    public int BorrowerId { get; set; }
    public decimal Amount { get; set; }
    public string? Notes { get; set; }
}
