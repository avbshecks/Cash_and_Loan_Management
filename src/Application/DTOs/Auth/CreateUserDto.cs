namespace CashLoanManagement.Application.DTOs.Auth;

public class CreateUserDto
{
    public string Username { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public int RoleId { get; set; }
    public string DefaultPassword { get; set; } = string.Empty;
}
