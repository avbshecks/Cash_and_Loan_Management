namespace CashLoanManagement.Application.DTOs.Loan;

public class CreateBorrowerDto
{
    public string FullName { get; set; } = string.Empty;
    public string NationalId { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string EmploymentStatus { get; set; } = string.Empty;
    public string? GuarantorDetails { get; set; }
}
