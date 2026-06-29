using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using CashLoanManagement.Application.Common.Interfaces;
using CashLoanManagement.Application.DTOs.Safekeeping;
using CashLoanManagement.Domain.Entities;
using CashLoanManagement.Domain.Enums;
using CashLoanManagement.Infrastructure.Persistence;

namespace CashLoanManagement.Api.Controllers;

[Authorize]
public class SafekeepingController : BaseApiController
{
    private readonly CashLoanDbContext _context;
    private readonly INotificationService _notificationService;

    public SafekeepingController(CashLoanDbContext context, INotificationService notificationService)
    {
        _context = context;
        _notificationService = notificationService;
    }

    // ─── GET /api/safekeeping/accounts ────────────────────────────────────────
    [HttpGet("accounts")]
    public async Task<IActionResult> GetAccounts()
    {
        var accounts = await _context.SafekeepingAccounts
            .Include(a => a.Transactions)
            .OrderBy(a => a.DepositorName)
            .Select(a => new
            {
                a.Id, a.DepositorName, a.Phone, a.NationalId, a.IsActive, a.CreatedAt,
                // Balance counts deposits and only APPROVED withdrawals
                balance = a.Transactions.Where(t => t.Type == SafekeepingTransactionType.Deposit).Sum(t => t.Amount)
                        - a.Transactions.Where(t => t.Type == SafekeepingTransactionType.Withdrawal &&
                              (t.ApprovalStatus == CashApprovalStatus.AutoApproved || t.ApprovalStatus == CashApprovalStatus.Approved)).Sum(t => t.Amount),
                totalDeposited = a.Transactions.Where(t => t.Type == SafekeepingTransactionType.Deposit).Sum(t => t.Amount),
                totalCollected = a.Transactions.Where(t => t.Type == SafekeepingTransactionType.Withdrawal &&
                              (t.ApprovalStatus == CashApprovalStatus.AutoApproved || t.ApprovalStatus == CashApprovalStatus.Approved)).Sum(t => t.Amount),
                pendingWithdrawals = a.Transactions.Count(t => t.Type == SafekeepingTransactionType.Withdrawal && t.ApprovalStatus == CashApprovalStatus.Pending),
                lastActivity = a.Transactions.Max(t => (DateTime?)t.Date)
            })
            .ToListAsync();

        return Ok(new { totalHeld = accounts.Sum(a => a.balance), accounts });
    }

    // ─── GET /api/safekeeping/accounts/{id} (statement) ───────────────────────
    [HttpGet("accounts/{id:int}")]
    public async Task<IActionResult> GetAccount(int id)
    {
        var account = await _context.SafekeepingAccounts
            .Include(a => a.Transactions).ThenInclude(t => t.CreatedByUser)
            .Include(a => a.CreatedByUser)
            .FirstOrDefaultAsync(a => a.Id == id);

        if (account == null) return NotFound(new { message = "Safekeeping account not found." });

        // Running balance only moves on deposits and approved withdrawals
        var ordered = account.Transactions.OrderBy(t => t.Date).ToList();
        decimal running = 0;
        var statement = ordered.Select(t =>
        {
            var counts = t.Type == SafekeepingTransactionType.Deposit
                         || t.ApprovalStatus == CashApprovalStatus.AutoApproved
                         || t.ApprovalStatus == CashApprovalStatus.Approved;
            if (counts) running += t.Type == SafekeepingTransactionType.Deposit ? t.Amount : -t.Amount;
            return new
            {
                t.Id, t.Date, type = t.Type.ToString(), t.Amount,
                status = t.ApprovalStatus.ToString(),
                t.Reference, t.Notes, t.RejectionReason,
                balanceAfter = running,
                capturedBy = t.CreatedByUser.FullName
            };
        }).ToList();

        var deposits = ordered.Where(t => t.Type == SafekeepingTransactionType.Deposit).ToList();
        var approvedWithdrawals = ordered.Where(t => t.Type == SafekeepingTransactionType.Withdrawal &&
            (t.ApprovalStatus == CashApprovalStatus.AutoApproved || t.ApprovalStatus == CashApprovalStatus.Approved)).ToList();

        return Ok(new
        {
            account.Id, account.DepositorName, account.Phone, account.NationalId,
            account.Notes, account.IsActive, account.CreatedAt,
            openedBy = account.CreatedByUser.FullName,
            dateFirstLeft = deposits.Count > 0 ? deposits.First().Date : (DateTime?)null,
            lastCollection = approvedWithdrawals.Count > 0 ? approvedWithdrawals.Last().Date : (DateTime?)null,
            totalDeposited = deposits.Sum(t => t.Amount),
            totalCollected = approvedWithdrawals.Sum(t => t.Amount),
            pendingCollections = ordered.Count(t => t.Type == SafekeepingTransactionType.Withdrawal && t.ApprovalStatus == CashApprovalStatus.Pending),
            currentBalance = running,
            transactionCount = ordered.Count,
            statement
        });
    }

    // ─── POST /api/safekeeping/account ────────────────────────────────────────
    [HttpPost("account")]
    public async Task<IActionResult> CreateAccount([FromBody] CreateSafekeepingAccountDto request)
    {
        if (string.IsNullOrWhiteSpace(request.DepositorName))
            return BadRequest(new { message = "Depositor name is required." });

        var account = new SafekeepingAccount
        {
            DepositorName = request.DepositorName, Phone = request.Phone,
            NationalId = request.NationalId, Notes = request.Notes,
            IsActive = true, CreatedByUserId = GetCurrentUserId(), CreatedAt = DateTime.UtcNow
        };
        _context.SafekeepingAccounts.Add(account);
        await LogAuditAsync($"Safekeeping account opened for '{request.DepositorName}'.");
        await _context.SaveChangesAsync();

        return Ok(new { message = "Safekeeping account created.", account.Id, account.DepositorName });
    }

    // ─── POST /api/safekeeping/accounts/{id}/deposit (auto-approved) ──────────
    [HttpPost("accounts/{id:int}/deposit")]
    public async Task<IActionResult> Deposit(int id, [FromBody] SafekeepingMovementDto request)
    {
        if (request.Amount <= 0) return BadRequest(new { message = "Amount must be greater than zero." });

        var account = await _context.SafekeepingAccounts.FirstOrDefaultAsync(a => a.Id == id);
        if (account == null) return NotFound(new { message = "Account not found." });
        if (!account.IsActive) return BadRequest(new { message = "Account is closed." });

        var userId = GetCurrentUserId();
        var reference = await NextReferenceAsync("SK-D", SafekeepingTransactionType.Deposit);
        _context.SafekeepingTransactions.Add(new SafekeepingTransaction
        {
            AccountId = id, Type = SafekeepingTransactionType.Deposit,
            Amount = request.Amount, Reference = reference, Notes = request.Notes,
            Date = DateTime.UtcNow, ApprovalStatus = CashApprovalStatus.AutoApproved,
            ApprovedByUserId = userId, ApprovedAt = DateTime.UtcNow,
            CreatedByUserId = userId, CreatedAt = DateTime.UtcNow
        });
        await LogAuditAsync($"Safekeeping deposit ${request.Amount:N2} for '{account.DepositorName}'. Ref: {reference}");
        await _context.SaveChangesAsync();

        return Ok(new { message = "Deposit recorded.", reference, balance = await ApprovedBalanceAsync(id) });
    }

    // ─── POST /api/safekeeping/accounts/{id}/withdraw (MAKER → pending) ───────
    [HttpPost("accounts/{id:int}/withdraw")]
    public async Task<IActionResult> Withdraw(int id, [FromBody] SafekeepingMovementDto request)
    {
        if (request.Amount <= 0) return BadRequest(new { message = "Amount must be greater than zero." });

        var account = await _context.SafekeepingAccounts.FirstOrDefaultAsync(a => a.Id == id);
        if (account == null) return NotFound(new { message = "Account not found." });

        var approved = await ApprovedBalanceAsync(id);
        var pending  = await PendingWithdrawalsAsync(id);
        var available = approved - pending;
        if (request.Amount > available)
            return BadRequest(new { message = $"Cannot request ${request.Amount:N2}. Available (after pending requests) is ${available:N2}." });

        var userId = GetCurrentUserId();
        var reference = await NextReferenceAsync("SK-W", SafekeepingTransactionType.Withdrawal);
        _context.SafekeepingTransactions.Add(new SafekeepingTransaction
        {
            AccountId = id, Type = SafekeepingTransactionType.Withdrawal,
            Amount = request.Amount, Reference = reference, Notes = request.Notes,
            Date = DateTime.UtcNow, ApprovalStatus = CashApprovalStatus.Pending,
            CreatedByUserId = userId, CreatedAt = DateTime.UtcNow
        });
        await LogAuditAsync($"Safekeeping collection requested ${request.Amount:N2} for '{account.DepositorName}'. Ref: {reference}. AWAITING APPROVAL.");
        await _context.SaveChangesAsync();

        await _notificationService.NotifyRoleAsync("Manager", "Safekeeping Withdrawal Pending Approval",
            $"${request.Amount:N2} collection for {account.DepositorName} needs approval.", NotificationType.PendingApproval);
        await _notificationService.NotifyRoleAsync("Finance Officer", "Safekeeping Withdrawal Pending Approval",
            $"${request.Amount:N2} collection for {account.DepositorName} needs approval.", NotificationType.PendingApproval);

        return Ok(new { message = "Collection submitted for approval. The depositor can be paid once a checker approves it.", reference, status = "PendingApproval" });
    }

    // ─── GET /api/safekeeping/pending (CHECKER) ───────────────────────────────
    [HttpGet("pending")]
    public async Task<IActionResult> GetPendingWithdrawals()
    {
        var pending = await _context.SafekeepingTransactions
            .Include(t => t.Account)
            .Include(t => t.CreatedByUser)
            .Where(t => t.Type == SafekeepingTransactionType.Withdrawal && t.ApprovalStatus == CashApprovalStatus.Pending)
            .OrderBy(t => t.Date)
            .Select(t => new
            {
                t.Id, t.Date, t.Amount, t.Reference, t.Notes,
                accountId = t.AccountId,
                depositorName = t.Account.DepositorName,
                requestedBy = t.CreatedByUser.FullName
            })
            .ToListAsync();
        return Ok(pending);
    }

    // ─── POST /api/safekeeping/withdrawals/{id}/approve (CHECKER) ─────────────
    [HttpPost("withdrawals/{id:int}/approve")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> ApproveWithdrawal(int id)
    {
        var txn = await _context.SafekeepingTransactions.Include(t => t.Account).FirstOrDefaultAsync(t => t.Id == id);
        if (txn == null) return NotFound(new { message = "Withdrawal not found." });
        if (txn.Type != SafekeepingTransactionType.Withdrawal)
            return BadRequest(new { message = "Only withdrawals require approval." });
        if (txn.ApprovalStatus != CashApprovalStatus.Pending)
            return BadRequest(new { message = $"Withdrawal is not pending. Status: {txn.ApprovalStatus}." });

        // Re-check funds (other approvals may have reduced the balance)
        var approved = await ApprovedBalanceAsync(txn.AccountId);
        if (txn.Amount > approved)
            return BadRequest(new { message = $"Insufficient balance to approve. Available ${approved:N2}, required ${txn.Amount:N2}." });

        var userId = GetCurrentUserId();
        txn.ApprovalStatus = CashApprovalStatus.Approved;
        txn.ApprovedByUserId = userId;
        txn.ApprovedAt = DateTime.UtcNow;
        txn.UpdatedAt = DateTime.UtcNow;

        await LogAuditAsync($"Safekeeping withdrawal APPROVED ${txn.Amount:N2} for '{txn.Account.DepositorName}'. Ref: {txn.Reference}");
        await _context.SaveChangesAsync();

        return Ok(new { message = "Withdrawal approved. Depositor can be paid.", balance = await ApprovedBalanceAsync(txn.AccountId) });
    }

    // ─── POST /api/safekeeping/withdrawals/{id}/reject (CHECKER) ──────────────
    [HttpPost("withdrawals/{id:int}/reject")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> RejectWithdrawal(int id, [FromBody] string reason)
    {
        var txn = await _context.SafekeepingTransactions.Include(t => t.Account).FirstOrDefaultAsync(t => t.Id == id);
        if (txn == null) return NotFound(new { message = "Withdrawal not found." });
        if (txn.ApprovalStatus != CashApprovalStatus.Pending)
            return BadRequest(new { message = "Withdrawal is not pending." });

        var userId = GetCurrentUserId();
        txn.ApprovalStatus = CashApprovalStatus.Rejected;
        txn.RejectionReason = reason;
        txn.ApprovedByUserId = userId;
        txn.UpdatedAt = DateTime.UtcNow;

        await LogAuditAsync($"Safekeeping withdrawal REJECTED ${txn.Amount:N2} for '{txn.Account.DepositorName}'. Reason: {reason}");
        await _context.SaveChangesAsync();

        await _notificationService.NotifyUserAsync(txn.CreatedByUserId, "Safekeeping Withdrawal Rejected",
            $"Collection of ${txn.Amount:N2} for {txn.Account.DepositorName} was rejected. Reason: {reason}", NotificationType.PendingApproval);

        return Ok(new { message = "Withdrawal rejected.", reason });
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────
    private async Task<decimal> ApprovedBalanceAsync(int accountId)
    {
        var deposits = await _context.SafekeepingTransactions
            .Where(t => t.AccountId == accountId && t.Type == SafekeepingTransactionType.Deposit)
            .SumAsync(t => (decimal?)t.Amount) ?? 0m;
        var withdrawals = await _context.SafekeepingTransactions
            .Where(t => t.AccountId == accountId && t.Type == SafekeepingTransactionType.Withdrawal &&
                       (t.ApprovalStatus == CashApprovalStatus.AutoApproved || t.ApprovalStatus == CashApprovalStatus.Approved))
            .SumAsync(t => (decimal?)t.Amount) ?? 0m;
        return deposits - withdrawals;
    }

    private async Task<decimal> PendingWithdrawalsAsync(int accountId)
    {
        return await _context.SafekeepingTransactions
            .Where(t => t.AccountId == accountId && t.Type == SafekeepingTransactionType.Withdrawal && t.ApprovalStatus == CashApprovalStatus.Pending)
            .SumAsync(t => (decimal?)t.Amount) ?? 0m;
    }

    private async Task<string> NextReferenceAsync(string prefix, SafekeepingTransactionType type)
    {
        var todayUtc = DateTime.SpecifyKind(DateTime.UtcNow.Date, DateTimeKind.Utc);
        var tomorrowUtc = todayUtc.AddDays(1);
        var countToday = await _context.SafekeepingTransactions
            .CountAsync(t => t.Type == type && t.Date >= todayUtc && t.Date < tomorrowUtc);
        return $"{prefix}-{todayUtc:yyyyMMdd}-{(countToday + 1):D4}";
    }

    private async Task LogAuditAsync(string action)
    {
        _context.AuditLogs.Add(new AuditLog
        {
            Action = action, IpAddress = GetClientIp(), DeviceInfo = GetDeviceInfo(),
            Timestamp = DateTime.UtcNow, UserId = GetCurrentUserId(), CreatedAt = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();
    }
}
