using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using CashLoanManagement.Domain.Entities;

namespace CashLoanManagement.Application.Common.Interfaces;

public interface ICashLoanDbContext
{
    DbSet<User> Users { get; }
    DbSet<Role> Roles { get; }
    DbSet<CashTransaction> CashTransactions { get; }
    DbSet<CashReconciliation> CashReconciliations { get; }
    DbSet<Borrower> Borrowers { get; }
    DbSet<Loan> Loans { get; }
    DbSet<LoanRepayment> LoanRepayments { get; }
    DbSet<Blacklist> Blacklists { get; }
    DbSet<AuditLog> AuditLogs { get; }
    DbSet<Notification> Notifications { get; }
    DbSet<ApprovalWorkflow> ApprovalWorkflows { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
