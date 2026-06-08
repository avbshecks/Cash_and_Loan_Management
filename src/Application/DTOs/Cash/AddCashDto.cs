namespace CashLoanManagement.Application.DTOs.Cash;

public class AddCashDto
{
    public decimal Amount { get; set; }
    public string Source { get; set; } = string.Empty;
    public string Reference { get; set; } = string.Empty;
}
