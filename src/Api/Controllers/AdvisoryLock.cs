using Microsoft.EntityFrameworkCore;
using CashLoanManagement.Infrastructure.Persistence;

namespace CashLoanManagement.Api.Controllers;

/// <summary>
/// Serializes concurrent balance-check-then-write sequences (e.g. two Managers approving
/// two disbursements at once) using a Postgres session advisory lock scoped to the current
/// transaction. The lock is released automatically on commit/rollback — callers must already
/// be inside a transaction (see <see cref="WithLockAsync"/>).
/// </summary>
public static class AdvisoryLock
{
    // Fixed keys for the single-balance books; safekeeping is keyed per-account since each
    // account has its own independent balance and shouldn't block unrelated accounts.
    public const long MainCashBook = 1;
    public const long AccountantBook = 2;
    public const long SafekeepingBookBase = 1000; // + accountId

    /// <summary>Runs <paramref name="action"/> inside a DB transaction holding an advisory
    /// lock for <paramref name="key"/>, then commits. Any exception rolls back automatically.
    /// Uses the context's own execution strategy (Npgsql's EnableRetryOnFailure) so the whole
    /// lock+check+write unit is retried atomically together if a transient fault occurs — EF
    /// Core forbids opening a bare transaction directly when a retrying strategy is registered.</summary>
    public static async Task<T> WithLockAsync<T>(CashLoanDbContext context, long key, Func<Task<T>> action)
    {
        var strategy = context.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await context.Database.BeginTransactionAsync();
            await context.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})", key);
            var result = await action();
            await tx.CommitAsync();
            return result;
        });
    }
}
