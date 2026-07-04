using ClosedXML.Excel;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace CashLoanManagement.Api.Controllers;

/// <summary>Weekly (Mon–Sun) / monthly UTC date ranges with a display label.</summary>
public static class PeriodUtil
{
    /// <summary>Custom from/to range (inclusive dates) takes precedence when both parse; otherwise falls back to period/date.</summary>
    public static (DateTime Start, DateTime End, string Label, string Slug) Range(string? period, string? date, string? from, string? to)
    {
        if (DateTime.TryParse(from, out var f) && DateTime.TryParse(to, out var t))
        {
            var s = DateTime.SpecifyKind(f.Date, DateTimeKind.Utc);
            var e = DateTime.SpecifyKind(t.Date.AddDays(1), DateTimeKind.Utc);
            if (e <= s) e = s.AddDays(1);
            return (s, e, $"{s:dd MMM yyyy} – {e.AddDays(-1):dd MMM yyyy}", $"{s:yyyyMMdd}-{e.AddDays(-1):yyyyMMdd}");
        }
        return Range(period, date);
    }

    public static (DateTime Start, DateTime End, string Label, string Slug) Range(string? period, string? date)
    {
        var raw = DateTime.TryParse(date, out var p) ? p : DateTime.UtcNow;
        if (string.Equals(period, "monthly", StringComparison.OrdinalIgnoreCase))
        {
            var s = new DateTime(raw.Year, raw.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            return (s, s.AddMonths(1), s.ToString("MMMM yyyy"), s.ToString("yyyyMM"));
        }
        var offset = ((int)raw.DayOfWeek + 6) % 7;
        var ws = DateTime.SpecifyKind(raw.Date.AddDays(-offset), DateTimeKind.Utc);
        return (ws, ws.AddDays(7), $"{ws:dd MMM} – {ws.AddDays(6):dd MMM yyyy}", ws.ToString("yyyyMMdd"));
    }
}

/// <summary>
/// Generic CALM-branded Excel: header, summary key/values, then a data table.
/// Cells that are decimal are money-formatted; "!r"/"!g" string prefixes colour red/green.
/// </summary>
public static class ExcelTable
{
    public static MemoryStream Build(
        string title,
        List<(string Label, string Value)> summary,
        string[] headers,
        List<object?[]> rows)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Report");
        ws.Cell("A1").Value = "CALM – Cash & Liquidity Management";
        ws.Cell("A1").Style.Font.Bold = true; ws.Cell("A1").Style.Font.FontSize = 14;
        ws.Cell("A2").Value = "Welble Investments P/L"; ws.Cell("A2").Style.Font.FontColor = XLColor.Gray;
        ws.Cell("A3").Value = title; ws.Cell("A3").Style.Font.Bold = true;
        ws.Cell("A4").Value = $"Generated: {DateTime.Now:dd MMM yyyy HH:mm}";
        ws.Cell("A4").Style.Font.FontColor = XLColor.Gray; ws.Cell("A4").Style.Font.FontSize = 9;

        int r = 6;
        if (summary.Count > 0)
        {
            ws.Cell(r, 1).Value = "SUMMARY";
            ws.Cell(r, 1).Style.Font.Bold = true;
            ws.Range(r, 1, r, 2).Merge().Style.Fill.BackgroundColor = XLColor.FromHtml("#f59e0b");
            r++;
            foreach (var (label, value) in summary)
            {
                ws.Cell(r, 1).Value = label; ws.Cell(r, 2).Value = value; r++;
            }
            r++;
        }

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
            for (int c = 0; c < headers.Length; c++)
            {
                var v = c < row.Length ? row[c] : null;
                var cell = ws.Cell(r, c + 1);
                if (v is decimal d)
                {
                    cell.Value = d;
                    cell.Style.NumberFormat.Format = "$#,##0.00";
                    if (d < 0) cell.Style.Font.FontColor = XLColor.Red;
                }
                else if (v is string s)
                {
                    if (s.StartsWith("!r")) { cell.Value = s[2..]; cell.Style.Font.FontColor = XLColor.Red; }
                    else if (s.StartsWith("!g")) { cell.Value = s[2..]; cell.Style.Font.FontColor = XLColor.DarkGreen; }
                    else cell.Value = s;
                }
                else if (v != null) cell.Value = v.ToString();
            }
            r++;
        }

        ws.Columns().AdjustToContents();
        var ms = new MemoryStream(); wb.SaveAs(ms); ms.Position = 0;
        return ms;
    }
}

/// <summary>
/// Generic CALM-branded PDF for tabular reports: header, optional summary
/// key/value block, then a data table. Cell strings may carry a colour hint
/// prefix: "!r" = red, "!g" = green (stripped before rendering).
/// </summary>
public static class ReportPdf
{
    public static MemoryStream Build(
        string title,
        List<(string Label, string Value)> summary,
        string[] headers,
        List<string[]> rows,
        int[]? rightAlign = null)
    {
        QuestPDF.Settings.License = LicenseType.Community;
        rightAlign ??= Array.Empty<int>();

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
                        row.ConstantItem(190).AlignRight().Column(c =>
                        {
                            c.Item().Text(title).Bold().FontSize(11);
                            c.Item().Text($"Generated: {DateTime.Now:dd MMM yyyy HH:mm}").FontSize(8).FontColor("#94a3b8");
                        });
                    });
                    col.Item().PaddingTop(6).LineHorizontal(1).LineColor("#f59e0b");
                });

                page.Content().PaddingTop(12).Column(col =>
                {
                    if (summary.Count > 0)
                    {
                        col.Item().Text("SUMMARY").Bold().FontSize(10).FontColor("#f59e0b");
                        col.Item().PaddingTop(4).Table(tbl =>
                        {
                            tbl.ColumnsDefinition(c => { c.RelativeColumn(3); c.RelativeColumn(2); });
                            foreach (var (label, value) in summary)
                            {
                                tbl.Cell().Padding(4).Text(label).FontSize(9);
                                tbl.Cell().Padding(4).AlignRight().Text(value).Bold().FontSize(9);
                            }
                        });
                        col.Item().PaddingTop(12);
                    }

                    col.Item().Table(tbl =>
                    {
                        tbl.ColumnsDefinition(c =>
                        {
                            for (int i = 0; i < headers.Length; i++) c.RelativeColumn();
                        });

                        foreach (var h in headers)
                            tbl.Cell().Background("#1e293b").Padding(5).Text(h).Bold().FontSize(8).FontColor("#ffffff");

                        bool alt = false;
                        foreach (var row in rows)
                        {
                            var bg = alt ? "#f8fafc" : "#ffffff"; alt = !alt;
                            for (int i = 0; i < headers.Length; i++)
                            {
                                var raw = i < row.Length ? row[i] ?? "" : "";
                                string? color = null;
                                if (raw.StartsWith("!r")) { color = "#dc2626"; raw = raw[2..]; }
                                else if (raw.StartsWith("!g")) { color = "#16a34a"; raw = raw[2..]; }

                                var cell = tbl.Cell().Background(bg).Padding(4);
                                var txt  = (rightAlign.Contains(i) ? cell.AlignRight() : cell).Text(raw).FontSize(8);
                                if (color != null) txt.FontColor(color);
                            }
                        }
                    });
                });

                page.Footer().AlignCenter().Text(txt =>
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
        return ms;
    }
}
