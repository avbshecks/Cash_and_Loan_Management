using ClosedXML.Excel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using CashLoanManagement.Domain.Enums;
using CashLoanManagement.Infrastructure.Persistence;

namespace CashLoanManagement.Api.Controllers;

// Typed record — avoids dynamic/extension-method dispatch issues in QuestPDF lambdas
internal record DaySummary(DateTime Date, decimal Additions, decimal Disbursements, int TxnCount, string Narration = "");

[Authorize]
[Route("api/report")]
[ApiController]
public class ReportExportController : ControllerBase
{
    private readonly CashLoanDbContext _context;

    public ReportExportController(CashLoanDbContext context)
    {
        _context = context;
    }

    private static bool IsPosted(CashApprovalStatus s) => s == CashApprovalStatus.AutoApproved || s == CashApprovalStatus.Approved;

    private static string BuildNarration(List<Domain.Entities.CashTransaction> dayTxns)
    {
        var parts = dayTxns
            .OrderBy(t => t.Date)
            .Select(t => $"{(t.Type == TransactionType.Disbursement ? "-" : "+")}${t.Amount:N0} {t.SourceOrPurpose}".Trim())
            .Where(s => !string.IsNullOrWhiteSpace(s));
        var narration = string.Join("; ", parts);
        return narration.Length > 160 ? narration[..157] + "..." : narration;
    }

    // ─── GET /api/report/daily-cash/export ───────────────────────────────────
    [HttpGet("daily-cash/export")]
    public async Task<IActionResult> ExportDailyCash(
        [FromQuery] string? date,
        [FromQuery] string format = "xlsx")
    {
        var rawDate  = DateTime.TryParse(date, out var p) ? p : DateTime.UtcNow;
        var dayStart = DateTime.SpecifyKind(rawDate.Date,            DateTimeKind.Utc);
        var dayEnd   = DateTime.SpecifyKind(rawDate.Date.AddDays(1), DateTimeKind.Utc);

        var transactions = await _context.CashTransactions
            .Include(t => t.CreatedByUser)
            .Where(t => t.Date >= dayStart && t.Date < dayEnd)
            .OrderBy(t => t.Date)
            .ToListAsync();

        var opening = transactions.Where(t => t.Type == TransactionType.OpeningBalance && IsPosted(t.ApprovalStatus)).Sum(t => t.Amount);
        var adds    = transactions.Where(t => t.Type == TransactionType.Addition && IsPosted(t.ApprovalStatus)).Sum(t => t.Amount);
        var disb    = transactions.Where(t => t.Type == TransactionType.Disbursement && IsPosted(t.ApprovalStatus)).Sum(t => t.Amount);
        var prevCredits = await _context.CashTransactions
            .Where(t => (t.Type == TransactionType.OpeningBalance || t.Type == TransactionType.Addition) && t.Date < dayStart && IsPosted(t.ApprovalStatus))
            .SumAsync(t => (decimal?)t.Amount) ?? 0m;
        var prevDisb = await _context.CashTransactions
            .Where(t => t.Type == TransactionType.Disbursement && t.Date < dayStart && IsPosted(t.ApprovalStatus))
            .SumAsync(t => (decimal?)t.Amount) ?? 0m;
        var openingBalance = opening > 0 ? opening : prevCredits - prevDisb;
        var closing = openingBalance + adds - disb;

        var reconciliation = await _context.CashReconciliations
            .Where(r => r.Date >= dayStart && r.Date < dayEnd)
            .OrderByDescending(r => r.Id)
            .FirstOrDefaultAsync();

        var title    = $"Daily Cash Report — {dayStart:dd MMM yyyy}";
        var fileName = $"CALM_DailyCash_{dayStart:yyyyMMdd}";

        if (format.ToLower() == "pdf")
            return PdfDailyCash(title, fileName, dayStart, transactions, openingBalance, adds, disb, closing, reconciliation);

        // Excel in DR/CR ledger format (shared builder)
        var rows = transactions
            // Exclude pending/rejected disbursements from the posted ledger
            .Where(t => t.Type != TransactionType.Disbursement ||
                        t.ApprovalStatus == CashApprovalStatus.AutoApproved ||
                        t.ApprovalStatus == CashApprovalStatus.Approved)
            .Select(t => (
                Time: t.Date, Type: t.Type, Amount: t.Amount,
                Source: t.SourceOrPurpose, Reference: t.Reference,
                By: t.CreatedByUser?.FullName ?? "")).ToList();

        var ms = DrCrExcel.Build(title, openingBalance, adds, disb, closing, rows);
        return File(ms, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"{fileName}.xlsx");
    }

    // ─── GET /api/report/monthly-cash/export ─────────────────────────────────
    [HttpGet("monthly-cash/export")]
    public async Task<IActionResult> ExportMonthlyCash(
        [FromQuery] int? year,
        [FromQuery] int? month,
        [FromQuery] string format = "xlsx")
    {
        var y = year  ?? DateTime.UtcNow.Year;
        var m = month ?? DateTime.UtcNow.Month;

        var firstDay = new DateTime(y, m, 1, 0, 0, 0, DateTimeKind.Utc);
        var lastDay  = firstDay.AddMonths(1);

        var txns = await _context.CashTransactions
            .Include(t => t.CreatedByUser)
            .Where(t => t.Date >= firstDay && t.Date < lastDay)
            .OrderBy(t => t.Date)
            .ToListAsync();

        var repayments = await _context.LoanRepayments
            .Where(r => r.RepaymentDate >= firstDay && r.RepaymentDate < lastDay && r.ApprovalStatus == CashApprovalStatus.Approved)
            .SumAsync(r => (decimal?)r.Amount) ?? 0m;

        var newLoans = await _context.Loans
            .CountAsync(l => l.CreatedAt >= firstDay && l.CreatedAt < lastDay);

        var days = Enumerable.Range(0, DateTime.DaysInMonth(y, m)).Select(i =>
        {
            var d    = firstDay.AddDays(i);
            var dStr = d.ToString("yyyy-MM-dd");
            var dt   = txns.Where(t => t.Date.ToString("yyyy-MM-dd") == dStr && IsPosted(t.ApprovalStatus)).ToList();
            return new DaySummary(
                Date:          d,
                Additions:     dt.Where(t => t.Type == TransactionType.Addition || t.Type == TransactionType.OpeningBalance).Sum(t => t.Amount),
                Disbursements: dt.Where(t => t.Type == TransactionType.Disbursement).Sum(t => t.Amount),
                TxnCount:      dt.Count,
                Narration:     BuildNarration(dt)
            );
        }).Where(d => d.TxnCount > 0).ToList();

        var totalAdds = txns.Where(t => IsPosted(t.ApprovalStatus) && (t.Type == TransactionType.Addition || t.Type == TransactionType.OpeningBalance)).Sum(t => t.Amount);
        var totalDisb = txns.Where(t => IsPosted(t.ApprovalStatus) && t.Type == TransactionType.Disbursement).Sum(t => t.Amount);

        var monthName = firstDay.ToString("MMMM yyyy");
        var title     = $"Monthly Cash Report — {monthName}";
        var fileName  = $"CALM_MonthlyCash_{y}{m:00}";

        if (format.ToLower() == "pdf")
            return PdfMonthlyCash(title, fileName, monthName, days, totalAdds, totalDisb, newLoans, repayments);

        return ExcelMonthlyCash(title, fileName, monthName, days, totalAdds, totalDisb, newLoans, repayments);
    }

    // ─── GET /api/report/weekly-cash/export ──────────────────────────────────
    [HttpGet("weekly-cash/export")]
    public async Task<IActionResult> ExportWeeklyCash(
        [FromQuery] string? date,
        [FromQuery] string format = "xlsx")
    {
        var raw       = DateTime.TryParse(date, out var p) ? p : DateTime.UtcNow;
        var offset    = ((int)raw.DayOfWeek + 6) % 7;
        var weekStart = DateTime.SpecifyKind(raw.Date.AddDays(-offset), DateTimeKind.Utc);
        var weekEnd   = weekStart.AddDays(7);

        var txns = await _context.CashTransactions
            .Where(t => t.Date >= weekStart && t.Date < weekEnd)
            .OrderBy(t => t.Date)
            .ToListAsync();

        var repayments = await _context.LoanRepayments
            .Where(r => r.RepaymentDate >= weekStart && r.RepaymentDate < weekEnd && r.ApprovalStatus == CashApprovalStatus.Approved)
            .SumAsync(r => (decimal?)r.Amount) ?? 0m;
        var newLoans = await _context.Loans
            .CountAsync(l => l.CreatedAt >= weekStart && l.CreatedAt < weekEnd);

        var days = Enumerable.Range(0, 7).Select(i =>
        {
            var d    = weekStart.AddDays(i);
            var dStr = d.ToString("yyyy-MM-dd");
            var dt   = txns.Where(t => t.Date.ToString("yyyy-MM-dd") == dStr && IsPosted(t.ApprovalStatus)).ToList();
            return new DaySummary(
                Date:          d,
                Additions:     dt.Where(t => t.Type == TransactionType.Addition || t.Type == TransactionType.OpeningBalance).Sum(t => t.Amount),
                Disbursements: dt.Where(t => t.Type == TransactionType.Disbursement).Sum(t => t.Amount),
                TxnCount:      dt.Count,
                Narration:     BuildNarration(dt)
            );
        }).Where(d => d.TxnCount > 0).ToList();

        var totalAdds = txns.Where(t => IsPosted(t.ApprovalStatus) && (t.Type == TransactionType.Addition || t.Type == TransactionType.OpeningBalance)).Sum(t => t.Amount);
        var totalDisb = txns.Where(t => IsPosted(t.ApprovalStatus) && t.Type == TransactionType.Disbursement).Sum(t => t.Amount);

        var label    = $"{weekStart:dd MMM} – {weekStart.AddDays(6):dd MMM yyyy}";
        var title    = $"Weekly Cash Report — {label}";
        var fileName = $"CALM_WeeklyCash_{weekStart:yyyyMMdd}";

        // Reuse the monthly builders — they render any day list with totals
        if (format.ToLower() == "pdf")
            return PdfMonthlyCash(title, fileName, label, days, totalAdds, totalDisb, newLoans, repayments);

        return ExcelMonthlyCash(title, fileName, label, days, totalAdds, totalDisb, newLoans, repayments);
    }

    // ─── GET /api/report/range-cash/export (custom range, xlsx/pdf) ───────────
    [HttpGet("range-cash/export")]
    public async Task<IActionResult> ExportRangeCash(
        [FromQuery] string from, [FromQuery] string to,
        [FromQuery] string format = "xlsx")
    {
        if (!DateTime.TryParse(from, out var f) || !DateTime.TryParse(to, out var t))
            return BadRequest(new { message = "Both 'from' and 'to' dates are required." });

        var start = DateTime.SpecifyKind(f.Date, DateTimeKind.Utc);
        var end   = DateTime.SpecifyKind(t.Date.AddDays(1), DateTimeKind.Utc);
        if (end <= start) return BadRequest(new { message = "'to' must be on or after 'from'." });
        var dayCount = (int)(end - start).TotalDays;
        if (dayCount > 92) return BadRequest(new { message = "Date range cannot exceed 92 days." });

        var txns = await _context.CashTransactions
            .Where(x => x.Date >= start && x.Date < end)
            .OrderBy(x => x.Date)
            .ToListAsync();

        var repayments = await _context.LoanRepayments
            .Where(r => r.RepaymentDate >= start && r.RepaymentDate < end && r.ApprovalStatus == CashApprovalStatus.Approved)
            .SumAsync(r => (decimal?)r.Amount) ?? 0m;
        var newLoans = await _context.Loans
            .CountAsync(l => l.CreatedAt >= start && l.CreatedAt < end);

        var days = Enumerable.Range(0, dayCount).Select(i =>
        {
            var d    = start.AddDays(i);
            var dStr = d.ToString("yyyy-MM-dd");
            var dt   = txns.Where(x => x.Date.ToString("yyyy-MM-dd") == dStr && IsPosted(x.ApprovalStatus)).ToList();
            return new DaySummary(
                Date:          d,
                Additions:     dt.Where(x => x.Type == TransactionType.Addition || x.Type == TransactionType.OpeningBalance).Sum(x => x.Amount),
                Disbursements: dt.Where(x => x.Type == TransactionType.Disbursement).Sum(x => x.Amount),
                TxnCount:      dt.Count,
                Narration:     BuildNarration(dt)
            );
        }).Where(d => d.TxnCount > 0).ToList();

        var totalAdds = txns.Where(x => IsPosted(x.ApprovalStatus) && (x.Type == TransactionType.Addition || x.Type == TransactionType.OpeningBalance)).Sum(x => x.Amount);
        var totalDisb = txns.Where(x => IsPosted(x.ApprovalStatus) && x.Type == TransactionType.Disbursement).Sum(x => x.Amount);

        var label    = $"{start:dd MMM yyyy} – {end.AddDays(-1):dd MMM yyyy}";
        var title    = $"Cash Report — {label}";
        var fileName = $"CALM_Cash_{start:yyyyMMdd}-{end.AddDays(-1):yyyyMMdd}";

        if (format.ToLower() == "pdf")
            return PdfMonthlyCash(title, fileName, label, days, totalAdds, totalDisb, newLoans, repayments);

        return ExcelMonthlyCash(title, fileName, label, days, totalAdds, totalDisb, newLoans, repayments);
    }

    // ─── GET /api/report/loans/period/export (weekly|monthly, xlsx/pdf) ───────
    [HttpGet("loans/period/export")]
    public async Task<IActionResult> ExportLoansPeriod(
        [FromQuery] string period = "weekly",
        [FromQuery] string? date = null,
        [FromQuery] string? from = null,
        [FromQuery] string? to = null,
        [FromQuery] string format = "xlsx")
    {
        var (start, end, label, slug) = PeriodUtil.Range(period, date, from, to);

        var loans = await _context.Loans
            .Include(l => l.Borrower).Include(l => l.Repayments)
            .Where(l => l.CreatedAt >= start && l.CreatedAt < end)
            .OrderBy(l => l.CreatedAt)
            .ToListAsync();

        var repayments = await _context.LoanRepayments
            .Where(r => r.RepaymentDate >= start && r.RepaymentDate < end && r.ApprovalStatus == CashApprovalStatus.Approved)
            .ToListAsync();

        var disbursed = await _context.Loans
            .Where(l => l.DisbursedAt != null && l.DisbursedAt >= start && l.DisbursedAt < end)
            .SumAsync(l => (decimal?)l.Amount) ?? 0m;

        var paidCount = await _context.Loans
            .CountAsync(l => l.Status == LoanStatus.Paid && l.UpdatedAt >= start && l.UpdatedAt < end);
        var overdueNow = await _context.Loans.CountAsync(l => l.Status == LoanStatus.Overdue);

        var periodName = from != null && to != null ? "Range"
            : string.Equals(period, "monthly", StringComparison.OrdinalIgnoreCase) ? "Monthly" : "Weekly";
        var title    = $"Loan Report — {label}";
        var fileName = $"CALM_Loans_{periodName}_{slug}";

        var summary = new List<(string, string)>
        {
            ("Loans Issued",         $"{loans.Count}  (${loans.Sum(l => l.Amount):N2})"),
            ("Cash Disbursed",       $"${disbursed:N2}"),
            ("Repayments Collected", $"{repayments.Count}  (${repayments.Sum(r => r.Amount):N2})"),
            ("Loans Fully Paid",     paidCount.ToString()),
            ("Currently Overdue",    overdueNow.ToString())
        };
        var headers = new[] { "Date", "Reference", "Borrower", "Type", "Amount", "Repaid", "Status", "Due Date" };

        if (format.ToLower() == "pdf")
        {
            var rows = loans.Select(l => new[]
            {
                l.CreatedAt.ToString("dd MMM"),
                l.ReferenceNumber, l.Borrower.FullName, l.Type.ToString(),
                $"${l.Amount:N2}", $"!g${l.Repayments.Where(r => r.ApprovalStatus == CashApprovalStatus.Approved).Sum(r => r.Amount):N2}",
                l.Status.ToString(), l.DueDate.ToString("dd MMM yyyy")
            }).ToList();
            var pms = ReportPdf.Build(title, summary, headers, rows, rightAlign: new[] { 4, 5 });
            return File(pms, "application/pdf", $"{fileName}.pdf");
        }

        var xrows = loans.Select(l => new object?[]
        {
            l.CreatedAt.ToString("dd MMM yyyy"),
            l.ReferenceNumber, l.Borrower.FullName, l.Type.ToString(),
            l.Amount, l.Repayments.Where(r => r.ApprovalStatus == CashApprovalStatus.Approved).Sum(r => r.Amount),
            l.Status.ToString(), l.DueDate.ToString("dd MMM yyyy")
        }).ToList();
        var ms = ExcelTable.Build(title, summary, headers, xrows);
        return File(ms, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"{fileName}.xlsx");
    }

    // ════════════════════════════════════════════════════════════════
    // EXCEL helpers
    // ════════════════════════════════════════════════════════════════

    private IActionResult ExcelDailyCash(
        string title, string fileName, DateTime date,
        List<Domain.Entities.CashTransaction> txns,
        decimal opening, decimal adds, decimal disb, decimal closing,
        Domain.Entities.CashReconciliation? rec)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Daily Cash");

        // ── Header ──────────────────────────────────────────────────
        ws.Cell("A1").Value = "CALM – Cash & Liquidity Management";
        ws.Cell("A1").Style.Font.Bold = true;
        ws.Cell("A1").Style.Font.FontSize = 14;
        ws.Cell("A2").Value = "Welble Investments P/L";
        ws.Cell("A2").Style.Font.FontColor = XLColor.Gray;
        ws.Cell("A3").Value = title;
        ws.Cell("A3").Style.Font.Bold = true;
        ws.Cell("A4").Value = $"Generated: {DateTime.Now:dd MMM yyyy HH:mm}";
        ws.Cell("A4").Style.Font.FontColor = XLColor.Gray;
        ws.Cell("A4").Style.Font.FontSize = 9;

        // ── Summary box ──────────────────────────────────────────────
        ws.Cell("A6").Value = "SUMMARY";
        ws.Cell("A6").Style.Font.Bold = true;
        ws.Cell("A6").Style.Fill.BackgroundColor = XLColor.FromHtml("#f59e0b");
        ws.Range("A6:B6").Merge();

        var summaryRows = new[] {
            ("Opening Balance", opening),
            ("Cash Added",      adds),
            ("Cash Disbursed",  disb),
            ("Closing Balance", closing)
        };
        int row = 7;
        foreach (var (label, val) in summaryRows)
        {
            ws.Cell(row, 1).Value = label;
            ws.Cell(row, 2).Value = val;
            ws.Cell(row, 2).Style.NumberFormat.Format = "$#,##0.00";
            if (label == "Closing Balance")
            {
                ws.Cell(row, 1).Style.Font.Bold = true;
                ws.Cell(row, 2).Style.Font.Bold = true;
            }
            row++;
        }

        if (rec != null)
        {
            ws.Cell(row, 1).Value = "Reconciliation Status";
            ws.Cell(row, 2).Value = rec.Variance == 0 ? "Balanced" : $"Variance: ${rec.Variance:N2}";
            ws.Cell(row, 2).Style.Font.FontColor = rec.Variance == 0 ? XLColor.Green : XLColor.Red;
            row++;
        }

        // ── Transactions table ───────────────────────────────────────
        row += 1;
        var headers = new[] { "Time", "Type", "Amount (USD)", "Source / Purpose", "Reference", "Recorded By" };
        for (int c = 0; c < headers.Length; c++)
        {
            ws.Cell(row, c + 1).Value = headers[c];
            ws.Cell(row, c + 1).Style.Font.Bold = true;
            ws.Cell(row, c + 1).Style.Fill.BackgroundColor = XLColor.FromHtml("#1e293b");
            ws.Cell(row, c + 1).Style.Font.FontColor = XLColor.White;
        }
        row++;

        foreach (var t in txns)
        {
            ws.Cell(row, 1).Value = t.Date.ToLocalTime().ToString("HH:mm");
            ws.Cell(row, 2).Value = t.Type.ToString();
            ws.Cell(row, 3).Value = t.Type == TransactionType.Disbursement ? -t.Amount : t.Amount;
            ws.Cell(row, 3).Style.NumberFormat.Format = "$#,##0.00";
            ws.Cell(row, 3).Style.Font.FontColor = t.Type == TransactionType.Disbursement ? XLColor.Red : XLColor.DarkGreen;
            ws.Cell(row, 4).Value = t.SourceOrPurpose;
            ws.Cell(row, 5).Value = t.Reference;
            ws.Cell(row, 6).Value = t.CreatedByUser?.FullName ?? "";
            row++;
        }

        ws.Columns().AdjustToContents();

        var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;
        return File(ms, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"{fileName}.xlsx");
    }

    private IActionResult ExcelMonthlyCash(
        string title, string fileName, string monthName,
        List<DaySummary> days, decimal totalAdds, decimal totalDisb,
        int newLoans, decimal repayments)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Monthly Cash");

        ws.Cell("A1").Value = "CALM – Cash & Liquidity Management";
        ws.Cell("A1").Style.Font.Bold = true;
        ws.Cell("A1").Style.Font.FontSize = 14;
        ws.Cell("A2").Value = "Welble Investments P/L";
        ws.Cell("A2").Style.Font.FontColor = XLColor.Gray;
        ws.Cell("A3").Value = title;
        ws.Cell("A3").Style.Font.Bold = true;
        ws.Cell("A4").Value = $"Generated: {DateTime.Now:dd MMM yyyy HH:mm}";
        ws.Cell("A4").Style.Font.FontColor = XLColor.Gray;
        ws.Cell("A4").Style.Font.FontSize = 9;

        // Summary
        ws.Cell("A6").Value = "MONTHLY SUMMARY";
        ws.Cell("A6").Style.Font.Bold = true;
        ws.Cell("A6").Style.Fill.BackgroundColor = XLColor.FromHtml("#f59e0b");
        ws.Range("A6:B6").Merge();

        int row = 7;
        ws.Cell(row, 1).Value = "Total Cash In";        ws.Cell(row, 2).Value = totalAdds;              ws.Cell(row, 2).Style.NumberFormat.Format = "$#,##0.00"; row++;
        ws.Cell(row, 1).Value = "Total Cash Out";       ws.Cell(row, 2).Value = totalDisb;              ws.Cell(row, 2).Style.NumberFormat.Format = "$#,##0.00"; row++;
        ws.Cell(row, 1).Value = "Net Cash Flow";        ws.Cell(row, 2).Value = totalAdds - totalDisb;  ws.Cell(row, 2).Style.NumberFormat.Format = "$#,##0.00"; ws.Cell(row, 1).Style.Font.Bold = true; ws.Cell(row, 2).Style.Font.Bold = true; row++;
        ws.Cell(row, 1).Value = "New Loans Issued";     ws.Cell(row, 2).Value = newLoans; row++;
        ws.Cell(row, 1).Value = "Repayments Collected"; ws.Cell(row, 2).Value = repayments;             ws.Cell(row, 2).Style.NumberFormat.Format = "$#,##0.00"; row++;

        // Day-by-day table
        row += 1;
        var hdrs = new[] { "Date", "Day", "Cash In (USD)", "Cash Out (USD)", "Net (USD)", "Transactions", "Narration" };
        for (int c = 0; c < hdrs.Length; c++)
        {
            ws.Cell(row, c + 1).Value = hdrs[c];
            ws.Cell(row, c + 1).Style.Font.Bold = true;
            ws.Cell(row, c + 1).Style.Fill.BackgroundColor = XLColor.FromHtml("#1e293b");
            ws.Cell(row, c + 1).Style.Font.FontColor = XLColor.White;
        }
        row++;

        foreach (var d in days)
        {
            ws.Cell(row, 1).Value = d.Date.ToString("dd MMM yyyy");
            ws.Cell(row, 2).Value = d.Date.DayOfWeek.ToString().Substring(0, 3);
            ws.Cell(row, 3).Value = d.Additions;
            ws.Cell(row, 3).Style.NumberFormat.Format = "$#,##0.00";
            ws.Cell(row, 4).Value = d.Disbursements;
            ws.Cell(row, 4).Style.NumberFormat.Format = "$#,##0.00";
            decimal net = d.Additions - d.Disbursements;
            ws.Cell(row, 5).Value = net;
            ws.Cell(row, 5).Style.NumberFormat.Format = "$#,##0.00";
            ws.Cell(row, 5).Style.Font.FontColor = net >= 0 ? XLColor.DarkGreen : XLColor.Red;
            ws.Cell(row, 6).Value = d.TxnCount;
            ws.Cell(row, 7).Value = d.Narration;
            ws.Cell(row, 7).Style.Font.FontSize = 9;
            row++;
        }

        // Totals row
        ws.Cell(row, 1).Value = "TOTAL";
        ws.Cell(row, 1).Style.Font.Bold = true;
        ws.Cell(row, 3).Value = totalAdds;
        ws.Cell(row, 3).Style.NumberFormat.Format = "$#,##0.00";
        ws.Cell(row, 3).Style.Font.Bold = true;
        ws.Cell(row, 4).Value = totalDisb;
        ws.Cell(row, 4).Style.NumberFormat.Format = "$#,##0.00";
        ws.Cell(row, 4).Style.Font.Bold = true;
        ws.Cell(row, 5).Value = totalAdds - totalDisb;
        ws.Cell(row, 5).Style.NumberFormat.Format = "$#,##0.00";
        ws.Cell(row, 5).Style.Font.Bold = true;
        ws.Cell(row, 5).Style.Font.FontColor = (totalAdds - totalDisb) >= 0 ? XLColor.DarkGreen : XLColor.Red;

        ws.Column(7).Width = 60;
        ws.Columns().AdjustToContents();
        ws.Column(7).Width = 60;

        var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;
        return File(ms, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"{fileName}.xlsx");
    }

    // ════════════════════════════════════════════════════════════════
    // PDF helpers
    // ════════════════════════════════════════════════════════════════

    private IActionResult PdfDailyCash(
        string title, string fileName, DateTime date,
        List<Domain.Entities.CashTransaction> txns,
        decimal opening, decimal adds, decimal disb, decimal closing,
        Domain.Entities.CashReconciliation? rec)
    {
        QuestPDF.Settings.License = LicenseType.Community;

        var doc = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(30);
                page.DefaultTextStyle(t => t.FontSize(9).FontFamily("Arial"));

                page.Header().Column(col =>
                {
                    col.Item().Row(row =>
                    {
                        row.RelativeItem().Column(c =>
                        {
                            c.Item().Text("CALM – Cash & Liquidity Management").Bold().FontSize(14).FontColor("#f59e0b");
                            c.Item().Text("Welble Investments P/L").FontSize(9).FontColor("#94a3b8");
                        });
                        row.ConstantItem(160).AlignRight().Column(c =>
                        {
                            c.Item().Text(title).Bold().FontSize(11);
                            c.Item().Text($"Generated: {DateTime.Now:dd MMM yyyy HH:mm}").FontSize(8).FontColor("#94a3b8");
                        });
                    });
                    col.Item().PaddingTop(6).LineHorizontal(1).LineColor("#f59e0b");
                });

                page.Content().PaddingTop(12).Column(col =>
                {
                    // Summary
                    col.Item().Text("SUMMARY").Bold().FontSize(10).FontColor("#f59e0b");
                    col.Item().PaddingTop(4).Table(tbl =>
                    {
                        tbl.ColumnsDefinition(c => { c.RelativeColumn(3); c.RelativeColumn(2); });
                        void SummaryRow(string lbl, decimal val, bool bold = false, string? color = null)
                        {
                            var lc = tbl.Cell().Padding(4).Background(bold ? "#f8fafc" : "#ffffff").Text(lbl).FontSize(9);
                            if (bold) lc.Bold();
                            var vc = tbl.Cell().Padding(4).AlignRight().Background(bold ? "#f8fafc" : "#ffffff").Text($"${val:N2}").FontSize(9);
                            if (bold)  vc.Bold();
                            if (color != null) vc.FontColor(color);
                        }
                        SummaryRow("Opening Balance", opening);
                        SummaryRow("Cash Added",      adds,    color: "#16a34a");
                        SummaryRow("Cash Disbursed",  disb,    color: "#dc2626");
                        SummaryRow("Closing Balance", closing, bold: true);
                        if (rec != null)
                        {
                            tbl.Cell().Padding(4).Text("Reconciliation").Bold().FontSize(9);
                            tbl.Cell().Padding(4).AlignRight()
                               .Text(rec.Variance == 0 ? "✓ Balanced" : $"⚠ Variance ${rec.Variance:N2}")
                               .FontColor(rec.Variance == 0 ? "#16a34a" : "#dc2626").Bold().FontSize(9);
                        }
                    });

                    // Transactions
                    col.Item().PaddingTop(14).Text("TRANSACTIONS").Bold().FontSize(10).FontColor("#f59e0b");
                    col.Item().PaddingTop(4).Table(tbl =>
                    {
                        tbl.ColumnsDefinition(c =>
                        {
                            c.ConstantColumn(42);  // Time
                            c.ConstantColumn(70);  // Type
                            c.RelativeColumn(2);   // Source
                            c.ConstantColumn(80);  // Ref
                            c.ConstantColumn(70);  // Amount
                            c.ConstantColumn(80);  // By
                        });

                        void Hdr(string t) => tbl.Cell().Background("#1e293b").Padding(5)
                            .Text(t).Bold().FontSize(8).FontColor("#ffffff");
                        Hdr("Time"); Hdr("Type"); Hdr("Source / Purpose"); Hdr("Reference"); Hdr("Amount"); Hdr("By");

                        bool alt = false;
                        foreach (var t in txns)
                        {
                            var bg = alt ? "#f8fafc" : "#ffffff"; alt = !alt;
                            tbl.Cell().Background(bg).Padding(4).Text(t.Date.ToLocalTime().ToString("HH:mm")).FontSize(8);
                            tbl.Cell().Background(bg).Padding(4).Text(t.Type.ToString()).FontSize(8);
                            tbl.Cell().Background(bg).Padding(4).Text(t.SourceOrPurpose).FontSize(8);
                            tbl.Cell().Background(bg).Padding(4).Text(t.Reference).FontSize(8);
                            var amtColor = t.Type == TransactionType.Disbursement ? "#dc2626" : "#16a34a";
                            var prefix   = t.Type == TransactionType.Disbursement ? "-" : "+";
                            tbl.Cell().Background(bg).Padding(4).AlignRight()
                               .Text($"{prefix}${t.Amount:N2}").FontSize(8).Bold().FontColor(amtColor);
                            tbl.Cell().Background(bg).Padding(4).Text(t.CreatedByUser?.FullName ?? "").FontSize(8);
                        }
                    });
                });

                page.Footer().AlignCenter()
                    .Text(txt =>
                    {
                        txt.Span("CALM System — Welble Investments P/L   |   Page ").FontSize(8).FontColor("#94a3b8");
                        txt.CurrentPageNumber().FontSize(8).FontColor("#94a3b8");
                        txt.Span(" of ").FontSize(8).FontColor("#94a3b8");
                        txt.TotalPages().FontSize(8).FontColor("#94a3b8");
                    });
            });
        });

        var ms = new MemoryStream();
        doc.GeneratePdf(ms);
        ms.Position = 0;
        return File(ms, "application/pdf", $"{fileName}.pdf");
    }

    private IActionResult PdfMonthlyCash(
        string title, string fileName, string monthName,
        List<DaySummary> days, decimal totalAdds, decimal totalDisb,
        int newLoans, decimal repayments)
    {
        QuestPDF.Settings.License = LicenseType.Community;

        var doc = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(30);
                page.DefaultTextStyle(t => t.FontSize(9).FontFamily("Arial"));

                page.Header().Column(col =>
                {
                    col.Item().Row(row =>
                    {
                        row.RelativeItem().Column(c =>
                        {
                            c.Item().Text("CALM – Cash & Liquidity Management").Bold().FontSize(14).FontColor("#f59e0b");
                            c.Item().Text("Welble Investments P/L").FontSize(9).FontColor("#94a3b8");
                        });
                        row.ConstantItem(160).AlignRight().Column(c =>
                        {
                            c.Item().Text(title).Bold().FontSize(11);
                            c.Item().Text($"Generated: {DateTime.Now:dd MMM yyyy HH:mm}").FontSize(8).FontColor("#94a3b8");
                        });
                    });
                    col.Item().PaddingTop(6).LineHorizontal(1).LineColor("#f59e0b");
                });

                page.Content().PaddingTop(12).Column(col =>
                {
                    // Summary
                    col.Item().Text("MONTHLY SUMMARY").Bold().FontSize(10).FontColor("#f59e0b");
                    col.Item().PaddingTop(4).Table(tbl =>
                    {
                        tbl.ColumnsDefinition(c => { c.RelativeColumn(3); c.RelativeColumn(2); });
                        void SR(string lbl, string val, bool bold = false, string? color = null)
                        {
                            var lc = tbl.Cell().Padding(4).Background(bold ? "#f8fafc" : "#fff").Text(lbl).FontSize(9);
                            if (bold) lc.Bold();
                            var vc = tbl.Cell().Padding(4).AlignRight().Background(bold ? "#f8fafc" : "#fff").Text(val).FontSize(9);
                            if (bold)  vc.Bold();
                            if (color != null) vc.FontColor(color);
                        }
                        SR("Total Cash In",         $"${totalAdds:N2}",               color: "#16a34a");
                        SR("Total Cash Out",        $"${totalDisb:N2}",               color: "#dc2626");
                        SR("Net Cash Flow",         $"${totalAdds - totalDisb:N2}",   bold: true, color: totalAdds >= totalDisb ? "#16a34a" : "#dc2626");
                        SR("New Loans Issued",      $"{newLoans}");
                        SR("Repayments Collected",  $"${repayments:N2}");
                    });

                    // Daily table
                    col.Item().PaddingTop(14).Text("DAY-BY-DAY BREAKDOWN").Bold().FontSize(10).FontColor("#f59e0b");
                    col.Item().PaddingTop(4).Table(tbl =>
                    {
                        tbl.ColumnsDefinition(c =>
                        {
                            c.RelativeColumn(2);
                            c.ConstantColumn(32);
                            c.RelativeColumn(2);
                            c.RelativeColumn(2);
                            c.RelativeColumn(2);
                            c.ConstantColumn(40);
                            c.RelativeColumn(6);
                        });

                        void Hdr(string t) => tbl.Cell().Background("#1e293b").Padding(5)
                            .Text(t).Bold().FontSize(8).FontColor("#ffffff");
                        Hdr("Date"); Hdr("Day"); Hdr("Cash In"); Hdr("Cash Out"); Hdr("Net"); Hdr("Txns"); Hdr("Narration");

                        bool alt = false;
                        foreach (var d in days)
                        {
                            var bg = alt ? "#f8fafc" : "#ffffff"; alt = !alt;
                            decimal net = d.Additions - d.Disbursements;
                            bool hasData = d.TxnCount > 0;

                            var dateCell = tbl.Cell().Background(bg).Padding(4).Text(d.Date.ToString("dd MMM")).FontSize(8);
                            if (hasData) dateCell.Bold();
                            tbl.Cell().Background(bg).Padding(4).AlignCenter().Text(d.Date.DayOfWeek.ToString().Substring(0, 3)).FontSize(8).FontColor("#94a3b8");
                            tbl.Cell().Background(bg).Padding(4).AlignRight().Text(hasData ? $"${d.Additions:N2}" : "—").FontSize(8).FontColor(hasData ? "#16a34a" : "#cbd5e1");
                            tbl.Cell().Background(bg).Padding(4).AlignRight().Text(hasData ? $"${d.Disbursements:N2}" : "—").FontSize(8).FontColor(hasData ? "#dc2626" : "#cbd5e1");
                            var netCell = tbl.Cell().Background(bg).Padding(4).AlignRight().Text(hasData ? $"${net:N2}" : "—").FontSize(8).FontColor(!hasData ? "#cbd5e1" : net >= 0 ? "#16a34a" : "#dc2626");
                            if (hasData) netCell.Bold();
                            tbl.Cell().Background(bg).Padding(4).AlignCenter().Text(hasData ? d.TxnCount.ToString() : "—").FontSize(8).FontColor(hasData ? "#0f172a" : "#cbd5e1");
                            tbl.Cell().Background(bg).Padding(4).Text(d.Narration).FontSize(7).FontColor("#475569");
                        }

                        // Totals
                        tbl.Cell().Background("#f1f5f9").Padding(4).Text("TOTAL").Bold().FontSize(8);
                        tbl.Cell().Background("#f1f5f9").Padding(4).Text("").FontSize(8);
                        tbl.Cell().Background("#f1f5f9").Padding(4).AlignRight().Text($"${totalAdds:N2}").Bold().FontSize(8).FontColor("#16a34a");
                        tbl.Cell().Background("#f1f5f9").Padding(4).AlignRight().Text($"${totalDisb:N2}").Bold().FontSize(8).FontColor("#dc2626");
                        var totalNet = totalAdds - totalDisb;
                        tbl.Cell().Background("#f1f5f9").Padding(4).AlignRight().Text($"${totalNet:N2}").Bold().FontSize(8).FontColor(totalNet >= 0 ? "#16a34a" : "#dc2626");
                        tbl.Cell().Background("#f1f5f9").Padding(4).Text("").FontSize(8);
                        tbl.Cell().Background("#f1f5f9").Padding(4).Text("").FontSize(8);
                    });
                });

                page.Footer().AlignCenter()
                    .Text(txt =>
                    {
                        txt.Span("CALM System — Welble Investments P/L   |   Page ").FontSize(8).FontColor("#94a3b8");
                        txt.CurrentPageNumber().FontSize(8).FontColor("#94a3b8");
                        txt.Span(" of ").FontSize(8).FontColor("#94a3b8");
                        txt.TotalPages().FontSize(8).FontColor("#94a3b8");
                    });
            });
        });

        var ms = new MemoryStream();
        doc.GeneratePdf(ms);
        ms.Position = 0;
        return File(ms, "application/pdf", $"{fileName}.pdf");
    }
}
