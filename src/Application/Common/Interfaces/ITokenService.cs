namespace CashLoanManagement.Application.Common.Interfaces;

public interface ITokenService
{
    string GenerateToken(int userId, string username, string fullName, string role);
    string GenerateRefreshToken();
}
