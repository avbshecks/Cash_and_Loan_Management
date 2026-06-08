using System;

namespace CashLoanManagement.Domain.Entities;

public class CashReconciliation : BaseEntity
{
    public DateTime Date { get; set; } = DateTime.UtcNow.Date;
    public decimal OpeningBalance { get; set; }
    public decimal CalculatedEndBalance { get; set; }
    public decimal ActualEndBalance { get; set; }
    public decimal Variance { get; set; }
    public string Comment { get; set; } = string.Empty;

    // Foreign Key
    public int ReconciliationUserId { get; set; }

    // Navigation property
    public User ReconciliationUser { get; set; } = null!;
}
