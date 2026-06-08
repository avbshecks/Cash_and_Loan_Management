using CashLoanManagement.Domain.Enums;

namespace CashLoanManagement.Domain.Entities;

public class ApprovalWorkflow : BaseEntity
{
    public WorkflowTargetType TargetType { get; set; }
    public int TargetId { get; set; }
    public ApprovalStatus Status { get; set; } = ApprovalStatus.Pending;
    public string Comment { get; set; } = string.Empty;

    // Foreign Keys
    public int RequestedByUserId { get; set; }
    public int? ApprovedOrRejectedByUserId { get; set; }

    // Navigation properties
    public User RequestedByUser { get; set; } = null!;
    public User? ApprovedOrRejectedByUser { get; set; }
}
