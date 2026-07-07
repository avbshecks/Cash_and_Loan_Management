using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using CashLoanManagement.Application.Common.Interfaces;
using CashLoanManagement.Application.DTOs.Cash;
using CashLoanManagement.Domain.Entities;
using CashLoanManagement.Domain.Enums;
using CashLoanManagement.Infrastructure.Persistence;

namespace CashLoanManagement.Api.Controllers;

[Authorize]
public class CashController : BaseApiController
{
    private readonly CashLoanDbContext _context;
    private readonly INotificationService _notificationService;
    private const decimal LowCashThreshold = 5000m;

    public CashController(CashLoanDbContext context, INotificationService notificationService)
    {
        _context = context;
        _notificationService = notificationService;
    }

    // ─── GET /api/cash/balance ────────────────────────────────────────────────
    [HttpGet("balance")]
    public async Task<IActionResult> GetBalance()
    {
        var balance = await ComputeBalanceAsync();
        var pendingCount = await _context.CashTransactions
            .CountAsync(t => (t.Type == TransactionType.Disbursement || t.Type == TransactionType.Addition)
                             && t.ApprovalStatus == CashApprovalStatus.Pending);
        var initialized = await _context.CashTransactions.AnyAsync();
        return Ok(new { currentBalance = balance, currency = "USD", pendingDisbursements = pendingCount, initialized });
    }

    // ─── POST /api/cash/opening-balance (ONE-TIME initial setup only) ─────────
    /// <summary>
    /// Captures the system's very first opening balance (initial cash at inception).
    /// Allowed only when no cash transactions exist yet. From then on each day's
    /// opening balance is automatically the previous day's closing balance — there
    /// is no daily manual capture.
    /// </summary>
    [HttpPost("opening-balance")]
    [Authorize(Roles = "Admin,Cashier")]
    public async Task<IActionResult> SetInitialOpeningBalance([FromBody] SetOpeningBalanceDto request)
    {
        if (request.Amount < 0)
            return BadRequest(new { message = "Opening balance cannot be negative." });

        var anyExisting = await _context.CashTransactions.AnyAsync();
        if (anyExisting)
            return BadRequest(new { message = "Initial opening balance can only be set once. Each day's opening balance now carries forward automatically from the previous day's closing balance." });

        var userId = GetCurrentUserId();
        _context.CashTransactions.Add(new CashTransaction
        {
            Amount = request.Amount, Type = TransactionType.OpeningBalance,
            SourceOrPurpose = "Initial Opening Balance",
            Reference = $"OB-INIT-{DateTime.UtcNow:yyyyMMdd}",
            Date = DateTime.UtcNow, ApprovalStatus = CashApprovalStatus.AutoApproved,
            ApprovedByUserId = userId, ApprovedAt = DateTime.UtcNow,
            CreatedByUserId = userId, CreatedAt = DateTime.UtcNow
        });
        await LogAuditAsync(userId, $"Initial opening balance set: ${request.Amount:N2}{(request.Notes != null ? " — " + request.Notes : "")}");
        await _context.SaveChangesAsync();

        return Ok(new { message = "Initial opening balance recorded.", openingBalance = request.Amount });
    }

    // ─── POST /api/cash/add (MAKER — now requires checker approval) ───────────
    [HttpPost("add")]
    [Authorize(Roles = "Admin,Cashier")]
    public async Task<IActionResult> AddCash([FromBody] AddCashDto request)
    {
        if (request.Amount <= 0) return BadRequest(new { message = "Amount must be greater than zero." });

        var userId    = GetCurrentUserId();
        var reference = await NextReferenceAsync("ADD", TransactionType.Addition);
        var tx = new CashTransaction
        {
            Amount = request.Amount, Type = TransactionType.Addition,
            SourceOrPurpose = request.Source, Reference = reference,
            Date = DateTime.UtcNow,
            ApprovalStatus = CashApprovalStatus.Pending,   // ← awaiting checker; balance unaffected until approved
            CreatedByUserId = userId, CreatedAt = DateTime.UtcNow
        };
        _context.CashTransactions.Add(tx);
        await LogAuditAsync(userId, $"Cash addition requested: ${request.Amount:N2} from '{request.Source}'. Ref: {reference}. AWAITING APPROVAL.");
        await _context.SaveChangesAsync();

        await _notificationService.NotifyRoleAsync("Manager", "Cash Addition Pending Approval",
            $"${request.Amount:N2} from '{request.Source}' needs your approval.", NotificationType.PendingApproval);

        return Ok(new { message = "Cash addition recorded and sent for approval.", transactionId = tx.Id, reference, status = "PendingApproval" });
    }

    // ─── POST /api/cash/disburse (MAKER) ─────────────────────────────────────
    [HttpPost("disburse")]
    [Authorize(Roles = "Admin,Cashier")]
    public async Task<IActionResult> DisburseCash([FromBody] DisburseCashDto request)
    {
        if (request.Amount <= 0) return BadRequest(new { message = "Amount must be greater than zero." });

        var currentBalance = await ComputeBalanceAsync();
        if (request.Amount > currentBalance)
            return BadRequest(new { message = $"Insufficient balance. Current balance: ${currentBalance:N2}." });

        var userId    = GetCurrentUserId();
        var reference = await NextReferenceAsync("DISB", TransactionType.Disbursement);
        var tx = new CashTransaction
        {
            Amount = request.Amount, Type = TransactionType.Disbursement,
            SourceOrPurpose = $"{request.Recipient} — {request.Purpose}",
            Reference = reference,
            Date = DateTime.UtcNow,
            ApprovalStatus = CashApprovalStatus.Pending,   // ← awaiting checker
            CreatedByUserId = userId, CreatedAt = DateTime.UtcNow
        };
        _context.CashTransactions.Add(tx);
        await LogAuditAsync(userId, $"Cash disbursement requested: ${request.Amount:N2} to '{request.Recipient}' for '{request.Purpose}'. Ref: {reference}. AWAITING APPROVAL.");
        await _context.SaveChangesAsync();

        // Notify checkers
        await _notificationService.NotifyRoleAsync("Finance Officer", "Cash Disbursement Pending Approval",
            $"${request.Amount:N2} to {request.Recipient} for '{request.Purpose}' needs your approval.", NotificationType.PendingApproval);
        await _notificationService.NotifyRoleAsync("Manager", "Cash Disbursement Pending Approval",
            $"${request.Amount:N2} to {request.Recipient} for '{request.Purpose}' needs your approval.", NotificationType.PendingApproval);

        return Ok(new { message = "Disbursement recorded and sent for approval.", transactionId = tx.Id, reference, status = "PendingApproval" });
    }

    // ─── GET /api/cash/pending ─────────────────────────────────────────────── CHECKER
    /// <summary>Pending cash additions AND disbursements awaiting checker approval.</summary>
    [HttpGet("pending")]
    public async Task<IActionResult> GetPendingDisbursements()
    {
        var pending = await _context.CashTransactions
            .Include(t => t.CreatedByUser)
            .Where(t => (t.Type == TransactionType.Disbursement || t.Type == TransactionType.Addition)
                        && t.ApprovalStatus == CashApprovalStatus.Pending)
            .OrderBy(t => t.Date)
            .Select(t => new
            {
                t.Id, t.Date, t.Amount, t.SourceOrPurpose, t.Reference,
                type = t.Type.ToString(),
                requestedBy = t.CreatedByUser.FullName,
                t.ApprovalStatus
            })
            .ToListAsync();
        return Ok(pending);
    }

    // ─── POST /api/cash/approve/{id} ──────────────────────────────────────── CHECKER
    [HttpPost("approve/{id:int}")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> ApproveDisbursement(int id)
    {
        var tx = await _context.CashTransactions
            .Include(t => t.CreatedByUser)
            .FirstOrDefaultAsync(t => t.Id == id);

        if (tx == null) return NotFound(new { message = "Transaction not found." });
        if (tx.Type != TransactionType.Disbursement && tx.Type != TransactionType.Addition)
            return BadRequest(new { message = "Only additions and disbursements require approval." });
        if (tx.ApprovalStatus != CashApprovalStatus.Pending)
            return BadRequest(new { message = $"Transaction is not pending. Status: {tx.ApprovalStatus}." });
        if (tx.CreatedByUserId == GetCurrentUserId())
            return BadRequest(new { message = "You cannot approve your own request." });

        var userId = GetCurrentUserId();
        var action = tx.Type == TransactionType.Addition ? "addition" : "disbursement";

        // Disbursements risk overdraft if two are approved at the same instant — serialize
        // the balance check + write behind an advisory lock so the second approver always
        // re-checks against the first approver's already-committed balance.
        if (tx.Type == TransactionType.Disbursement)
        {
            var approved = await AdvisoryLock.WithLockAsync(_context, AdvisoryLock.MainCashBook, async () =>
            {
                var currentBalance = await ComputeBalanceAsync();
                if (tx.Amount > currentBalance) return false;

                tx.ApprovalStatus  = CashApprovalStatus.Approved;
                tx.ApprovedByUserId = userId;
                tx.ApprovedAt       = DateTime.UtcNow;
                tx.UpdatedAt        = DateTime.UtcNow;
                await _context.SaveChangesAsync();
                return true;
            });
            if (!approved)
            {
                var currentBalance = await ComputeBalanceAsync();
                return BadRequest(new { message = $"Insufficient balance to approve. Current: ${currentBalance:N2}, Required: ${tx.Amount:N2}." });
            }
        }
        else
        {
            tx.ApprovalStatus  = CashApprovalStatus.Approved;
            tx.ApprovedByUserId = userId;
            tx.ApprovedAt       = DateTime.UtcNow;
            tx.UpdatedAt        = DateTime.UtcNow;
            await _context.SaveChangesAsync();
        }

        await LogAuditAsync(userId, $"Cash {action} APPROVED: ${tx.Amount:N2} — '{tx.SourceOrPurpose}'. Ref: {tx.Reference}");

        var newBalance = await ComputeBalanceAsync();

        if (newBalance < LowCashThreshold)
            await _notificationService.NotifyRoleAsync("Manager", "Low Cash Balance Alert",
                $"Balance dropped to ${newBalance:N2} after approving a {action}.", NotificationType.LowCashBalance);

        await _notificationService.NotifyUserAsync(tx.CreatedByUserId, $"Cash {char.ToUpper(action[0])}{action[1..]} Approved",
            $"Your {action} of ${tx.Amount:N2} ({tx.Reference}) has been approved.", NotificationType.PendingApproval);

        return Ok(new { message = $"Cash {action} approved.", newBalance });
    }

    // ─── POST /api/cash/reject/{id} ───────────────────────────────────────── CHECKER
    [HttpPost("reject/{id:int}")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> RejectDisbursement(int id, [FromBody] string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return BadRequest(new { message = "A reason for rejection is required." });

        var tx = await _context.CashTransactions
            .FirstOrDefaultAsync(t => t.Id == id);

        if (tx == null) return NotFound(new { message = "Transaction not found." });
        if (tx.ApprovalStatus != CashApprovalStatus.Pending)
            return BadRequest(new { message = "Transaction is not pending." });

        var userId = GetCurrentUserId();
        tx.ApprovalStatus  = CashApprovalStatus.Rejected;
        tx.RejectionReason = reason;
        tx.ApprovedByUserId = userId;
        tx.UpdatedAt        = DateTime.UtcNow;

        var action = tx.Type == TransactionType.Addition ? "addition" : "disbursement";
        await LogAuditAsync(userId, $"Cash {action} REJECTED: ${tx.Amount:N2}. Reason: {reason}");
        await _context.SaveChangesAsync();

        // Notify originator
        await _notificationService.NotifyUserAsync(tx.CreatedByUserId, $"Cash {char.ToUpper(action[0])}{action[1..]} Rejected",
            $"Your {action} of ${tx.Amount:N2} was rejected. Reason: {reason}", NotificationType.PendingApproval);

        return Ok(new { message = $"Cash {action} rejected.", reason });
    }

    // ─── GET /api/cash/transactions ───────────────────────────────────────────
    [HttpGet("transactions")]
    public async Task<IActionResult> GetLedger(
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromQuery] string? type,
        [FromQuery] string? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        if (pageSize > 200) pageSize = 200;

        var query = _context.CashTransactions
            .Include(t => t.CreatedByUser)
            .Include(t => t.ApprovedByUser)
            .AsQueryable();

        if (DateTime.TryParse(from, out var f))
            query = query.Where(t => t.Date >= f);
        if (DateTime.TryParse(to, out var t2))
            query = query.Where(t => t.Date < t2.AddDays(1));
        if (!string.IsNullOrEmpty(type) && Enum.TryParse<TransactionType>(type, out var typeEnum))
            query = query.Where(t => t.Type == typeEnum);
        if (!string.IsNullOrEmpty(status) && Enum.TryParse<CashApprovalStatus>(status, out var statusEnum))
            query = query.Where(t => t.ApprovalStatus == statusEnum);

        var total = await query.CountAsync();

        var txns = await query
            .OrderByDescending(t => t.Date)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(t => new
            {
                t.Id, t.Date, t.Amount,
                type       = t.Type.ToString(),
                status     = t.ApprovalStatus.ToString(),
                t.SourceOrPurpose, t.Reference,
                t.RejectionReason,
                t.IsReversed,
                isReversal = t.ReversalOfTransactionId != null,
                reversalStatus = t.ReversalStatus != null ? t.ReversalStatus.ToString() : null,
                createdBy  = t.CreatedByUser.FullName,
                approvedBy = t.ApprovedByUser != null ? t.ApprovedByUser.FullName : null,
                t.ApprovedAt
            })
            .ToListAsync();

        return Ok(new { total, page, pageSize, transactions = txns });
    }

    // ─── POST /api/cash/reverse/{id} (MAKER: request a reversal) ──────────────
    /// <summary>
    /// Requests reversal of a mistaken cash entry (e.g. 1000 captured instead of 100).
    /// This only flags the entry as Pending reversal — the balance is NOT touched
    /// yet. A Manager/Admin must approve before the actual contra entry is posted.
    /// Requestable by Cashier or Admin.
    /// </summary>
    [HttpPost("reverse/{id:int}")]
    [Authorize(Roles = "Admin,Cashier")]
    public async Task<IActionResult> RequestReversal(int id, [FromBody] string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return BadRequest(new { message = "A reason for the reversal is required." });

        var original = await _context.CashTransactions.FirstOrDefaultAsync(t => t.Id == id);
        if (original == null) return NotFound(new { message = "Transaction not found." });

        if (original.ReversalOfTransactionId != null)
            return BadRequest(new { message = "You cannot reverse a reversal entry." });
        if (original.IsReversed)
            return BadRequest(new { message = "This transaction has already been reversed." });
        if (original.ReversalStatus == CashApprovalStatus.Pending)
            return BadRequest(new { message = "A reversal request is already pending for this transaction." });

        var isCounted = original.ApprovalStatus == CashApprovalStatus.AutoApproved
                     || original.ApprovalStatus == CashApprovalStatus.Approved;
        if (!isCounted)
            return BadRequest(new { message = $"Only posted transactions can be reversed (this one is {original.ApprovalStatus}). Reject pending disbursements instead." });

        var userId = GetCurrentUserId();
        original.ReversalStatus = CashApprovalStatus.Pending;
        original.ReversalReason = reason;
        original.ReversalRequestedByUserId = userId;
        original.ReversalRequestedAt = DateTime.UtcNow;
        original.UpdatedAt = DateTime.UtcNow;

        await LogAuditAsync(userId, $"Reversal requested for transaction {original.Reference} (${original.Amount:N2} {original.Type}). Reason: {reason}. AWAITING APPROVAL.");
        await _context.SaveChangesAsync();

        await _notificationService.NotifyRoleAsync("Manager", "Cash Reversal Pending Approval",
            $"Reversal of {original.Reference} (${original.Amount:N2}) needs your approval.", NotificationType.PendingApproval);

        return Ok(new { message = "Reversal requested and sent for approval.", status = "PendingApproval" });
    }

    // ─── GET /api/cash/pending-reversals (CHECKER) ────────────────────────────
    [HttpGet("pending-reversals")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> GetPendingReversals()
    {
        var pending = await _context.CashTransactions
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

    // ─── POST /api/cash/reverse/{id}/approve (CHECKER) ────────────────────────
    [HttpPost("reverse/{id:int}/approve")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> ApproveReversal(int id)
    {
        var original = await _context.CashTransactions.FirstOrDefaultAsync(t => t.Id == id);
        if (original == null) return NotFound(new { message = "Transaction not found." });
        if (original.ReversalStatus != CashApprovalStatus.Pending)
            return BadRequest(new { message = "No pending reversal request for this transaction." });
        if (original.ReversalRequestedByUserId == GetCurrentUserId())
            return BadRequest(new { message = "You cannot approve your own reversal request." });

        var userId = GetCurrentUserId();

        // Post the contra entry now — this is the moment the balance actually changes.
        var reversal = new CashTransaction
        {
            Amount          = -original.Amount,
            Type            = original.Type,
            SourceOrPurpose = $"Reversal of {original.Reference}: {original.ReversalReason}",
            Reference       = $"REV-{original.Reference}",
            Date            = DateTime.UtcNow,
            ApprovalStatus  = CashApprovalStatus.Approved,
            ReversalOfTransactionId = original.Id,
            ApprovedByUserId = userId, ApprovedAt = DateTime.UtcNow,
            CreatedByUserId  = userId, CreatedAt = DateTime.UtcNow
        };
        _context.CashTransactions.Add(reversal);

        original.IsReversed = true;
        original.ReversalStatus = CashApprovalStatus.Approved;
        original.ReversalApprovedByUserId = userId;
        original.ReversalApprovedAt = DateTime.UtcNow;
        original.UpdatedAt = DateTime.UtcNow;

        await LogAuditAsync(userId, $"Reversal APPROVED for transaction {original.Reference} (${original.Amount:N2} {original.Type}).");
        await _context.SaveChangesAsync();

        if (original.ReversalRequestedByUserId.HasValue)
            await _notificationService.NotifyUserAsync(original.ReversalRequestedByUserId.Value, "Reversal Approved",
                $"Your reversal request for {original.Reference} was approved.", NotificationType.PendingApproval);

        return Ok(new
        {
            message       = $"Transaction {original.Reference} reversed. You can now post the correct amount.",
            reversalReference = reversal.Reference,
            newBalance    = await ComputeBalanceAsync()
        });
    }

    // ─── POST /api/cash/reverse/{id}/reject (CHECKER) ─────────────────────────
    [HttpPost("reverse/{id:int}/reject")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> RejectReversal(int id, [FromBody] string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return BadRequest(new { message = "A reason for rejection is required." });

        var original = await _context.CashTransactions.FirstOrDefaultAsync(t => t.Id == id);
        if (original == null) return NotFound(new { message = "Transaction not found." });
        if (original.ReversalStatus != CashApprovalStatus.Pending)
            return BadRequest(new { message = "No pending reversal request for this transaction." });

        var userId = GetCurrentUserId();
        original.ReversalStatus = CashApprovalStatus.Rejected;
        original.ReversalRejectionReason = reason;
        original.ReversalApprovedByUserId = userId;
        original.ReversalApprovedAt = DateTime.UtcNow;
        original.UpdatedAt = DateTime.UtcNow;

        await LogAuditAsync(userId, $"Reversal REJECTED for transaction {original.Reference}. Reason: {reason}");
        await _context.SaveChangesAsync();

        if (original.ReversalRequestedByUserId.HasValue)
            await _notificationService.NotifyUserAsync(original.ReversalRequestedByUserId.Value, "Reversal Rejected",
                $"Your reversal request for {original.Reference} was rejected. Reason: {reason}", NotificationType.PendingApproval);

        return Ok(new { message = "Reversal request rejected.", reason });
    }

    // ─── GET /api/cash/opening-balance/today ──────────────────────────────────
    /// <summary>Today's opening balance = closing balance of all prior days (auto carry-forward).</summary>
    [HttpGet("opening-balance/today")]
    public async Task<IActionResult> GetTodayOpeningBalance()
    {
        var todayUtc = DateTime.SpecifyKind(DateTime.UtcNow.Date, DateTimeKind.Utc);
        return Ok(new { openingBalance = await ComputeBalanceBeforeAsync(todayUtc) });
    }

    // ─── POST /api/cash/reconcile ─────────────────────────────────────────────
    [HttpPost("reconcile")]
    [Authorize(Roles = "Admin,Cashier")]
    public async Task<IActionResult> Reconcile([FromBody] ReconcileCashDto request)
    {
        var userId            = GetCurrentUserId();
        var todayUtc          = DateTime.SpecifyKind(DateTime.UtcNow.Date, DateTimeKind.Utc);
        var openingBalance    = await ComputeBalanceBeforeAsync(todayUtc);   // auto carry-forward
        var calculatedBalance = await ComputeBalanceAsync();
        var variance          = request.ActualEndBalance - calculatedBalance;

        _context.CashReconciliations.Add(new CashReconciliation
        {
            Date = DateTime.UtcNow.Date, OpeningBalance = openingBalance,
            CalculatedEndBalance = calculatedBalance, ActualEndBalance = request.ActualEndBalance,
            Variance = variance, Comment = request.Comment,
            ReconciliationUserId = userId, CreatedAt = DateTime.UtcNow
        });

        await LogAuditAsync(userId, $"Reconciliation: Opening ${openingBalance:N2}, Calculated ${calculatedBalance:N2}, Actual ${request.ActualEndBalance:N2}, Variance ${variance:N2}.");
        await _context.SaveChangesAsync();

        if (variance != 0)
            await _notificationService.NotifyRoleAsync("Manager", "Reconciliation Variance",
                $"Variance of ${variance:N2} on {DateTime.UtcNow:yyyy-MM-dd}.", NotificationType.ReconciliationVariance);

        return Ok(new { message = "Reconciliation saved.", openingBalance, calculatedBalance, actualBalance = request.ActualEndBalance, variance, status = variance == 0 ? "Balanced" : "Variance Flagged" });
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────
    /// <summary>True for AutoApproved (legacy/opening balance) or checker-Approved entries — the only ones that count towards the balance.</summary>
    private static bool IsPosted(CashApprovalStatus s) => s == CashApprovalStatus.AutoApproved || s == CashApprovalStatus.Approved;

    internal async Task<decimal> ComputeBalanceAsync()
    {
        // Only count APPROVED additions and opening balances (Cash Add now requires checker approval too)
        var credits = await _context.CashTransactions
            .Where(t => (t.Type == TransactionType.OpeningBalance || t.Type == TransactionType.Addition) &&
                       (t.ApprovalStatus == CashApprovalStatus.AutoApproved ||
                        t.ApprovalStatus == CashApprovalStatus.Approved))
            .SumAsync(t => (decimal?)t.Amount) ?? 0m;

        // Only count APPROVED disbursements (AutoApproved for legacy, Approved for new)
        var debits = await _context.CashTransactions
            .Where(t => t.Type == TransactionType.Disbursement &&
                       (t.ApprovalStatus == CashApprovalStatus.AutoApproved ||
                        t.ApprovalStatus == CashApprovalStatus.Approved))
            .SumAsync(t => (decimal?)t.Amount) ?? 0m;

        return credits - debits;
    }

    /// <summary>Running balance of all posted transactions strictly before the given UTC instant.</summary>
    private async Task<decimal> ComputeBalanceBeforeAsync(DateTime beforeUtc)
    {
        var credits = await _context.CashTransactions
            .Where(t => (t.Type == TransactionType.OpeningBalance || t.Type == TransactionType.Addition)
                        && t.Date < beforeUtc &&
                       (t.ApprovalStatus == CashApprovalStatus.AutoApproved ||
                        t.ApprovalStatus == CashApprovalStatus.Approved))
            .SumAsync(t => (decimal?)t.Amount) ?? 0m;

        var debits = await _context.CashTransactions
            .Where(t => t.Type == TransactionType.Disbursement && t.Date < beforeUtc &&
                       (t.ApprovalStatus == CashApprovalStatus.AutoApproved ||
                        t.ApprovalStatus == CashApprovalStatus.Approved))
            .SumAsync(t => (decimal?)t.Amount) ?? 0m;

        return credits - debits;
    }

    /// <summary>
    /// Generates a sequential, human-readable reference per transaction type per day,
    /// e.g. ADD-20260608-0001, DISB-20260608-0003.
    /// </summary>
    private async Task<string> NextReferenceAsync(string prefix, TransactionType type)
    {
        var todayUtc    = DateTime.SpecifyKind(DateTime.UtcNow.Date, DateTimeKind.Utc);
        var tomorrowUtc = todayUtc.AddDays(1);
        var countToday  = await _context.CashTransactions
            .CountAsync(t => t.Type == type && t.Date >= todayUtc && t.Date < tomorrowUtc);
        return $"{prefix}-{todayUtc:yyyyMMdd}-{(countToday + 1):D4}";
    }

    private async Task LogAuditAsync(int userId, string action)
    {
        _context.AuditLogs.Add(new AuditLog
        {
            Action = action, IpAddress = GetClientIp(), DeviceInfo = GetDeviceInfo(),
            Timestamp = DateTime.UtcNow, UserId = userId, CreatedAt = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();
    }
}
