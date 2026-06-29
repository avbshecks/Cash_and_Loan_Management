using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using CashLoanManagement.Application.Common.Interfaces;
using CashLoanManagement.Domain.Entities;

namespace CashLoanManagement.Infrastructure.Persistence;

public class CashLoanDbContext : DbContext, ICashLoanDbContext
{
    public CashLoanDbContext(DbContextOptions<CashLoanDbContext> options) : base(options)
    {
    }

    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<CashTransaction> CashTransactions => Set<CashTransaction>();
    public DbSet<CashReconciliation> CashReconciliations => Set<CashReconciliation>();
    public DbSet<Borrower> Borrowers => Set<Borrower>();
    public DbSet<Loan> Loans => Set<Loan>();
    public DbSet<LoanRepayment> LoanRepayments => Set<LoanRepayment>();
    public DbSet<Blacklist> Blacklists => Set<Blacklist>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<ApprovalWorkflow> ApprovalWorkflows => Set<ApprovalWorkflow>();
    public DbSet<SafekeepingAccount> SafekeepingAccounts => Set<SafekeepingAccount>();
    public DbSet<SafekeepingTransaction> SafekeepingTransactions => Set<SafekeepingTransaction>();

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        return base.SaveChangesAsync(cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Apply any configuration classes if separated, otherwise configure inline
        
        // Decimal precisions
        modelBuilder.Entity<CashTransaction>()
            .Property(t => t.Amount)
            .HasColumnType("decimal(18,2)");

        modelBuilder.Entity<CashReconciliation>()
            .Property(r => r.OpeningBalance).HasColumnType("decimal(18,2)");
        modelBuilder.Entity<CashReconciliation>()
            .Property(r => r.CalculatedEndBalance).HasColumnType("decimal(18,2)");
        modelBuilder.Entity<CashReconciliation>()
            .Property(r => r.ActualEndBalance).HasColumnType("decimal(18,2)");
        modelBuilder.Entity<CashReconciliation>()
            .Property(r => r.Variance).HasColumnType("decimal(18,2)");

        modelBuilder.Entity<Loan>()
            .Property(l => l.Amount)
            .HasColumnType("decimal(18,2)");

        modelBuilder.Entity<LoanRepayment>()
            .Property(r => r.Amount)
            .HasColumnType("decimal(18,2)");

        modelBuilder.Entity<SafekeepingTransaction>()
            .Property(t => t.Amount)
            .HasColumnType("decimal(18,2)");

        // Safekeeping relationships
        modelBuilder.Entity<SafekeepingAccount>()
            .HasOne(a => a.CreatedByUser)
            .WithMany()
            .HasForeignKey(a => a.CreatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<SafekeepingTransaction>()
            .HasOne(t => t.Account)
            .WithMany(a => a.Transactions)
            .HasForeignKey(t => t.AccountId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<SafekeepingTransaction>()
            .HasOne(t => t.CreatedByUser)
            .WithMany()
            .HasForeignKey(t => t.CreatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        // Restrict deletes on multiple cascade paths
        modelBuilder.Entity<Loan>()
            .HasOne(l => l.CreatedByUser)
            .WithMany()
            .HasForeignKey(l => l.CreatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Loan>()
            .HasOne(l => l.ApprovedByUser)
            .WithMany()
            .HasForeignKey(l => l.ApprovedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<LoanRepayment>()
            .HasOne(r => r.CapturedByUser)
            .WithMany()
            .HasForeignKey(r => r.CapturedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<CashTransaction>()
            .HasOne(t => t.CreatedByUser)
            .WithMany()
            .HasForeignKey(t => t.CreatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<CashTransaction>()
            .HasOne(t => t.ApprovedByUser)
            .WithMany()
            .HasForeignKey(t => t.ApprovedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<CashReconciliation>()
            .HasOne(r => r.ReconciliationUser)
            .WithMany()
            .HasForeignKey(r => r.ReconciliationUserId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Blacklist>()
            .HasOne(b => b.BlacklistedByUser)
            .WithMany()
            .HasForeignKey(b => b.BlacklistedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<ApprovalWorkflow>()
            .HasOne(w => w.RequestedByUser)
            .WithMany()
            .HasForeignKey(w => w.RequestedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<ApprovalWorkflow>()
            .HasOne(w => w.ApprovedOrRejectedByUser)
            .WithMany()
            .HasForeignKey(w => w.ApprovedOrRejectedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        // Seed basic roles
        modelBuilder.Entity<Role>().HasData(
            new Role { Id = 1, Name = "Admin", Description = "Full system access" },
            new Role { Id = 2, Name = "Cashier", Description = "Cash transactions access" },
            new Role { Id = 3, Name = "Finance Officer", Description = "Loans and approvals access" },
            new Role { Id = 4, Name = "Manager", Description = "Reporting and approvals access" },
            new Role { Id = 5, Name = "Auditor", Description = "Read-only reporting access" }
        );
    }
}
