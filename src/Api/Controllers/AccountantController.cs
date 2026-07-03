using ClosedXML.Excel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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
[Authorize(Roles = "Admin,Manager,Accountant")]
public class AccountantController : BaseApiController
{
    private readonly CashLoanDbContext _context;

    public AccountantController(CashLoanDbContext context)
    {
        _context = context;
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
            Date = DateTime.UtcNow, CreatedByUserId = userId, CreatedAt = DateTime.UtcNow
        });
        await LogAuditAsync($"Accountant book initial balance set: ${request.Amount:N2}");
        await _context.SaveChangesAsync();
        return Ok(new { message = "Accountant book opening balance recorded.", openingBalance = request.Amount });
    }

    // ─── POST /api/accountant/add ─────────────────────────────────────────────
    [HttpPost("add")]
    public async Task<IActionResult> AddCash([FromBody] AccountantMovementDto request)
    {
        if (request.Amount <= 0) return BadRequest(new { message = "Amount must be greater than zero." });

        var userId    = GetCurrentUserId();
        var reference = await NextReferenceAsync("ACC-ADD", TransactionType.Addition);
        _context.AccountantTransactions.Add(new AccountantTransaction
        {
            Amount = request.Amount, Type = TransactionType.Addition,
            SourceOrPurpose = request.SourceOrPurpose, Reference = reference,
            Date = DateTime.UtcNow, CreatedByUserId = userId, CreatedAt = DateTime.UtcNow
        });
        await LogAuditAsync($"Accountant book: cash in ${request.Amount:N2} — {request.SourceOrPurpose}. Ref: {reference}");
        await _context.SaveChangesAsync();
        return Ok(new { message = "Cash added to accountant book.", reference, newBalance = await ComputeBalanceAsync() });
    }

    // ─── POST /api/accountant/disburse ────────────────────────────────────────
    [HttpPost("disburse")]
    public async Task<IActionResult> Disburse([FromBody] AccountantMovementDto request)
    {
        if (request.Amount <= 0) return BadRequest(new { message = "Amount must be greater than zero." });

        var balance = await ComputeBalanceAsync();
        if (request.Amount > balance)
            return BadRequest(new { message = $"Insufficient accountant book balance. Available: ${balance:N2}." });

        var userId    = GetCurrentUserId();
        var reference = await NextReferenceAsync("ACC-DISB", TransactionType.Disbursement);
        _context.AccountantTransactions.Add(new AccountantTransaction
        {
            Amount = request.Amount, Type = TransactionType.Disbursement,
            SourceOrPurpose = request.SourceOrPurpose, Reference = reference,
            Date = DateTime.UtcNow, CreatedByUserId = userId, CreatedAt = DateTime.UtcNow
        });
        await LogAuditAsync($"Accountant book: cash out ${request.Amount:N2} — {request.SourceOrPurpose}. Ref: {reference}");
        await _context.SaveChangesAsync();
        return Ok(new { message = "Disbursement recorded in accountant book.", reference, newBalance = await ComputeBalanceAsync() });
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
                t.SourceOrPurpose, t.Reference, createdBy = t.CreatedByUser.FullName
            })
            .ToListAsync();
        return Ok(new { total, page, pageSize, transactions = txns });
    }

    // ─── GET /api/accountant/daily-report ─────────────────────────────────────
    [HttpGet("daily-report")]
    public async Task<IActionResult> GetDailyReport([FromQuery] string? date)
    {
        var (dayStart, dayEnd) = DayRange(date);
        var txns = await DayTransactionsAsync(dayStart, dayEnd);
        var opening = await BalanceBeforeAsync(dayStart);
        var added     = txns.Where(t => t.Type == TransactionType.Addition || t.Type == TransactionType.OpeningBalance).Sum(t => t.Amount);
        var disbursed = txns.Where(t => t.Type == TransactionType.Disbursement).Sum(t => t.Amount);

        return Ok(new
        {
            date = dayStart.ToString("yyyy-MM-dd"),
            openingBalance = opening, totalAdded = added, totalDisbursed = disbursed,
            closingBalance = opening + added - disbursed,
            transactions = txns.Select(t => new
            {
                t.Id, t.Date, t.Amount, type = t.Type.ToString(),
                t.SourceOrPurpose, t.Reference, createdBy = t.CreatedByUser.FullName
            })
        });
    }

    // ─── GET /api/accountant/daily-report/export (Excel, DR/CR format) ────────
    [HttpGet("daily-report/export")]
    public async Task<IActionResult> ExportDailyReport([FromQuery] string? date)
    {
        var (dayStart, dayEnd) = DayRange(date);
        var txns = await DayTransactionsAsync(dayStart, dayEnd);
        var opening   = await BalanceBeforeAsync(dayStart);
        var added     = txns.Where(t => t.Type == TransactionType.Addition || t.Type == TransactionType.OpeningBalance).Sum(t => t.Amount);
        var disbursed = txns.Where(t => t.Type == TransactionType.Disbursement).Sum(t => t.Amount);

        var rows = txns.Select(t => (
            Time: t.Date, Type: t.Type, Amount: t.Amount,
            Source: t.SourceOrPurpose, Reference: t.Reference,
            By: t.CreatedByUser.FullName)).ToList();

        var ms = DrCrExcel.Build(
            title: $"Accountant Daily Cash Report — {dayStart:dd MMM yyyy}",
            opening: opening, added: added, disbursed: disbursed,
            closing: opening + added - disbursed, rows: rows);

        return File(ms, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"CALM_Accountant_Daily_{dayStart:yyyyMMdd}.xlsx");
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────
    private static (DateTime start, DateTime end) DayRange(string? date)
    {
        var raw = DateTime.TryParse(date, out var p) ? p : DateTime.UtcNow;
        var s = DateTime.SpecifyKind(raw.Date, DateTimeKind.Utc);
        return (s, s.AddDays(1));
    }

    private Task<List<AccountantTransaction>> DayTransactionsAsync(DateTime s, DateTime e) =>
        _context.AccountantTransactions.Include(t => t.CreatedByUser)
            .Where(t => t.Date >= s && t.Date < e).OrderBy(t => t.Date).ToListAsync();

    private async Task<decimal> ComputeBalanceAsync()
    {
        var credits = await _context.AccountantTransactions
            .Where(t => t.Type == TransactionType.OpeningBalance || t.Type == TransactionType.Addition)
            .SumAsync(t => (decimal?)t.Amount) ?? 0m;
        var debits = await _context.AccountantTransactions
            .Where(t => t.Type == TransactionType.Disbursement)
            .SumAsync(t => (decimal?)t.Amount) ?? 0m;
        return credits - debits;
    }

    private async Task<decimal> BalanceBeforeAsync(DateTime beforeUtc)
    {
        var credits = await _context.AccountantTransactions
            .Where(t => (t.Type == TransactionType.OpeningBalance || t.Type == TransactionType.Addition) && t.Date < beforeUtc)
            .SumAsync(t => (decimal?)t.Amount) ?? 0m;
        var debits = await _context.AccountantTransactions
            .Where(t => t.Type == TransactionType.Disbursement && t.Date < beforeUtc)
            .SumAsync(t => (decimal?)t.Amount) ?? 0m;
        return credits - debits;
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
