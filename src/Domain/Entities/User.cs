namespace CashLoanManagement.Domain.Entities;

public class User : BaseEntity
{
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;

    // Password lifecycle
    public bool MustChangePassword { get; set; } = false;
    public DateTime? PasswordChangedAt { get; set; }

    // Refresh token (JWT rotation)
    public string? RefreshToken { get; set; }
    public DateTime? RefreshTokenExpiry { get; set; }

    // Password reset
    public string? PasswordResetToken { get; set; }
    public DateTime? PasswordResetTokenExpiry { get; set; }

    // Foreign Key
    public int RoleId { get; set; }

    // Navigation property
    public Role Role { get; set; } = null!;
}
