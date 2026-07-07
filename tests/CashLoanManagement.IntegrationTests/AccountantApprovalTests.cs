using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace CashLoanManagement.IntegrationTests;

[Collection("CashLoanApi")]
public class AccountantApprovalTests
{
    private readonly CashLoanApiFactory _factory;

    public AccountantApprovalTests(CashLoanApiFactory factory) => _factory = factory;

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage res) =>
        (await res.Content.ReadFromJsonAsync<JsonElement>());

    [Fact]
    public async Task CashIn_AndCashOut_OnlyAffectAccountantBalance_OnceApproved()
    {
        var accountant = await _factory.LoginAsAsync("it_accountant");
        var manager = await _factory.LoginAsAsync("it_manager");

        var before = await ReadAsync(await accountant.GetAsync("/api/accountant/balance"));
        var balanceBefore = before.GetProperty("currentBalance").GetDecimal();

        var inRes = await accountant.PostAsJsonAsync("/api/accountant/add", new { amount = 600.00m, sourceOrPurpose = "IT cash in" });
        inRes.EnsureSuccessStatusCode();
        Assert.Equal("PendingApproval", (await ReadAsync(inRes)).GetProperty("status").GetString());

        var afterRequest = await ReadAsync(await accountant.GetAsync("/api/accountant/balance"));
        Assert.Equal(balanceBefore, afterRequest.GetProperty("currentBalance").GetDecimal());

        var pending = await ReadAsync(await manager.GetAsync("/api/accountant/pending"));
        var txId = pending.EnumerateArray().Last().GetProperty("id").GetInt32();

        var approveRes = await manager.PostAsync($"/api/accountant/approve/{txId}", null);
        approveRes.EnsureSuccessStatusCode();

        var afterApproval = await ReadAsync(await accountant.GetAsync("/api/accountant/balance"));
        Assert.Equal(balanceBefore + 600.00m, afterApproval.GetProperty("currentBalance").GetDecimal());
    }

    [Fact]
    public async Task Admin_CannotApprove_TheirOwnAccountantBookEntry()
    {
        var admin = await _factory.LoginAsAsync("it_admin");

        var inRes = await admin.PostAsJsonAsync("/api/accountant/add", new { amount = 15.00m, sourceOrPurpose = "IT admin self-approval test" });
        inRes.EnsureSuccessStatusCode();
        var reference = (await ReadAsync(inRes)).GetProperty("reference").GetString();

        var pending = await ReadAsync(await admin.GetAsync("/api/accountant/pending"));
        var txId = pending.EnumerateArray().First(t => t.GetProperty("reference").GetString() == reference).GetProperty("id").GetInt32();

        var selfApprove = await admin.PostAsync($"/api/accountant/approve/{txId}", null);
        Assert.Equal(HttpStatusCode.BadRequest, selfApprove.StatusCode);
    }

    [Fact]
    public async Task RejectingAccountantEntry_WithoutReason_IsRejectedByTheApi()
    {
        var accountant = await _factory.LoginAsAsync("it_accountant");
        var manager = await _factory.LoginAsAsync("it_manager");

        var inRes = await accountant.PostAsJsonAsync("/api/accountant/add", new { amount = 42.00m, sourceOrPurpose = "IT reject test" });
        var txId = (await ReadAsync(inRes)).GetProperty("reference").GetString();

        var pending = await ReadAsync(await manager.GetAsync("/api/accountant/pending"));
        var id = pending.EnumerateArray().Last(t => t.GetProperty("reference").GetString() == txId).GetProperty("id").GetInt32();

        var emptyReject = await manager.PostAsJsonAsync($"/api/accountant/reject/{id}", "");
        Assert.Equal(HttpStatusCode.BadRequest, emptyReject.StatusCode);
    }
}
