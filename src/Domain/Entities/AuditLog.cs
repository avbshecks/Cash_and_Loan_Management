using System;

namespace CashLoanManagement.Domain.Entities;

public class AuditLog : BaseEntity
{
    public string Action { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public string DeviceInfo { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    // Foreign Key (Optional for anonymous actions, but mostly tied)
    public int? UserId { get; set; }

    // Navigation property
    public User? User { get; set; }
}
