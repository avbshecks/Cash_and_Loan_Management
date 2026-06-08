using System;
using System.Security.Cryptography;
using System.Text;
using CashLoanManagement.Application.Common.Interfaces;

namespace CashLoanManagement.Infrastructure.Services;

public class PasswordService : IPasswordService
{
    public string HashPassword(string password)
    {
        // Use SHA256 with a salt for basic hashing (upgrade to BCrypt in production)
        var salt = GenerateSalt();
        var hash = ComputeHash(password, salt);
        return $"{salt}:{hash}";
    }

    public bool VerifyPassword(string password, string passwordHash)
    {
        var parts = passwordHash.Split(':');
        if (parts.Length != 2)
            return false;

        var salt = parts[0];
        var storedHash = parts[1];
        var computedHash = ComputeHash(password, salt);

        return storedHash.Equals(computedHash, StringComparison.Ordinal);
    }

    private static string GenerateSalt()
    {
        var saltBytes = new byte[16];
        RandomNumberGenerator.Fill(saltBytes);
        return Convert.ToBase64String(saltBytes);
    }

    private static string ComputeHash(string password, string salt)
    {
        var combined = Encoding.UTF8.GetBytes(password + salt);
        var hashBytes = SHA256.HashData(combined);
        return Convert.ToBase64String(hashBytes);
    }
}
