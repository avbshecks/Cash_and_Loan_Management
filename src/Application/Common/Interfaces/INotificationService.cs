using CashLoanManagement.Domain.Enums;

namespace CashLoanManagement.Application.Common.Interfaces;

public interface INotificationService
{
    Task NotifyUserAsync(int userId, string title, string message, NotificationType type, CancellationToken ct = default);
    Task NotifyRoleAsync(string roleName, string title, string message, NotificationType type, CancellationToken ct = default);
}
