using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace CashLoanManagement.IntegrationTests;

[Collection("CashLoanApi")]
public class CashApprovalTests
{
    private readonly CashLoanApiFactory _factory;

    public CashApprovalTests(CashLoanApiFactory factory) => _factory = factory;

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage res) =>
        (await res.Content.ReadFromJsonAsync<JsonElement>());

    [Fact]
    public async Task PendingCashAddition_DoesNotChangeBalance_UntilApproved()
    {
        var cashier = await _factory.LoginAsAsync("it_cashier");
        var manager = await _factory.LoginAsAsync("it_manager");

        var before = await ReadAsync(await cashier.GetAsync("/api/cash/balance"));
        var balanceBefore = before.GetProperty("currentBalance").GetDecimal();

        var addRes = await cashier.PostAsJsonAsync("/api/cash/add",
            new { amount = 321.00m, source = "IT: pending addition", reference = "IT-ADD-1" });
        addRes.EnsureSuccessStatusCode();
        var added = await ReadAsync(addRes);
        Assert.Equal("PendingApproval", added.GetProperty("status").GetString());
        var txId = added.GetProperty("transactionId").GetInt32();

        // Balance must be untouched while Pending — this is the whole point of maker-checker.
        var afterRequest = await ReadAsync(await cashier.GetAsync("/api/cash/balance"));
        Assert.Equal(balanceBefore, afterRequest.GetProperty("currentBalance").GetDecimal());

        // Checker approves -> balance now moves by exactly the requested amount.
        var approveRes = await manager.PostAsync($"/api/cash/approve/{txId}", null);
        approveRes.EnsureSuccessStatusCode();

        var afterApproval = await ReadAsync(await cashier.GetAsync("/api/cash/balance"));
        Assert.Equal(balanceBefore + 321.00m, afterApproval.GetProperty("currentBalance").GetDecimal());
    }

    [Fact]
    public async Task Admin_CannotApprove_TheirOwnCashAddition()
    {
        // Admin is the only role authorized to both create AND approve — every other maker
        // role (Cashier) is blocked from even calling the approve endpoint (403), so the
        // self-approval guard can only be genuinely exercised through a same-role Admin.
        var admin = await _factory.LoginAsAsync("it_admin");

        var addRes = await admin.PostAsJsonAsync("/api/cash/add",
            new { amount = 55.00m, source = "IT: admin self-approval test", reference = "IT-ADD-SELF" });
        addRes.EnsureSuccessStatusCode();
        var txId = (await ReadAsync(addRes)).GetProperty("transactionId").GetInt32();

        var selfApprove = await admin.PostAsync($"/api/cash/approve/{txId}", null);
        Assert.Equal(HttpStatusCode.BadRequest, selfApprove.StatusCode);
    }

    [Fact]
    public async Task RejectedCashAddition_NeverAffectsBalance_AndRequiresAReason()
    {
        var cashier = await _factory.LoginAsAsync("it_cashier");
        var manager = await _factory.LoginAsAsync("it_manager");

        var before = await ReadAsync(await cashier.GetAsync("/api/cash/balance"));
        var balanceBefore = before.GetProperty("currentBalance").GetDecimal();

        var addRes = await cashier.PostAsJsonAsync("/api/cash/add",
            new { amount = 88.00m, source = "IT: reject test", reference = "IT-ADD-2" });
        var added = await ReadAsync(addRes);
        var txId = added.GetProperty("transactionId").GetInt32();

        // Empty reason must be rejected outright.
        var emptyReject = await manager.PostAsJsonAsync($"/api/cash/reject/{txId}", "");
        Assert.Equal(HttpStatusCode.BadRequest, emptyReject.StatusCode);

        var rejectRes = await manager.PostAsJsonAsync($"/api/cash/reject/{txId}", "did not happen");
        rejectRes.EnsureSuccessStatusCode();

        var after = await ReadAsync(await cashier.GetAsync("/api/cash/balance"));
        Assert.Equal(balanceBefore, after.GetProperty("currentBalance").GetDecimal());
    }

    [Fact]
    public async Task DailyAndWeeklyCashReports_ReturnOk_WithApprovalStatusFilterApplied()
    {
        // Regression guard for the bug where IsPosted(t.ApprovalStatus) inside a still-IQueryable
        // .Where() clause crashed with "could not be translated" against Postgres.
        var admin = await _factory.LoginAsAsync("it_manager");

        var daily = await admin.GetAsync($"/api/report/daily-cash?date={DateTime.UtcNow:yyyy-MM-dd}");
        Assert.Equal(HttpStatusCode.OK, daily.StatusCode);

        var weekly = await admin.GetAsync("/api/report/weekly-cash");
        Assert.Equal(HttpStatusCode.OK, weekly.StatusCode);

        var monthly = await admin.GetAsync($"/api/report/monthly-cash?year={DateTime.UtcNow.Year}&month={DateTime.UtcNow.Month}");
        Assert.Equal(HttpStatusCode.OK, monthly.StatusCode);
    }
}
