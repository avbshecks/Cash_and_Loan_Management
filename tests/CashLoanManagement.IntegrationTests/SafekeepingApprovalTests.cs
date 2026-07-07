using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace CashLoanManagement.IntegrationTests;

[Collection("CashLoanApi")]
public class SafekeepingApprovalTests
{
    private readonly CashLoanApiFactory _factory;

    public SafekeepingApprovalTests(CashLoanApiFactory factory) => _factory = factory;

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage res) =>
        (await res.Content.ReadFromJsonAsync<JsonElement>());

    [Fact]
    public async Task Deposit_AndWithdrawal_OnlyAffectBalance_OnceApproved()
    {
        var cashier = await _factory.LoginAsAsync("it_cashier");
        var manager = await _factory.LoginAsAsync("it_manager");

        var acctRes = await cashier.PostAsJsonAsync("/api/safekeeping/account",
            new { depositorName = "IT Depositor", phone = "0771111111" });
        acctRes.EnsureSuccessStatusCode();
        var accountId = (await ReadAsync(acctRes)).GetProperty("id").GetInt32();

        // Deposit ("leave money") — pending, balance stays 0.
        var depRes = await cashier.PostAsJsonAsync($"/api/safekeeping/accounts/{accountId}/deposit", new { amount = 500.00m });
        depRes.EnsureSuccessStatusCode();
        Assert.Equal("PendingApproval", (await ReadAsync(depRes)).GetProperty("status").GetString());

        var afterDepositRequest = await ReadAsync(await cashier.GetAsync($"/api/safekeeping/accounts/{accountId}"));
        Assert.Equal(0m, afterDepositRequest.GetProperty("currentBalance").GetDecimal());

        var depositTxId = (await ReadAsync(await manager.GetAsync("/api/safekeeping/pending")))
            .EnumerateArray().First(t => t.GetProperty("accountId").GetInt32() == accountId).GetProperty("id").GetInt32();

        var approveDeposit = await manager.PostAsync($"/api/safekeeping/withdrawals/{depositTxId}/approve", null);
        approveDeposit.EnsureSuccessStatusCode();

        var afterDepositApproved = await ReadAsync(await cashier.GetAsync($"/api/safekeeping/accounts/{accountId}"));
        Assert.Equal(500.00m, afterDepositApproved.GetProperty("currentBalance").GetDecimal());

        // Withdrawal ("collect money") — pending, balance stays 500 until approved.
        var wRes = await cashier.PostAsJsonAsync($"/api/safekeeping/accounts/{accountId}/withdraw", new { amount = 200.00m });
        wRes.EnsureSuccessStatusCode();

        var afterWithdrawRequest = await ReadAsync(await cashier.GetAsync($"/api/safekeeping/accounts/{accountId}"));
        Assert.Equal(500.00m, afterWithdrawRequest.GetProperty("currentBalance").GetDecimal());

        var withdrawTxId = (await ReadAsync(await manager.GetAsync("/api/safekeeping/pending")))
            .EnumerateArray().First(t => t.GetProperty("accountId").GetInt32() == accountId).GetProperty("id").GetInt32();
        var approveWithdraw = await manager.PostAsync($"/api/safekeeping/withdrawals/{withdrawTxId}/approve", null);
        approveWithdraw.EnsureSuccessStatusCode();

        var afterWithdrawApproved = await ReadAsync(await cashier.GetAsync($"/api/safekeeping/accounts/{accountId}"));
        Assert.Equal(300.00m, afterWithdrawApproved.GetProperty("currentBalance").GetDecimal());
    }

    [Fact]
    public async Task WithdrawalExceedingAvailableBalance_IsRejectedAtRequestTime()
    {
        var cashier = await _factory.LoginAsAsync("it_cashier");

        var acctRes = await cashier.PostAsJsonAsync("/api/safekeeping/account",
            new { depositorName = "IT Overdraw Test", phone = "0772222222" });
        var accountId = (await ReadAsync(acctRes)).GetProperty("id").GetInt32();

        var wRes = await cashier.PostAsJsonAsync($"/api/safekeeping/accounts/{accountId}/withdraw", new { amount = 50.00m });
        Assert.Equal(HttpStatusCode.BadRequest, wRes.StatusCode);
    }

    [Fact]
    public async Task Admin_CannotApprove_TheirOwnDeposit()
    {
        var admin = await _factory.LoginAsAsync("it_admin");

        var acctRes = await admin.PostAsJsonAsync("/api/safekeeping/account",
            new { depositorName = "IT Admin Self-Approve Test", phone = "0773333333" });
        var accountId = (await ReadAsync(acctRes)).GetProperty("id").GetInt32();

        var depRes = await admin.PostAsJsonAsync($"/api/safekeeping/accounts/{accountId}/deposit", new { amount = 10.00m });
        depRes.EnsureSuccessStatusCode();

        var depositTxId = (await ReadAsync(await admin.GetAsync("/api/safekeeping/pending")))
            .EnumerateArray().First(t => t.GetProperty("accountId").GetInt32() == accountId).GetProperty("id").GetInt32();

        var selfApprove = await admin.PostAsync($"/api/safekeeping/withdrawals/{depositTxId}/approve", null);
        Assert.Equal(HttpStatusCode.BadRequest, selfApprove.StatusCode);
    }
}
