using ClosedXML.Excel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using CashLoanManagement.Application.Common.Interfaces;
using CashLoanManagement.Domain.Entities;
using CashLoanManagement.Domain.Enums;
using CashLoanManagement.Infrastructure.Persistence;

namespace CashLoanManagement.Api.Controllers;

public class AccountantMovementDto
{
    public decimal Amount { get; set; }
    public string SourceOrPurpose { get; set; } = string.Empty;
}

/// <summary>
/// The accountant's own cash book — a separate float from the main
/// operational cashbox. Accessible to Accountant, Admin and Manager.
/// </summary>
[Authorize(Roles = "Admin,Manager,Accountant,Finance Officer,Auditor")]
public class AccountantController : BaseApiController
{
    private readonly CashLoanDbContext _context;
    private readonly INotificationService _notificationService;

    private static bool IsPosted(CashApprovalStatus s) => s == CashApprovalStatus.AutoApproved || s == CashApprovalStatus.Approved;

    public AccountantController(CashLoanDbContext context, INotificationService notificationService)
    {
        _context = context;
        _notificationService = notificationService;
    }

    // ─── GET /api/accountant/balance ──────────────────────────────────────────
    [HttpGet("balance")]
    public async Task<IActionResult> GetBalance()
    {
        var initialized = await _context.AccountantTransactions.AnyAsync();
        return Ok(new { currentBalance = await ComputeBalanceAsync(), currency = "USD", initialized });
    }

    // ─── POST /api/accountant/opening-balance (one-time) ──────────────────────
    [HttpPost("opening-balance")]
    [Authorize(Roles = "Admin,Accountant")]
    public async Task<IActionResult> SetInitialOpeningBalance([FromBody] AccountantMovementDto request)
    {
        if (request.Amount < 0) return BadRequest(new { message = "Opening balance cannot be negative." });
        if (await _context.AccountantTransactions.AnyAsync())
            return BadRequest(new { message = "Initial opening balance can only be set once. The book carries forward automatically." });

        var userId = GetCurrentUserId();
        _context.AccountantTransactions.Add(new AccountantTransaction
        {
            Amount = request.Amount, Type = TransactionType.OpeningBalance,
            SourceOrPurpose = string.IsNullOrWhiteSpace(request.SourceOrPurpose) ? "Initial Opening Balance" : request.SourceOrPurpose,
            Reference = $"ACC-OB-{DateTime.UtcNow:yyyyMMdd}",
            ApprovalStatus = CashApprovalStatus.AutoApproved,
            ApprovedByUserId = userId, ApprovedAt = DateTime.UtcNow,
            Date = DateTime.UtcNow, CreatedByUserId = userId, CreatedAt = DateTime.UtcNow
        });
        await LogAuditAsync($"Accountant book initial balance set: ${request.Amount:N2}");
        await _context.SaveChangesAsync();
        return Ok(new { message = "Accountant book opening balance recorded.", openingBalance = request.Amount });
    }

    // ─── POST /api/accountant/add (MAKER → pending) ───────────────────────────
    [HttpPost("add")]
    [Authorize(Roles = "Admin,Accountant")]
    public async Task<IActionResult> AddCash([FromBody] AccountantMovementDto request)
    {
        if (request.Amount <= 0) return BadRequest(new { message = "Amount must be greater than zero." });

        var userId    = GetCurrentUserId();
        var reference = await NextReferenceAsync("ACC-ADD", TransactionType.Addition);
        _context.AccountantTransactions.Add(new AccountantTransaction
        {
            Amount = request.Amount, Type = TransactionType.Addition,
            SourceOrPurpose = request.SourceOrPurpose, Reference = reference,
            ApprovalStatus = CashApprovalStatus.Pending,
            Date = DateTime.UtcNow, CreatedByUserId = userId, CreatedAt = DateTime.UtcNow
        });
        await LogAuditAsync($"Accountant book: cash in ${request.Amount:N2} — {request.SourceOrPurpose}. Ref: {reference}. AWAITING APPROVAL.");
        await _context.SaveChangesAsync();

        await _notificationService.NotifyRoleAsync("Manager", "Accountant Book Cash In Pending Approval",
            $"${request.Amount:N2} cash-in ({request.SourceOrPurpose}) needs approval.", NotificationType.PendingApproval);

        return Ok(new { message = "Cash-in submitted for approval. It will reflect in the balance once a checker approves it.", reference, status = "PendingApproval" });
    }

    // ─── POST /api/accountant/disburse (MAKER → pending) ──────────────────────
    [HttpPost("disburse")]
    [Authorize(Roles = "Admin,Accountant")]
    public async Task<IActionResult> Disburse([FromBody] AccountantMovementDto request)
    {
        if (request.Amount <= 0) return BadRequest(new { message = "Amount must be greater than zero." });

        var approved = await ComputeBalanceAsync();
        var pending  = await PendingDisbursementsAsync();
        var available = approved - pending;
        if (request.Amount > available)
            return BadRequest(new { message = $"Cannot request ${request.Amount:N2}. Available (after pending requests): ${available:N2}." });

        var userId    = GetCurrentUserId();
        var reference = await NextReferenceAsync("ACC-DISB", TransactionType.Disbursement);
        _context.AccountantTransactions.Add(new AccountantTransaction
        {
            Amount = request.Amount, Type = TransactionType.Disbursement,
            SourceOrPurpose = request.SourceOrPurpose, Reference = reference,
            ApprovalStatus = CashApprovalStatus.Pending,
            Date = DateTime.UtcNow, CreatedByUserId = userId, CreatedAt = DateTime.UtcNow
        });
        await LogAuditAsync($"Accountant book: cash out ${request.Amount:N2} — {request.SourceOrPurpose}. Ref: {reference}. AWAITING APPROVAL.");
        await _context.SaveChangesAsync();

        await _notificationService.NotifyRoleAsync("Manager", "Accountant Book Cash Out Pending Approval",
            $"${request.Amount:N2} cash-out ({request.SourceOrPurpose}) needs approval.", NotificationType.PendingApproval);

        return Ok(new { message = "Cash-out submitted for approval. It will reflect in the balance once a checker approves it.", reference, status = "PendingApproval" });
    }

    // ─── GET /api/accountant/pending (CHECKER) ────────────────────────────────
    [HttpGet("pending")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> GetPendingMovements()
    {
        var pending = await _context.AccountantTransactions
            .Include(t => t.CreatedByUser)
            .Where(t => t.ApprovalStatus == CashApprovalStatus.Pending)
            .OrderBy(t => t.Date)
            .Select(t => new
            {
                t.Id, t.Date, t.Amount, type = t.Type.ToString(),
                t.SourceOrPurpose, t.Reference,
                requestedBy = t.CreatedByUser.FullName
            })
            .ToListAsync();
        return Ok(pending);
    }

    // ─── POST /api/accountant/approve/{id} (CHECKER) ──────────────────────────
    [HttpPost("approve/{id:int}")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> ApproveMovement(int id)
    {
        var txn = await _context.AccountantTransactions.FirstOrDefaultAsync(t => t.Id == id);
        if (txn == null) return NotFound(new { message = "Transaction not found." });
        if (txn.ApprovalStatus != CashApprovalStatus.Pending)
            return BadRequest(new { message = $"Transaction is not pending. Status: {txn.ApprovalStatus}." });
        if (txn.CreatedByUserId == GetCurrentUserId())
            return BadRequest(new { message = "You cannot approve your own request." });

        var userId = GetCurrentUserId();
        var action = txn.Type == TransactionType.Addition ? "cash-in" : "cash-out";

        if (txn.Type == TransactionType.Disbursement)
        {
            var approvedOk = await AdvisoryLock.WithLockAsync(_context, AdvisoryLock.AccountantBook, async () =>
            {
                var available = await ComputeBalanceAsync();
                if (txn.Amount > available) return false;

                txn.ApprovalStatus = CashApprovalStatus.Approved;
                txn.ApprovedByUserId = userId;
                txn.ApprovedAt = DateTime.UtcNow;
                txn.UpdatedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();
                return true;
            });
            if (!approvedOk)
            {
                var available = await ComputeBalanceAsync();
                return BadRequest(new { message = $"Insufficient accountant book balance to approve. Available: ${available:N2}, required ${txn.Amount:N2}." });
            }
        }
        else
        {
            txn.ApprovalStatus = CashApprovalStatus.Approved;
            txn.ApprovedByUserId = userId;
            txn.ApprovedAt = DateTime.UtcNow;
            txn.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
        }

        await LogAuditAsync($"Accountant book: {action} ${txn.Amount:N2} APPROVED. Ref: {txn.Reference}");

        await _notificationService.NotifyUserAsync(txn.CreatedByUserId, "Accountant Book Entry Approved",
            $"Your {action} of ${txn.Amount:N2} ({txn.SourceOrPurpose}) has been approved.", NotificationType.PendingApproval);

        return Ok(new { message = $"{char.ToUpper(action[0])}{action[1..]} approved.", newBalance = await ComputeBalanceAsync() });
    }

    // ─── POST /api/accountant/reject/{id} (CHECKER) ───────────────────────────
    [HttpPost("reject/{id:int}")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> RejectMovement(int id, [FromBody] string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return BadRequest(new { message = "A reason for rejection is required." });

        var txn = await _context.AccountantTransactions.FirstOrDefaultAsync(t => t.Id == id);
        if (txn == null) return NotFound(new { message = "Transaction not found." });
        if (txn.ApprovalStatus != CashApprovalStatus.Pending)
            return BadRequest(new { message = "Transaction is not pending." });

        var userId = GetCurrentUserId();
        txn.ApprovalStatus = CashApprovalStatus.Rejected;
        txn.RejectionReason = reason;
        txn.ApprovedByUserId = userId;
        txn.UpdatedAt = DateTime.UtcNow;

        var action = txn.Type == TransactionType.Addition ? "cash-in" : "cash-out";
        await LogAuditAsync($"Accountant book: {action} ${txn.Amount:N2} REJECTED. Reason: {reason}");
        await _context.SaveChangesAsync();

        await _notificationService.NotifyUserAsync(txn.CreatedByUserId, "Accountant Book Entry Rejected",
            $"Your {action} of ${txn.Amount:N2} ({txn.SourceOrPurpose}) was rejected. Reason: {reason}", NotificationType.PendingApproval);

        return Ok(new { message = $"{char.ToUpper(action[0])}{action[1..]} rejected.", reason });
    }

    // ─── GET /api/accountant/transactions ─────────────────────────────────────
    [HttpGet("transactions")]
    public async Task<IActionResult> GetLedger([FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        if (pageSize > 200) pageSize = 200;
        var query = _context.AccountantTransactions.Include(t => t.CreatedByUser);
        var total = await query.CountAsync();
        var txns  = await query.OrderByDescending(t => t.Date)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(t => new
            {
                t.Id, t.Date, t.Amount, type = t.Type.ToString(),
                t.SourceOrPurpose, t.Reference, createdBy = t.CreatedByUser.FullName,
                approvalStatus = t.ApprovalStatus.ToString(),
                t.IsReversed,
                isReversal = t.ReversalOfTransactionId != null,
                reversalStatus = t.ReversalStatus != null ? t.ReversalStatus.ToString() : null
            })
            .ToListAsync();
        return Ok(new { total, page, pageSize, transactions = txns });
    }

    // ─── POST /api/accountant/reverse/{id} (MAKER: Accountant/Admin request) ──
    [HttpPost("reverse/{id:int}")]
    [Authorize(Roles = "Admin,Accountant")]
    public async Task<IActionResult> RequestReversal(int id, [FromBody] string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return BadRequest(new { message = "A reason for the reversal is required." });

        var original = await _context.AccountantTransactions.FirstOrDefaultAsync(t => t.Id == id);
        if (original == null) return NotFound(new { message = "Transaction not found." });
        if (!IsPosted(original.ApprovalStatus))
            return BadRequest(new { message = "Only posted (approved) transactions can be reversed. This entry is still pending or was rejected." });
        if (original.ReversalOfTransactionId != null)
            return BadRequest(new { message = "You cannot reverse a reversal entry." });
        if (original.IsReversed)
            return BadRequest(new { message = "This transaction has already been reversed." });
        if (original.ReversalStatus == CashApprovalStatus.Pending)
            return BadRequest(new { message = "A reversal request is already pending for this transaction." });

        var userId = GetCurrentUserId();
        original.ReversalStatus = CashApprovalStatus.Pending;
        original.ReversalReason = reason;
        original.ReversalRequestedByUserId = userId;
        original.ReversalRequestedAt = DateTime.UtcNow;
        original.UpdatedAt = DateTime.UtcNow;

        await LogAuditAsync($"Accountant book: reversal requested for {original.Reference} (${original.Amount:N2}). Reason: {reason}. AWAITING APPROVAL.");
        await _context.SaveChangesAsync();

        await _notificationService.NotifyRoleAsync("Manager", "Accountant Book Reversal Pending Approval",
            $"Reversal of {original.Reference} (${original.Amount:N2}) needs your approval.", NotificationType.PendingApproval);

        return Ok(new { message = "Reversal requested and sent for approval.", status = "PendingApproval" });
    }

    // ─── GET /api/accountant/pending-reversals (CHECKER) ──────────────────────
    [HttpGet("pending-reversals")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> GetPendingReversals()
    {
        var pending = await _context.AccountantTransactions
            .Include(t => t.CreatedByUser)
            .Include(t => t.ReversalRequestedByUser)
            .Where(t => t.ReversalStatus == CashApprovalStatus.Pending)
            .OrderBy(t => t.ReversalRequestedAt)
            .Select(t => new
            {
                t.Id, t.Date, t.Amount, type = t.Type.ToString(), t.SourceOrPurpose, t.Reference,
                t.ReversalReason, t.ReversalRequestedAt,
                requestedBy = t.ReversalRequestedByUser!.FullName,
                originalPostedBy = t.CreatedByUser.FullName
            })
            .ToListAsync();
        return Ok(pending);
    }

    // ─── POST /api/accountant/reverse/{id}/approve (CHECKER) ──────────────────
    [HttpPost("reverse/{id:int}/approve")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> ApproveReversal(int id)
    {
        var original = await _context.AccountantTransactions.FirstOrDefaultAsync(t => t.Id == id);
        if (original == null) return NotFound(new { message = "Transaction not found." });
        if (original.ReversalStatus != CashApprovalStatus.Pending)
            return BadRequest(new { message = "No pending reversal request for this transaction." });
        if (original.ReversalRequestedByUserId == GetCurrentUserId())
            return BadRequest(new { message = "You cannot approve your own reversal request." });

        var userId = GetCurrentUserId();
        _context.AccountantTransactions.Add(new AccountantTransaction
        {
            Amount          = -original.Amount,
            Type            = original.Type,
            SourceOrPurpose = $"Reversal of {original.Reference}: {original.ReversalReason}",
            Reference       = $"REV-{original.Reference}",
            Date            = DateTime.UtcNow,
            ReversalOfTransactionId = original.Id,
            ApprovalStatus  = CashApprovalStatus.Approved,
            ApprovedByUserId = userId, ApprovedAt = DateTime.UtcNow,
            CreatedByUserId = userId, CreatedAt = DateTime.UtcNow
        });

        original.IsReversed = true;
        original.ReversalStatus = CashApprovalStatus.Approved;
        original.ReversalApprovedByUserId = userId;
        original.ReversalApprovedAt = DateTime.UtcNow;
        original.UpdatedAt = DateTime.UtcNow;

        await LogAuditAsync($"Accountant book: reversal APPROVED for {original.Reference} (${original.Amount:N2}).");
        await _context.SaveChangesAsync();

        if (original.ReversalRequestedByUserId.HasValue)
            await _notificationService.NotifyUserAsync(original.ReversalRequestedByUserId.Value, "Reversal Approved",
                $"Your accountant-book reversal request for {original.Reference} was approved.", NotificationType.PendingApproval);

        return Ok(new { message = $"Transaction {original.Reference} reversed.", newBalance = await ComputeBalanceAsync() });
    }

    // ─── POST /api/accountant/reverse/{id}/reject (CHECKER) ───────────────────
    [HttpPost("reverse/{id:int}/reject")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> RejectReversal(int id, [FromBody] string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return BadRequest(new { message = "A reason for rejection is required." });

        var original = await _context.AccountantTransactions.FirstOrDefaultAsync(t => t.Id == id);
        if (original == null) return NotFound(new { message = "Transaction not found." });
        if (original.ReversalStatus != CashApprovalStatus.Pending)
            return BadRequest(new { message = "No pending reversal request for this transaction." });

        var userId = GetCurrentUserId();
        original.ReversalStatus = CashApprovalStatus.Rejected;
        original.ReversalRejectionReason = reason;
        original.ReversalApprovedByUserId = userId;
        original.ReversalApprovedAt = DateTime.UtcNow;
        original.UpdatedAt = DateTime.UtcNow;

        await LogAuditAsync($"Accountant book: reversal REJECTED for {original.Reference}. Reason: {reason}");
        await _context.SaveChangesAsync();

        if (original.ReversalRequestedByUserId.HasValue)
            await _notificationService.NotifyUserAsync(original.ReversalRequestedByUserId.Value, "Reversal Rejected",
                $"Your accountant-book reversal request for {original.Reference} was rejected. Reason: {reason}", NotificationType.PendingApproval);

        return Ok(new { message = "Reversal request rejected.", reason });
    }

    // ─── GET /api/accountant/daily-report (period=daily|weekly|monthly) ───────
    [HttpGet("daily-report")]
    public async Task<IActionResult> GetDailyReport(
        [FromQuery] string? date, [FromQuery] string period = "daily",
        [FromQuery] string? from = null, [FromQuery] string? to = null)
    {
        var (dayStart, dayEnd, _, _) = ResolveRange(period, date, from, to);
        var txns = await DayTransactionsAsync(dayStart, dayEnd);
        var opening = await BalanceBeforeAsync(dayStart);
        var added     = txns.Where(t => (t.ApprovalStatus == CashApprovalStatus.AutoApproved || t.ApprovalStatus == CashApprovalStatus.Approved) && (t.Type == TransactionType.Addition || t.Type == TransactionType.OpeningBalance)).Sum(t => t.Amount);
        var disbursed = txns.Where(t => (t.ApprovalStatus == CashApprovalStatus.AutoApproved || t.ApprovalStatus == CashApprovalStatus.Approved) && t.Type == TransactionType.Disbursement).Sum(t => t.Amount);

        return Ok(new
        {
            date = dayStart.ToString("yyyy-MM-dd"),
            openingBalance = opening, totalAdded = added, totalDisbursed = disbursed,
            closingBalance = opening + added - disbursed,
            transactions = txns.Select(t => new
            {
                t.Id, t.Date, t.Amount, type = t.Type.ToString(),
                t.SourceOrPurpose, t.Reference, createdBy = t.CreatedByUser.FullName,
                approvalStatus = t.ApprovalStatus.ToString(),
                t.IsReversed,
                isReversal = t.ReversalOfTransactionId != null,
                reversalStatus = t.ReversalStatus != null ? t.ReversalStatus.ToString() : null
            })
        });
    }

    // ─── GET /api/accountant/daily-report/export (period + xlsx/pdf) ──────────
    [HttpGet("daily-report/export")]
    public async Task<IActionResult> ExportDailyReport(
        [FromQuery] string? date,
        [FromQuery] string period = "daily",
        [FromQuery] string? from = null,
        [FromQuery] string? to = null,
        [FromQuery] string format = "xlsx")
    {
        var (dayStart, dayEnd, label, slug) = ResolveRange(period, date, from, to);
        var txns = await DayTransactionsAsync(dayStart, dayEnd);
        var opening   = await BalanceBeforeAsync(dayStart);
        var added     = txns.Where(t => (t.ApprovalStatus == CashApprovalStatus.AutoApproved || t.ApprovalStatus == CashApprovalStatus.Approved) && (t.Type == TransactionType.Addition || t.Type == TransactionType.OpeningBalance)).Sum(t => t.Amount);
        var disbursed = txns.Where(t => (t.ApprovalStatus == CashApprovalStatus.AutoApproved || t.ApprovalStatus == CashApprovalStatus.Approved) && t.Type == TransactionType.Disbursement).Sum(t => t.Amount);
        var closing   = opening + added - disbursed;

        // Exclude pending/rejected entries from the posted DR/CR ledger export
        txns = txns.Where(t => (t.ApprovalStatus == CashApprovalStatus.AutoApproved || t.ApprovalStatus == CashApprovalStatus.Approved)).ToList();

        var periodName = from != null && to != null ? "Range" : char.ToUpper(period[0]) + period[1..].ToLower();
        var title    = $"Accountant Cash Report — {label}";
        var fileName = $"CALM_Accountant_{periodName}_{slug}";

        if (format.ToLower() == "pdf")
        {
            var pdfRows = txns.Select(t =>
            {
                var isDebit = t.Type == TransactionType.Disbursement;
                return new[]
                {
                    t.Date.ToLocalTime().ToString("dd MMM HH:mm"),
                    t.Type.ToString(),
                    isDebit ? $"!r-${t.Amount:N2}" : "",
                    isDebit ? "" : $"!g${t.Amount:N2}",
                    t.SourceOrPurpose, t.Reference, t.CreatedByUser.FullName
                };
            }).ToList();
            var pms = ReportPdf.Build(title,
                new List<(string, string)>
                {
                    ("Opening Balance", $"${opening:N2}"),
                    ("Cash Added",      $"${added:N2}"),
                    ("Cash Disbursed",  $"${disbursed:N2}"),
                    ("Closing Balance", $"${closing:N2}")
                },
                new[] { "Time", "Type", "DR Amount (USD)", "CR Amount (USD)", "Source / Purpose", "Reference", "Recorded By" },
                pdfRows, rightAlign: new[] { 2, 3 });
            return File(pms, "application/pdf", $"{fileName}.pdf");
        }

        var rows = txns.Select(t => (
            Time: t.Date, Type: t.Type, Amount: t.Amount,
            Source: t.SourceOrPurpose, Reference: t.Reference,
            By: t.CreatedByUser.FullName)).ToList();

        var ms = DrCrExcel.Build(title, opening, added, disbursed, closing, rows);
        return File(ms, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"{fileName}.xlsx");
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────
    private static (DateTime start, DateTime end, string label, string slug) ResolveRange(
        string? period, string? date, string? from = null, string? to = null)
    {
        if (from != null && to != null)
            return PeriodUtil.Range(period, date, from, to);
        if (string.Equals(period, "weekly", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(period, "monthly", StringComparison.OrdinalIgnoreCase))
            return PeriodUtil.Range(period, date);

        var raw = DateTime.TryParse(date, out var p) ? p : DateTime.UtcNow;
        var s = DateTime.SpecifyKind(raw.Date, DateTimeKind.Utc);
        return (s, s.AddDays(1), s.ToString("dd MMM yyyy"), s.ToString("yyyyMMdd"));
    }

    private Task<List<AccountantTransaction>> DayTransactionsAsync(DateTime s, DateTime e) =>
        _context.AccountantTransactions.Include(t => t.CreatedByUser)
            .Where(t => t.Date >= s && t.Date < e).OrderBy(t => t.Date).ToListAsync();

    private async Task<decimal> ComputeBalanceAsync()
    {
        var credits = await _context.AccountantTransactions
            .Where(t => (t.Type == TransactionType.OpeningBalance || t.Type == TransactionType.Addition) &&
                        (t.ApprovalStatus == CashApprovalStatus.AutoApproved || t.ApprovalStatus == CashApprovalStatus.Approved))
            .SumAsync(t => (decimal?)t.Amount) ?? 0m;
        var debits = await _context.AccountantTransactions
            .Where(t => t.Type == TransactionType.Disbursement &&
                        (t.ApprovalStatus == CashApprovalStatus.AutoApproved || t.ApprovalStatus == CashApprovalStatus.Approved))
            .SumAsync(t => (decimal?)t.Amount) ?? 0m;
        return credits - debits;
    }

    private async Task<decimal> BalanceBeforeAsync(DateTime beforeUtc)
    {
        var credits = await _context.AccountantTransactions
            .Where(t => (t.Type == TransactionType.OpeningBalance || t.Type == TransactionType.Addition) && t.Date < beforeUtc &&
                        (t.ApprovalStatus == CashApprovalStatus.AutoApproved || t.ApprovalStatus == CashApprovalStatus.Approved))
            .SumAsync(t => (decimal?)t.Amount) ?? 0m;
        var debits = await _context.AccountantTransactions
            .Where(t => t.Type == TransactionType.Disbursement && t.Date < beforeUtc &&
                        (t.ApprovalStatus == CashApprovalStatus.AutoApproved || t.ApprovalStatus == CashApprovalStatus.Approved))
            .SumAsync(t => (decimal?)t.Amount) ?? 0m;
        return credits - debits;
    }

    private async Task<decimal> PendingDisbursementsAsync()
    {
        return await _context.AccountantTransactions
            .Where(t => t.Type == TransactionType.Disbursement && t.ApprovalStatus == CashApprovalStatus.Pending)
            .SumAsync(t => (decimal?)t.Amount) ?? 0m;
    }

    private async Task<string> NextReferenceAsync(string prefix, TransactionType type)
    {
        var todayUtc = DateTime.SpecifyKind(DateTime.UtcNow.Date, DateTimeKind.Utc);
        var count = await _context.AccountantTransactions
            .CountAsync(t => t.Type == type && t.Date >= todayUtc && t.Date < todayUtc.AddDays(1));
        return $"{prefix}-{todayUtc:yyyyMMdd}-{(count + 1):D4}";
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

/// <summary>
/// Shared DR/CR-format Excel builder for cash reports:
/// Summary block, then Time | Type | DR Amount | CR Amount | Source/Purpose | Reference | Recorded By.
/// Disbursements go in the DR column (red, negative); credits in the CR column (green).
/// </summary>
public static class DrCrExcel
{
    public static MemoryStream Build(
        string title, decimal opening, decimal added, decimal disbursed, decimal closing,
        List<(DateTime Time, TransactionType Type, decimal Amount, string Source, string Reference, string By)> rows)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Daily Cash");

        ws.Cell("A1").Value = "CALM – Cash & Liquidity Management";
        ws.Cell("A1").Style.Font.Bold = true; ws.Cell("A1").Style.Font.FontSize = 14;
        ws.Cell("A2").Value = "Welble Investments P/L";
        ws.Cell("A2").Style.Font.FontColor = XLColor.Gray;
        ws.Cell("A3").Value = title;
        ws.Cell("A3").Style.Font.Bold = true;
        ws.Cell("A4").Value = $"Generated: {DateTime.Now:dd MMM yyyy HH:mm}";
        ws.Cell("A4").Style.Font.FontColor = XLColor.Gray; ws.Cell("A4").Style.Font.FontSize = 9;

        // Summary
        ws.Cell("A6").Value = "SUMMARY";
        ws.Cell("A6").Style.Font.Bold = true;
        ws.Range("A6:B6").Merge().Style.Fill.BackgroundColor = XLColor.FromHtml("#f59e0b");
        int r = 7;
        void Sum(string label, decimal v, bool bold = false)
        {
            ws.Cell(r, 1).Value = label;
            ws.Cell(r, 2).Value = v;
            ws.Cell(r, 2).Style.NumberFormat.Format = "$#,##0.00";
            if (bold) { ws.Cell(r, 1).Style.Font.Bold = true; ws.Cell(r, 2).Style.Font.Bold = true; }
            r++;
        }
        Sum("Opening Balance", opening);
        Sum("Cash Added",      added);
        Sum("Cash Disbursed",  disbursed);
        Sum("Closing Balance", closing, bold: true);

        // DR/CR table
        r += 1;
        var headers = new[] { "Time", "Type", "DR Amount (USD)", "CR Amount (USD)", "Source / Purpose", "Reference", "Recorded By" };
        for (int c = 0; c < headers.Length; c++)
        {
            ws.Cell(r, c + 1).Value = headers[c];
            ws.Cell(r, c + 1).Style.Font.Bold = true;
            ws.Cell(r, c + 1).Style.Fill.BackgroundColor = XLColor.FromHtml("#1e293b");
            ws.Cell(r, c + 1).Style.Font.FontColor = XLColor.White;
        }
        r++;

        foreach (var row in rows)
        {
            var isDebit = row.Type == TransactionType.Disbursement;
            ws.Cell(r, 1).Value = row.Time.ToLocalTime().ToString("HH:mm");
            ws.Cell(r, 2).Value = row.Type.ToString();
            if (isDebit)
            {
                ws.Cell(r, 3).Value = -Math.Abs(row.Amount);
                ws.Cell(r, 3).Style.NumberFormat.Format = "$#,##0.00";
                ws.Cell(r, 3).Style.Font.FontColor = XLColor.Red;
            }
            else
            {
                ws.Cell(r, 4).Value = row.Amount;
                ws.Cell(r, 4).Style.NumberFormat.Format = "$#,##0.00";
                ws.Cell(r, 4).Style.Font.FontColor = XLColor.DarkGreen;
            }
            ws.Cell(r, 5).Value = row.Source;
            ws.Cell(r, 6).Value = row.Reference;
            ws.Cell(r, 7).Value = row.By;
            r++;
        }

        ws.Columns().AdjustToContents();
        var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;
        return ms;
    }
}
