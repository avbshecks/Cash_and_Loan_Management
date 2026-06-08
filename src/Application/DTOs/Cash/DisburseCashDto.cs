namespace CashLoanManagement.Application.DTOs.Cash;

public class DisburseCashDto
{
    public decimal Amount { get; set; }
    public string Recipient { get; set; } = string.Empty;
    public string Purpose { get; set; } = string.Empty;
    public string Reference { get; set; } = string.Empty;
}
