using System.Collections.Generic;

namespace CashLoanManagement.Domain.Entities;

public class Role : BaseEntity
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    // Navigation property
    public ICollection<User> Users { get; set; } = new List<User>();
}
