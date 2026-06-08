namespace CashLoanManagement.Application.DTOs.Cash;

public class ReconcileCashDto
{
    public decimal OpeningBalance { get; set; }
    public decimal ActualEndBalance { get; set; }
    public string Comment { get; set; } = string.Empty;
}
