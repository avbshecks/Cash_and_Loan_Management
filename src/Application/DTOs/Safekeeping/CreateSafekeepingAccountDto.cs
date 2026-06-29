namespace CashLoanManagement.Application.DTOs.Safekeeping;

public class CreateSafekeepingAccountDto
{
    public string DepositorName { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string? NationalId { get; set; }
    public string? Notes { get; set; }
}

public class SafekeepingMovementDto
{
    public decimal Amount { get; set; }
    public string? Notes { get; set; }
}
