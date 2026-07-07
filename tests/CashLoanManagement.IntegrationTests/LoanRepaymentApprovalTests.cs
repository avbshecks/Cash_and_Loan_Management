using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace CashLoanManagement.IntegrationTests;

[Collection("CashLoanApi")]
public class LoanRepaymentApprovalTests
{
    private readonly CashLoanApiFactory _factory;

    public LoanRepaymentApprovalTests(CashLoanApiFactory factory) => _factory = factory;

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage res) =>
        (await res.Content.ReadFromJsonAsync<JsonElement>());

    private async Task<int> CreateActiveLoanAsync(HttpClient cashier, HttpClient manager, decimal amount)
    {
        var nationalId = $"IT-{Guid.NewGuid():N}".Substring(0, 15);
        var borrowerRes = await cashier.PostAsJsonAsync("/api/loan/borrower", new
        {
            fullName = "IT Borrower", nationalId, phone = "0773333333",
            address = "Test", employmentStatus = "Employed"
        });
        borrowerRes.EnsureSuccessStatusCode();
        var borrowerId = (await ReadAsync(borrowerRes)).GetProperty("id").GetInt32();

        // Day loan is now itself two-step maker-checker (Pending -> approve -> disburse),
        // which conveniently gives us an Active loan to test repayments against.
        var dayLoanRes = await cashier.PostAsJsonAsync("/api/loan/day-loan", new { borrowerId, amount });
        dayLoanRes.EnsureSuccessStatusCode();
        var loanId = (await ReadAsync(dayLoanRes)).GetProperty("loanId").GetInt32();

        var approveRes = await manager.PostAsJsonAsync($"/api/loan/approve/{loanId}", "IT approval");
        approveRes.EnsureSuccessStatusCode();

        var disburseRes = await manager.PostAsync($"/api/loan/disburse/{loanId}", null);
        disburseRes.EnsureSuccessStatusCode();

        return loanId;
    }

    [Fact]
    public async Task DayLoan_RequiresApprovalAndDisbursement_BeforeItIsActive()
    {
        var cashier = await _factory.LoginAsAsync("it_cashier");
        var manager = await _factory.LoginAsAsync("it_manager");

        var nationalId = $"IT-{Guid.NewGuid():N}".Substring(0, 15);
        var borrowerRes = await cashier.PostAsJsonAsync("/api/loan/borrower", new
        {
            fullName = "IT Day Loan Borrower", nationalId, phone = "0774444444",
            address = "Test", employmentStatus = "Employed"
        });
        var borrowerId = (await ReadAsync(borrowerRes)).GetProperty("id").GetInt32();

        var dayLoanRes = await cashier.PostAsJsonAsync("/api/loan/day-loan", new { borrowerId, amount = 100.00m });
        dayLoanRes.EnsureSuccessStatusCode();
        var body = await ReadAsync(dayLoanRes);
        Assert.Equal("PendingApproval", body.GetProperty("status").GetString());

        var loanId = body.GetProperty("loanId").GetInt32();
        var loan = await ReadAsync(await cashier.GetAsync($"/api/loan/{loanId}"));
        Assert.Equal("Pending", loan.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Admin_CannotApprove_TheirOwnDayLoan()
    {
        var admin = await _factory.LoginAsAsync("it_admin");

        var nationalId = $"IT-{Guid.NewGuid():N}".Substring(0, 15);
        var borrowerRes = await admin.PostAsJsonAsync("/api/loan/borrower", new
        {
            fullName = "IT Admin Day Loan Borrower", nationalId, phone = "0775555555",
            address = "Test", employmentStatus = "Employed"
        });
        var borrowerId = (await ReadAsync(borrowerRes)).GetProperty("id").GetInt32();

        var dayLoanRes = await admin.PostAsJsonAsync("/api/loan/day-loan", new { borrowerId, amount = 20.00m });
        dayLoanRes.EnsureSuccessStatusCode();
        var loanId = (await ReadAsync(dayLoanRes)).GetProperty("loanId").GetInt32();

        var selfApprove = await admin.PostAsJsonAsync($"/api/loan/approve/{loanId}", "nope");
        Assert.Equal(HttpStatusCode.BadRequest, selfApprove.StatusCode);
    }

    [Fact]
    public async Task PendingRepayment_DoesNotReduceRemainingBalance_UntilApproved()
    {
        var cashier = await _factory.LoginAsAsync("it_cashier");
        var manager = await _factory.LoginAsAsync("it_manager");

        var loanId = await CreateActiveLoanAsync(cashier, manager, 400.00m);

        var repayRes = await cashier.PostAsJsonAsync("/api/loan/repayment", new { loanId, amount = 150.00m, reference = "x" });
        repayRes.EnsureSuccessStatusCode();
        Assert.Equal("PendingApproval", (await ReadAsync(repayRes)).GetProperty("status").GetString());

        var loanAfterRequest = await ReadAsync(await cashier.GetAsync($"/api/loan/{loanId}"));
        Assert.Equal(400.00m, loanAfterRequest.GetProperty("remainingBalance").GetDecimal());
        Assert.Equal("Active", loanAfterRequest.GetProperty("status").GetString());

        var pending = await ReadAsync(await manager.GetAsync("/api/loan/pending-repayments"));
        var repaymentId = pending.EnumerateArray().First(r => r.GetProperty("loanId").GetInt32() == loanId).GetProperty("id").GetInt32();

        var approveRes = await manager.PostAsync($"/api/loan/repayment/{repaymentId}/approve", null);
        approveRes.EnsureSuccessStatusCode();

        var loanAfterApproval = await ReadAsync(await cashier.GetAsync($"/api/loan/{loanId}"));
        Assert.Equal(250.00m, loanAfterApproval.GetProperty("remainingBalance").GetDecimal());
    }

    [Fact]
    public async Task FullRepaymentApproval_MarksLoanPaid()
    {
        var cashier = await _factory.LoginAsAsync("it_cashier");
        var manager = await _factory.LoginAsAsync("it_manager");

        var loanId = await CreateActiveLoanAsync(cashier, manager, 100.00m);

        var repayRes = await cashier.PostAsJsonAsync("/api/loan/repayment", new { loanId, amount = 100.00m, reference = "x" });
        repayRes.EnsureSuccessStatusCode();

        var pending = await ReadAsync(await manager.GetAsync("/api/loan/pending-repayments"));
        var repaymentId = pending.EnumerateArray().First(r => r.GetProperty("loanId").GetInt32() == loanId).GetProperty("id").GetInt32();

        var approveRes = await manager.PostAsync($"/api/loan/repayment/{repaymentId}/approve", null);
        approveRes.EnsureSuccessStatusCode();
        Assert.Equal("Paid", (await ReadAsync(approveRes)).GetProperty("loanStatus").GetString());
    }
}
