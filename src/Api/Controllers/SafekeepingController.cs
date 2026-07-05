using ClosedXML.Excel;
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
    [Authorize(Roles = "Admin,Cashier")]
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
    [Authorize(Roles = "Admin,Cashier")]
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
    [Authorize(Roles = "Admin,Cashier")]
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

    // ─── GET /api/safekeeping/report/export (all accounts, xlsx/pdf) ──────────
    [HttpGet("report/export")]
    public async Task<IActionResult> ExportSummaryReport([FromQuery] string format = "xlsx")
    {
        var accounts = await _context.SafekeepingAccounts
            .Include(a => a.Transactions)
            .OrderBy(a => a.DepositorName)
            .ToListAsync();

        var data = accounts.Select(a =>
        {
            var deposits = a.Transactions.Where(t => t.Type == SafekeepingTransactionType.Deposit).ToList();
            var collected = a.Transactions.Where(t => t.Type == SafekeepingTransactionType.Withdrawal &&
                (t.ApprovalStatus == CashApprovalStatus.AutoApproved || t.ApprovalStatus == CashApprovalStatus.Approved)).ToList();
            return new
            {
                a.DepositorName, a.Phone,
                NationalId = a.NationalId ?? "—",
                FirstLeft  = deposits.Count > 0 ? deposits.Min(t => t.Date).ToString("dd MMM yyyy") : "—",
                LastCollection = collected.Count > 0 ? collected.Max(t => t.Date).ToString("dd MMM yyyy") : "—",
                Deposited  = deposits.Sum(t => t.Amount),
                Collected  = collected.Sum(t => t.Amount),
                Balance    = deposits.Sum(t => t.Amount) - collected.Sum(t => t.Amount)
            };
        }).ToList();

        var totalHeld = data.Sum(d => d.Balance);
        var title     = $"Safekeeping Summary — {DateTime.UtcNow:dd MMM yyyy}";
        var fileName  = $"CALM_Safekeeping_Summary_{DateTime.UtcNow:yyyyMMdd}";

        if (format.ToLower() == "pdf")
        {
            var rows = data.Select(d => new[]
            {
                d.DepositorName, d.Phone, d.NationalId, d.FirstLeft, d.LastCollection,
                $"!g${d.Deposited:N2}", $"!r${d.Collected:N2}", $"${d.Balance:N2}"
            }).ToList();
            var ms = ReportPdf.Build(title,
                new List<(string, string)> { ("Accounts", data.Count.ToString()), ("Total Held", $"${totalHeld:N2}") },
                new[] { "Depositor", "Phone", "National ID", "First Left", "Last Collection", "Deposited", "Collected", "Balance" },
                rows, rightAlign: new[] { 5, 6, 7 });
            return File(ms, "application/pdf", $"{fileName}.pdf");
        }

        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Safekeeping");
        ws.Cell("A1").Value = "CALM – Cash & Liquidity Management";
        ws.Cell("A1").Style.Font.Bold = true; ws.Cell("A1").Style.Font.FontSize = 14;
        ws.Cell("A2").Value = "Welble Investments P/L"; ws.Cell("A2").Style.Font.FontColor = XLColor.Gray;
        ws.Cell("A3").Value = title; ws.Cell("A3").Style.Font.Bold = true;
        ws.Cell("A5").Value = "Total Held"; ws.Cell("A5").Style.Font.Bold = true;
        ws.Cell("B5").Value = totalHeld; ws.Cell("B5").Style.NumberFormat.Format = "$#,##0.00"; ws.Cell("B5").Style.Font.Bold = true;

        var headers = new[] { "Depositor", "Phone", "National ID", "First Left", "Last Collection", "Deposited (USD)", "Collected (USD)", "Balance (USD)" };
        int r = 7;
        for (int c = 0; c < headers.Length; c++)
        {
            ws.Cell(r, c + 1).Value = headers[c];
            ws.Cell(r, c + 1).Style.Font.Bold = true;
            ws.Cell(r, c + 1).Style.Fill.BackgroundColor = XLColor.FromHtml("#1e293b");
            ws.Cell(r, c + 1).Style.Font.FontColor = XLColor.White;
        }
        r++;
        foreach (var d in data)
        {
            ws.Cell(r, 1).Value = d.DepositorName; ws.Cell(r, 2).Value = d.Phone; ws.Cell(r, 3).Value = d.NationalId;
            ws.Cell(r, 4).Value = d.FirstLeft; ws.Cell(r, 5).Value = d.LastCollection;
            ws.Cell(r, 6).Value = d.Deposited; ws.Cell(r, 6).Style.NumberFormat.Format = "$#,##0.00"; ws.Cell(r, 6).Style.Font.FontColor = XLColor.DarkGreen;
            ws.Cell(r, 7).Value = d.Collected; ws.Cell(r, 7).Style.NumberFormat.Format = "$#,##0.00"; ws.Cell(r, 7).Style.Font.FontColor = XLColor.Red;
            ws.Cell(r, 8).Value = d.Balance;   ws.Cell(r, 8).Style.NumberFormat.Format = "$#,##0.00"; ws.Cell(r, 8).Style.Font.Bold = true;
            r++;
        }
        ws.Columns().AdjustToContents();
        var xms = new MemoryStream(); wb.SaveAs(xms); xms.Position = 0;
        return File(xms, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"{fileName}.xlsx");
    }

    // ─── GET /api/safekeeping/report/period/export (weekly|monthly, xlsx/pdf) ─
    [HttpGet("report/period/export")]
    public async Task<IActionResult> ExportPeriodReport(
        [FromQuery] string period = "weekly",
        [FromQuery] string? date = null,
        [FromQuery] string? from = null,
        [FromQuery] string? to = null,
        [FromQuery] string format = "xlsx")
    {
        var (start, end, label, slug) = PeriodUtil.Range(period, date, from, to);

        var txns = await _context.SafekeepingTransactions
            .Include(t => t.Account).Include(t => t.CreatedByUser)
            .Where(t => t.Date >= start && t.Date < end)
            .OrderBy(t => t.Date)
            .ToListAsync();

        var deposits = txns.Where(t => t.Type == SafekeepingTransactionType.Deposit).Sum(t => t.Amount);
        var collected = txns.Where(t => t.Type == SafekeepingTransactionType.Withdrawal &&
            (t.ApprovalStatus == CashApprovalStatus.AutoApproved || t.ApprovalStatus == CashApprovalStatus.Approved)).Sum(t => t.Amount);

        // Total currently held across all accounts
        var allDeposits = await _context.SafekeepingTransactions
            .Where(t => t.Type == SafekeepingTransactionType.Deposit && t.Date < end)
            .SumAsync(t => (decimal?)t.Amount) ?? 0m;
        var allCollected = await _context.SafekeepingTransactions
            .Where(t => t.Type == SafekeepingTransactionType.Withdrawal && t.Date < end &&
                       (t.ApprovalStatus == CashApprovalStatus.AutoApproved || t.ApprovalStatus == CashApprovalStatus.Approved))
            .SumAsync(t => (decimal?)t.Amount) ?? 0m;

        var periodName = from != null && to != null ? "Range"
            : string.Equals(period, "monthly", StringComparison.OrdinalIgnoreCase) ? "Monthly" : "Weekly";
        var title    = $"Safekeeping Report — {label}";
        var fileName = $"CALM_Safekeeping_{periodName}_{slug}";

        var summary = new List<(string, string)>
        {
            ("Deposits (left)",       $"${deposits:N2}"),
            ("Collections (approved)",$"${collected:N2}"),
            ("Net Movement",          $"${deposits - collected:N2}"),
            ("Total Held (period end)", $"${allDeposits - allCollected:N2}")
        };
        var headers = new[] { "Date", "Depositor", "Type", "Status", "Amount", "Reference", "By" };

        string StatusOf(SafekeepingTransaction t) =>
            t.Type == SafekeepingTransactionType.Deposit ? "—" :
            t.ApprovalStatus == CashApprovalStatus.Pending ? "Pending" :
            t.ApprovalStatus == CashApprovalStatus.Rejected ? "Rejected" : "Approved";

        if (format.ToLower() == "pdf")
        {
            var rows = txns.Select(t => new[]
            {
                t.Date.ToString("dd MMM HH:mm"),
                t.Account.DepositorName,
                t.Type == SafekeepingTransactionType.Deposit ? "Left" : "Collected",
                StatusOf(t),
                t.Type == SafekeepingTransactionType.Deposit ? $"!g+${t.Amount:N2}" : $"!r-${t.Amount:N2}",
                t.Reference, t.CreatedByUser.FullName
            }).ToList();
            var pms = ReportPdf.Build(title, summary, headers, rows, rightAlign: new[] { 4 });
            return File(pms, "application/pdf", $"{fileName}.pdf");
        }

        var xrows = txns.Select(t => new object?[]
        {
            t.Date.ToLocalTime().ToString("dd MMM yyyy HH:mm"),
            t.Account.DepositorName,
            t.Type == SafekeepingTransactionType.Deposit ? "Left" : "Collected",
            StatusOf(t),
            t.Type == SafekeepingTransactionType.Deposit ? t.Amount : -t.Amount,
            t.Reference, t.CreatedByUser.FullName
        }).ToList();
        var ms = ExcelTable.Build(title, summary, headers, xrows);
        return File(ms, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"{fileName}.xlsx");
    }

    // ─── GET /api/safekeeping/accounts/{id}/statement/export (xlsx/pdf) ───────
    [HttpGet("accounts/{id:int}/statement/export")]
    public async Task<IActionResult> ExportStatement(int id, [FromQuery] string format = "xlsx")
    {
        var account = await _context.SafekeepingAccounts
            .Include(a => a.Transactions).ThenInclude(t => t.CreatedByUser)
            .FirstOrDefaultAsync(a => a.Id == id);
        if (account == null) return NotFound(new { message = "Account not found." });

        var ordered = account.Transactions.OrderBy(t => t.Date).ToList();
        decimal running = 0;
        var lines = ordered.Select(t =>
        {
            var counts = t.Type == SafekeepingTransactionType.Deposit
                         || t.ApprovalStatus == CashApprovalStatus.AutoApproved
                         || t.ApprovalStatus == CashApprovalStatus.Approved;
            if (counts) running += t.Type == SafekeepingTransactionType.Deposit ? t.Amount : -t.Amount;
            return (Txn: t, Counts: counts, BalanceAfter: running);
        }).ToList();

        var deposits = ordered.Where(t => t.Type == SafekeepingTransactionType.Deposit).ToList();
        var title    = $"Safekeeping Statement — {account.DepositorName}";
        var fileName = $"CALM_Safekeeping_{account.DepositorName.Replace(' ', '_')}_{DateTime.UtcNow:yyyyMMdd}";
        var summary  = new List<(string, string)>
        {
            ("Depositor",       account.DepositorName),
            ("Phone",           account.Phone),
            ("Date First Left", deposits.Count > 0 ? deposits.Min(t => t.Date).ToString("dd MMM yyyy") : "—"),
            ("Total Deposited", $"${deposits.Sum(t => t.Amount):N2}"),
            ("Total Collected", $"${deposits.Sum(t => t.Amount) - running:N2}"),
            ("Remaining Balance", $"${running:N2}")
        };

        if (format.ToLower() == "pdf")
        {
            var rows = lines.Select(l => new[]
            {
                l.Txn.Date.ToString("dd MMM yyyy HH:mm"),
                l.Txn.Type == SafekeepingTransactionType.Deposit ? "Left" : "Collected",
                l.Txn.Type == SafekeepingTransactionType.Deposit ? "—" :
                    l.Txn.ApprovalStatus == CashApprovalStatus.Pending ? "Pending" :
                    l.Txn.ApprovalStatus == CashApprovalStatus.Rejected ? "Rejected" : "Approved",
                l.Txn.Type == SafekeepingTransactionType.Deposit ? $"!g+${l.Txn.Amount:N2}" : $"!r-${l.Txn.Amount:N2}",
                l.Counts ? $"${l.BalanceAfter:N2}" : "—",
                l.Txn.Reference,
                l.Txn.CreatedByUser.FullName
            }).ToList();
            var ms = ReportPdf.Build(title, summary,
                new[] { "Date", "Type", "Status", "Amount", "Balance After", "Reference", "By" },
                rows, rightAlign: new[] { 3, 4 });
            return File(ms, "application/pdf", $"{fileName}.pdf");
        }

        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Statement");
        ws.Cell("A1").Value = "CALM – Cash & Liquidity Management";
        ws.Cell("A1").Style.Font.Bold = true; ws.Cell("A1").Style.Font.FontSize = 14;
        ws.Cell("A2").Value = "Welble Investments P/L"; ws.Cell("A2").Style.Font.FontColor = XLColor.Gray;
        ws.Cell("A3").Value = title; ws.Cell("A3").Style.Font.Bold = true;

        int r = 5;
        foreach (var (label, value) in summary)
        {
            ws.Cell(r, 1).Value = label; ws.Cell(r, 2).Value = value;
            if (label == "Remaining Balance") { ws.Cell(r, 1).Style.Font.Bold = true; ws.Cell(r, 2).Style.Font.Bold = true; }
            r++;
        }

        r++;
        var headers = new[] { "Date", "Type", "Status", "DR Amount (USD)", "CR Amount (USD)", "Balance After", "Reference", "Recorded By" };
        for (int c = 0; c < headers.Length; c++)
        {
            ws.Cell(r, c + 1).Value = headers[c];
            ws.Cell(r, c + 1).Style.Font.Bold = true;
            ws.Cell(r, c + 1).Style.Fill.BackgroundColor = XLColor.FromHtml("#1e293b");
            ws.Cell(r, c + 1).Style.Font.FontColor = XLColor.White;
        }
        r++;
        foreach (var l in lines)
        {
            var isDeposit = l.Txn.Type == SafekeepingTransactionType.Deposit;
            ws.Cell(r, 1).Value = l.Txn.Date.ToLocalTime().ToString("dd MMM yyyy HH:mm");
            ws.Cell(r, 2).Value = isDeposit ? "Left" : "Collected";
            ws.Cell(r, 3).Value = isDeposit ? "—" :
                l.Txn.ApprovalStatus == CashApprovalStatus.Pending ? "Pending" :
                l.Txn.ApprovalStatus == CashApprovalStatus.Rejected ? "Rejected" : "Approved";
            if (!isDeposit)
            {
                ws.Cell(r, 4).Value = -l.Txn.Amount;
                ws.Cell(r, 4).Style.NumberFormat.Format = "$#,##0.00";
                ws.Cell(r, 4).Style.Font.FontColor = XLColor.Red;
            }
            else
            {
                ws.Cell(r, 5).Value = l.Txn.Amount;
                ws.Cell(r, 5).Style.NumberFormat.Format = "$#,##0.00";
                ws.Cell(r, 5).Style.Font.FontColor = XLColor.DarkGreen;
            }
            if (l.Counts) { ws.Cell(r, 6).Value = l.BalanceAfter; ws.Cell(r, 6).Style.NumberFormat.Format = "$#,##0.00"; }
            else ws.Cell(r, 6).Value = "—";
            ws.Cell(r, 7).Value = l.Txn.Reference;
            ws.Cell(r, 8).Value = l.Txn.CreatedByUser.FullName;
            r++;
        }
        ws.Columns().AdjustToContents();
        var xms2 = new MemoryStream(); wb.SaveAs(xms2); xms2.Position = 0;
        return File(xms2, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"{fileName}.xlsx");
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
