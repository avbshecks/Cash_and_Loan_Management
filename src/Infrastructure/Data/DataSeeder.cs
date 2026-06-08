using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using CashLoanManagement.Application.Common.Interfaces;
using CashLoanManagement.Domain.Entities;
using CashLoanManagement.Infrastructure.Persistence;

namespace CashLoanManagement.Infrastructure.Data;

public static class DataSeeder
{
    public static async Task SeedAsync(IServiceProvider serviceProvider)
    {
        using var scope = serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<CashLoanDbContext>();
        var passwordService = scope.ServiceProvider.GetRequiredService<IPasswordService>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<CashLoanDbContext>>();

        try
        {
            await context.Database.MigrateAsync();

            // Seed default Admin user if no users exist
            if (!await context.Users.AnyAsync())
            {
                logger.LogInformation("Seeding default admin user...");

                var adminRole = await context.Roles.FirstOrDefaultAsync(r => r.Name == "Admin");
                if (adminRole == null)
                {
                    logger.LogWarning("Admin role not found. Skipping user seed.");
                    return;
                }

                var adminUser = new User
                {
                    Username = "admin",
                    PasswordHash = passwordService.HashPassword("Admin@1234"),
                    FullName = "System Administrator",
                    Email = "admin@cashloan.local",
                    Phone = "0000000000",
                    IsActive = true,
                    MustChangePassword = false,
                    PasswordChangedAt = DateTime.UtcNow,
                    RoleId = adminRole.Id,
                    CreatedAt = DateTime.UtcNow
                };

                context.Users.Add(adminUser);
                await context.SaveChangesAsync();
                logger.LogInformation("Default admin user seeded successfully.");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "An error occurred while seeding the database.");
            throw;
        }
    }
}
