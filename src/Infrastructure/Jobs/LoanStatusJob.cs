using CashLoanManagement.Application.Common.Interfaces;
using CashLoanManagement.Domain.Enums;
using CashLoanManagement.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CashLoanManagement.Infrastructure.Jobs;

public class LoanStatusJob
{
    private readonly CashLoanDbContext _context;
    private readonly INotificationService _notificationService;
    private readonly ILogger<LoanStatusJob> _logger;

    private const int BlacklistThresholdDays = 60;   // BRD §7.5: blacklist at 60+ days overdue
    private const decimal LowCashThreshold = 5000m;

    public LoanStatusJob(CashLoanDbContext context, INotificationService notificationService, ILogger<LoanStatusJob> logger)
    {
        _context = context;
        _notificationService = notificationService;
        _logger = logger;
    }

    /// <summary>
    /// Marks Active loans as Overdue when past due date. Run daily.
    /// </summary>
    public async Task MarkOverdueLoansAsync()
    {
        var today = DateTime.UtcNow.Date;

        var newlyOverdue = await _context.Loans
            .Where(l => l.Status == LoanStatus.Active && l.DueDate.Date < today)
            .ToListAsync();

        foreach (var loan in newlyOverdue)
        {
            loan.Status = LoanStatus.Overdue;
            loan.UpdatedAt = DateTime.UtcNow;
        }

        if (newlyOverdue.Count > 0)
        {
            await _context.SaveChangesAsync();
            _logger.LogInformation("Marked {Count} loans as overdue.", newlyOverdue.Count);

            await _notificationService.NotifyRoleAsync(
                "Manager",
                "Overdue Loans Detected",
                $"{newlyOverdue.Count} loan(s) are now overdue.",
                NotificationType.OverdueLoan);

            await _notificationService.NotifyRoleAsync(
                "Finance Officer",
                "Overdue Loans Detected",
                $"{newlyOverdue.Count} loan(s) are now overdue.",
                NotificationType.OverdueLoan);
        }
    }

    /// <summary>
    /// Auto-blacklists borrowers whose loans are overdue beyond the threshold. Run daily.
    /// </summary>
    public async Task AutoBlacklistDefaultersAsync()
    {
        var cutoffDate = DateTime.UtcNow.Date.AddDays(-BlacklistThresholdDays);

        var eligibleLoans = await _context.Loans
            .Include(l => l.Borrower)
            .Where(l => l.Status == LoanStatus.Overdue && l.DueDate.Date < cutoffDate && !l.Borrower.IsBlacklisted)
            .ToListAsync();

        var blacklistedBorrowers = new List<string>();

        foreach (var loan in eligibleLoans)
        {
            loan.Status = LoanStatus.Blacklisted;
            loan.UpdatedAt = DateTime.UtcNow;

            loan.Borrower.IsBlacklisted = true;
            loan.Borrower.BlacklistedAt = DateTime.UtcNow;
            loan.Borrower.BlacklistedReason = $"Loan {loan.ReferenceNumber} overdue by more than {BlacklistThresholdDays} days.";
            loan.Borrower.UpdatedAt = DateTime.UtcNow;

            _context.Blacklists.Add(new Domain.Entities.Blacklist
            {
                BorrowerId = loan.BorrowerId,
                Reason = loan.Borrower.BlacklistedReason,
                BlacklistedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow
            });

            blacklistedBorrowers.Add(loan.Borrower.FullName);
        }

        if (blacklistedBorrowers.Count > 0)
        {
            await _context.SaveChangesAsync();
            _logger.LogWarning("Auto-blacklisted {Count} borrowers: {Names}", blacklistedBorrowers.Count, string.Join(", ", blacklistedBorrowers));

            await _notificationService.NotifyRoleAsync(
                "Manager",
                "Borrowers Auto-Blacklisted",
                $"{blacklistedBorrowers.Count} borrower(s) auto-blacklisted for exceeding {BlacklistThresholdDays}-day overdue threshold.",
                NotificationType.BlacklistAttempt);
        }
    }

    /// <summary>
    /// Checks cash balance and notifies if below threshold. Run daily.
    /// </summary>
    public async Task CheckLowCashBalanceAsync()
    {
        var additions = await _context.CashTransactions
            .Where(t => t.Type == TransactionType.OpeningBalance || t.Type == TransactionType.Addition)
            .SumAsync(t => (decimal?)t.Amount) ?? 0m;

        var disbursements = await _context.CashTransactions
            .Where(t => t.Type == TransactionType.Disbursement)
            .SumAsync(t => (decimal?)t.Amount) ?? 0m;

        var balance = additions - disbursements;

        if (balance < LowCashThreshold)
        {
            _logger.LogWarning("Low cash balance detected: {Balance:C}", balance);

            await _notificationService.NotifyRoleAsync(
                "Manager",
                "Low Cash Balance Alert",
                $"Current cash balance is {balance:N2}, which is below the threshold of {LowCashThreshold:N2}.",
                NotificationType.LowCashBalance);

            await _notificationService.NotifyRoleAsync(
                "Finance Officer",
                "Low Cash Balance Alert",
                $"Current cash balance is {balance:N2}, which is below the threshold of {LowCashThreshold:N2}.",
                NotificationType.LowCashBalance);
        }
    }
}
