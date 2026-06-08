using CashLoanManagement.Domain.Enums;

namespace CashLoanManagement.Domain.Entities;

public class Notification : BaseEntity
{
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public bool IsRead { get; set; }
    public NotificationType Type { get; set; }

    // Foreign Key
    public int UserId { get; set; }

    // Navigation property
    public User User { get; set; } = null!;
}
