using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using CashLoanManagement.Domain.Enums;
using CashLoanManagement.Infrastructure.Persistence;

namespace CashLoanManagement.Api.Controllers;

[Authorize]
public class ReportController : BaseApiController
{
    private readonly CashLoanDbContext _context;

    public ReportController(CashLoanDbContext context)
    {
        _context = context;
    }

    // ─── GET /api/report/daily-cash ──────────────────────────────────────────
    [HttpGet("daily-cash")]
    public async Task<IActionResult> GetDailyCashSummary([FromQuery] string? date)
    {
        // Use explicit UTC range comparisons — avoids Npgsql rejecting Kind=Unspecified
        // from .Date property (which strips DateTimeKind) on timestamptz columns.
        var rawDate = DateTime.TryParse(date, out var parsed) ? parsed : DateTime.UtcNow;
        var dayStart = DateTime.SpecifyKind(rawDate.Date,            DateTimeKind.Utc);
        var dayEnd   = DateTime.SpecifyKind(rawDate.Date.AddDays(1), DateTimeKind.Utc);

        var explicitOpening = await _context.CashTransactions
            .Where(t => t.Type == TransactionType.OpeningBalance && t.Date >= dayStart && t.Date < dayEnd)
            .SumAsync(t => (decimal?)t.Amount) ?? 0m;

        var additions = await _context.CashTransactions
            .Where(t => t.Type == TransactionType.Addition && t.Date >= dayStart && t.Date < dayEnd)
            .SumAsync(t => (decimal?)t.Amount) ?? 0m;

        var disbursements = await _context.CashTransactions
            .Where(t => t.Type == TransactionType.Disbursement && t.Date >= dayStart && t.Date < dayEnd)
            .SumAsync(t => (decimal?)t.Amount) ?? 0m;

        var prevCredits = await _context.CashTransactions
            .Where(t => (t.Type == TransactionType.OpeningBalance || t.Type == TransactionType.Addition)
                        && t.Date < dayStart)
            .SumAsync(t => (decimal?)t.Amount) ?? 0m;

        var prevDisbursements = await _context.CashTransactions
            .Where(t => t.Type == TransactionType.Disbursement && t.Date < dayStart)
            .SumAsync(t => (decimal?)t.Amount) ?? 0m;

        var openingBalance = explicitOpening > 0 ? explicitOpening : prevCredits - prevDisbursements;
        var currentBalance = openingBalance + additions - disbursements;

        var reconciliation = await _context.CashReconciliations
            .Where(r => r.Date >= dayStart && r.Date < dayEnd)
            .OrderByDescending(r => r.Id)
            .FirstOrDefaultAsync();

        // Recent transactions for the day
        var transactions = await _context.CashTransactions
            .Include(t => t.CreatedByUser)
            .Where(t => t.Date >= dayStart && t.Date < dayEnd)
            .OrderByDescending(t => t.Date)
            .Select(t => new
            {
                t.Id,
                t.Date,
                type = t.Type.ToString(),
                t.Amount,
                t.SourceOrPurpose,
                t.Reference,
                createdBy = t.CreatedByUser.FullName
            })
            .ToListAsync();

        return Ok(new
        {
            date = dayStart.ToString("yyyy-MM-dd"),
            openingBalanceCaptured = explicitOpening > 0,
            openingBalance,
            totalAdditions = additions,
            totalDisbursements = disbursements,
            currentBalance,
            transactionCount = transactions.Count,
            transactions,
            reconciliation = reconciliation == null ? null : new
            {
                reconciliation.ActualEndBalance,
                reconciliation.Variance,
                status = reconciliation.Variance == 0 ? "Balanced" : "Variance Flagged",
                reconciliation.Comment
            }
        });
    }

    // ─── GET /api/report/monthly-cash ────────────────────────────────────────
    [HttpGet("monthly-cash")]
    public async Task<IActionResult> GetMonthlyCashSummary([FromQuery] int? year, [FromQuery] int? month)
    {
        var y = year ?? DateTime.UtcNow.Year;
        var m = month ?? DateTime.UtcNow.Month;

        var firstDay = new DateTime(y, m, 1, 0, 0, 0, DateTimeKind.Utc);
        var lastDay  = firstDay.AddMonths(1);

        // All transactions for the month
        var txns = await _context.CashTransactions
            .Where(t => t.Date >= firstDay && t.Date < lastDay)
            .ToListAsync();

        // Build per-day summary (keep Kind=Utc throughout — never use .Date which drops Kind)
        var days = Enumerable.Range(0, DateTime.DaysInMonth(y, m)).Select(i =>
        {
            var d = firstDay.AddDays(i); // stays Utc
            // Compare in-memory using local date string to avoid Kind mismatch
            var dStr = d.ToString("yyyy-MM-dd");
            var dayTxns = txns.Where(t => t.Date.ToString("yyyy-MM-dd") == dStr).ToList();
            var adds = dayTxns.Where(t => t.Type == TransactionType.Addition || t.Type == TransactionType.OpeningBalance).Sum(t => t.Amount);
            var disb = dayTxns.Where(t => t.Type == TransactionType.Disbursement).Sum(t => t.Amount);
            return new
            {
                date = d.ToString("yyyy-MM-dd"),
                dayOfWeek = d.DayOfWeek.ToString(),
                additions = adds,
                disbursements = disb,
                net = adds - disb,
                txnCount = dayTxns.Count
            };
        }).ToList();

        var totalAdditions    = txns.Where(t => t.Type == TransactionType.Addition || t.Type == TransactionType.OpeningBalance).Sum(t => t.Amount);
        var totalDisbursements = txns.Where(t => t.Type == TransactionType.Disbursement).Sum(t => t.Amount);

        // Monthly loan activity
        var newLoans = await _context.Loans
            .CountAsync(l => l.CreatedAt >= firstDay && l.CreatedAt < lastDay);

        var repayments = await _context.LoanRepayments
            .Where(r => r.RepaymentDate >= firstDay && r.RepaymentDate < lastDay)
            .SumAsync(r => (decimal?)r.Amount) ?? 0m;

        return Ok(new
        {
            year = y,
            month = m,
            monthName = firstDay.ToString("MMMM yyyy"),
            totalAdditions,
            totalDisbursements,
            netCashFlow = totalAdditions - totalDisbursements,
            newLoansCount = newLoans,
            totalRepaymentsCollected = repayments,
            activeDays = days.Count(d => d.txnCount > 0),
            days
        });
    }

    // ─── GET /api/report/loans ───────────────────────────────────────────────
    [HttpGet("loans")]
    public async Task<IActionResult> GetLoanReport()
    {
        var activeCount      = await _context.Loans.CountAsync(l => l.Status == LoanStatus.Active);
        var pendingCount     = await _context.Loans.CountAsync(l => l.Status == LoanStatus.Pending);
        var overdueCount     = await _context.Loans.CountAsync(l => l.Status == LoanStatus.Overdue);
        var paidCount        = await _context.Loans.CountAsync(l => l.Status == LoanStatus.Paid);
        var blacklistedCount = await _context.Borrowers.CountAsync(b => b.IsBlacklisted);

        var totalOutstanding = await _context.Loans
            .Where(l => l.Status == LoanStatus.Active || l.Status == LoanStatus.Overdue)
            .SumAsync(l => (decimal?)l.Amount) ?? 0m;

        var totalRepaid = await _context.LoanRepayments
            .SumAsync(r => (decimal?)r.Amount) ?? 0m;

        var totalOverdueAmount = await _context.Loans
            .Where(l => l.Status == LoanStatus.Overdue)
            .SumAsync(l => (decimal?)l.Amount) ?? 0m;

        // Recent loans
        var recentLoans = await _context.Loans
            .Include(l => l.Borrower)
            .OrderByDescending(l => l.CreatedAt)
            .Take(10)
            .Select(l => new
            {
                l.Id,
                l.ReferenceNumber,
                borrowerName = l.Borrower.FullName,
                l.Amount,
                l.Status,
                l.DueDate,
                l.CreatedAt
            })
            .ToListAsync();

        return Ok(new
        {
            activeLoansCount      = activeCount,
            pendingLoansCount     = pendingCount,
            overdueLoansCount     = overdueCount,
            paidLoansCount        = paidCount,
            blacklistedBorrowersCount = blacklistedCount,
            totalOutstandingBalance   = totalOutstanding,
            totalRepaidAmount         = totalRepaid,
            totalOverdueAmount,
            recentLoans
        });
    }

    // ─── GET /api/report/audit ───────────────────────────────────────────────
    [HttpGet("audit")]
    public async Task<IActionResult> GetAuditReport(
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        if (pageSize > 200) pageSize = 200;

        var query = _context.AuditLogs.Include(a => a.User).AsQueryable();

        if (DateTime.TryParse(from, out var fromParsed))
        {
            var fromDate = DateTime.SpecifyKind(fromParsed.Date, DateTimeKind.Utc);
            query = query.Where(a => a.Timestamp >= fromDate);
        }

        if (DateTime.TryParse(to, out var toParsed))
        {
            var toDate = DateTime.SpecifyKind(toParsed.Date.AddDays(1), DateTimeKind.Utc);
            query = query.Where(a => a.Timestamp < toDate);
        }

        var total = await query.CountAsync();

        var logs = await query
            .OrderByDescending(a => a.Timestamp)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(a => new
            {
                a.Id,
                a.Timestamp,
                user      = a.User != null ? a.User.Username : "system",
                a.Action,
                a.IpAddress
            })
            .ToListAsync();

        return Ok(new { total, page, pageSize, logs });
    }

    // ─── GET /api/report/notifications ──────────────────────────────────────
    [HttpGet("notifications")]
    public async Task<IActionResult> GetMyNotifications([FromQuery] bool unreadOnly = false)
    {
        var userId = GetCurrentUserId();

        var query = _context.Notifications.Where(n => n.UserId == userId);
        if (unreadOnly) query = query.Where(n => !n.IsRead);

        var notifications = await query
            .OrderByDescending(n => n.CreatedAt)
            .Select(n => new { n.Id, n.Title, n.Message, n.Type, n.IsRead, n.CreatedAt })
            .ToListAsync();

        return Ok(notifications);
    }

    // ─── POST /api/report/notifications/{id}/read ───────────────────────────
    [HttpPost("notifications/{id:int}/read")]
    public async Task<IActionResult> MarkNotificationRead(int id)
    {
        var userId = GetCurrentUserId();
        var notification = await _context.Notifications
            .FirstOrDefaultAsync(n => n.Id == id && n.UserId == userId);

        if (notification == null)
            return NotFound(new { message = "Notification not found." });

        notification.IsRead = true;
        notification.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        return Ok(new { message = "Notification marked as read." });
    }
}
