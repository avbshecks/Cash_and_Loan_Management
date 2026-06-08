using CashLoanManagement.Application.Common.Interfaces;
using CashLoanManagement.Domain.Entities;
using CashLoanManagement.Domain.Enums;
using CashLoanManagement.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CashLoanManagement.Infrastructure.Services;

public class NotificationService : INotificationService
{
    private readonly CashLoanDbContext _context;

    public NotificationService(CashLoanDbContext context)
    {
        _context = context;
    }

    public async Task NotifyUserAsync(int userId, string title, string message, NotificationType type, CancellationToken ct = default)
    {
        _context.Notifications.Add(new Notification
        {
            UserId = userId,
            Title = title,
            Message = message,
            Type = type,
            IsRead = false,
            CreatedAt = DateTime.UtcNow
        });
        await _context.SaveChangesAsync(ct);
    }

    public async Task NotifyRoleAsync(string roleName, string title, string message, NotificationType type, CancellationToken ct = default)
    {
        var users = await _context.Users
            .Include(u => u.Role)
            .Where(u => u.Role.Name == roleName && u.IsActive)
            .ToListAsync(ct);

        foreach (var user in users)
        {
            _context.Notifications.Add(new Notification
            {
                UserId = user.Id,
                Title = title,
                Message = message,
                Type = type,
                IsRead = false,
                CreatedAt = DateTime.UtcNow
            });
        }

        await _context.SaveChangesAsync(ct);
    }
}
