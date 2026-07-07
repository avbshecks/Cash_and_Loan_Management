using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using CashLoanManagement.Application.Common.Interfaces;
using CashLoanManagement.Domain.Entities;
using CashLoanManagement.Infrastructure.Persistence;
using Xunit;

namespace CashLoanManagement.IntegrationTests;

/// <summary>
/// Boots the real API (real controllers, real EF Core -> real Postgres SQL translation)
/// against a disposable "CashLoanDb_Test" database, so tests exercise the exact code path
/// that broke in production (EF Core translation of LINQ inside .Where clauses) rather than
/// an in-memory provider that would silently paper over the same bug.
/// </summary>
public class CashLoanApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private const string AdminConnectionString =
        "Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=Password123";
    private const string TestConnectionString =
        "Host=localhost;Port=5432;Database=CashLoanDb_Test;Username=postgres;Password=Password123;Include Error Detail=true";

    // Must match Program.cs exactly, and must run before ANY Npgsql operation in this process
    // (including the raw admin connection below) — Npgsql locks in its type-mapping behavior
    // on first use, so setting this only inside the app's own startup would be too late here.
    //
    // The connection string is also forced here via an environment variable — Program.cs reads
    // it through the default ASP.NET Core config chain, where env vars outrank appsettings.json,
    // so this is what actually keeps tests off the real "CashLoanDb" (ConfigureWebHost's
    // AddInMemoryCollection alone was NOT reliably winning against appsettings.json in practice).
    static CashLoanApiFactory()
    {
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
        Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", TestConnectionString);
    }

    public async Task InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(AdminConnectionString);
        await conn.OpenAsync();

        async Task ExecAsync(string sql)
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
        }

        // Terminate any lingering connections from a previous run, then recreate fresh.
        await ExecAsync(@"
            SELECT pg_terminate_backend(pid) FROM pg_stat_activity
            WHERE datname = 'CashLoanDb_Test' AND pid <> pg_backend_pid();");
        await ExecAsync("DROP DATABASE IF EXISTS \"CashLoanDb_Test\";");
        await ExecAsync("CREATE DATABASE \"CashLoanDb_Test\";");

        // Touching Services triggers host startup, which runs migrations + DataSeeder (admin user).
        _ = Services;

        // Hard safety net: never let this test run touch anything but the disposable test DB,
        // no matter how the connection string ends up resolved.
        using (var scope = Services.CreateScope())
        {
            var actual = scope.ServiceProvider.GetRequiredService<CashLoanDbContext>().Database.GetConnectionString();
            if (actual == null || !actual.Contains("CashLoanDb_Test", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Refusing to run integration tests: resolved connection string is not CashLoanDb_Test. Actual: {actual}");
        }

        await SeedTestUsersAsync();
        await SeedOpeningCashBalanceAsync();
    }

    /// <summary>Funds the main cashbox so loan-disbursement tests have something to draw down.</summary>
    private async Task SeedOpeningCashBalanceAsync()
    {
        var cashier = await LoginAsAsync("it_cashier");
        var res = await cashier.PostAsJsonAsync("/api/cash/opening-balance", new { amount = 1_000_000.00m, notes = "IT seed" });
        if (!res.IsSuccessStatusCode)
            throw new Exception($"opening-balance seed failed: {res.StatusCode} {await res.Content.ReadAsStringAsync()}");
    }

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = TestConnectionString
            });
        });
    }

    private async Task SeedTestUsersAsync()
    {
        using var scope = Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<CashLoanDbContext>();
        var pwd = scope.ServiceProvider.GetRequiredService<IPasswordService>();

        var roles = await context.Roles.ToListAsync();
        int RoleId(string name) => roles.First(r => r.Name == name).Id;

        async Task<User> EnsureUserAsync(string username, string roleName)
        {
            var existing = await context.Users.FirstOrDefaultAsync(u => u.Username == username);
            if (existing != null) return existing;

            var user = new User
            {
                Username = username,
                PasswordHash = pwd.HashPassword("Test@12345"),
                FullName = username,
                Email = $"{username}@test.local",
                Phone = "0000000000",
                IsActive = true,
                MustChangePassword = false,
                PasswordChangedAt = DateTime.UtcNow,
                RoleId = RoleId(roleName),
                CreatedAt = DateTime.UtcNow
            };
            context.Users.Add(user);
            await context.SaveChangesAsync();
            return user;
        }

        await EnsureUserAsync("it_cashier", "Cashier");
        await EnsureUserAsync("it_manager", "Manager");
        // Admin is the only role authorized as BOTH maker and checker on every one of these
        // endpoints, so self-approval blocking can only be genuinely exercised through it.
        await EnsureUserAsync("it_admin", "Admin");
        await EnsureUserAsync("it_accountant", "Accountant");
    }

    public async Task<HttpClient> LoginAsAsync(string username, string password = "Test@12345")
    {
        var client = CreateClient();
        var res = await client.PostAsJsonAsync("/api/auth/login", new { username, password });
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<LoginResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body!.Token);
        return client;
    }

    private record LoginResponse(string Token);
}
