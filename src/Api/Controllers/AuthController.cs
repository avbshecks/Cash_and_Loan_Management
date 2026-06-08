using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using CashLoanManagement.Application.Common.Interfaces;
using CashLoanManagement.Application.DTOs.Auth;
using CashLoanManagement.Domain.Entities;
using CashLoanManagement.Infrastructure.Persistence;

namespace CashLoanManagement.Api.Controllers;

public class AuthController : BaseApiController
{
    private readonly CashLoanDbContext _context;
    private readonly ITokenService _tokenService;
    private readonly IPasswordService _passwordService;
    private readonly IConfiguration _configuration;

    private const int RefreshTokenExpiryDays = 7;
    private const int ResetTokenExpiryMinutes = 30;

    public AuthController(
        CashLoanDbContext context,
        ITokenService tokenService,
        IPasswordService passwordService,
        IConfiguration configuration)
    {
        _context = context;
        _tokenService = tokenService;
        _passwordService = passwordService;
        _configuration = configuration;
    }

    // ─── POST /api/auth/login ─────────────────────────────────────────────────
    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login([FromBody] LoginRequestDto request)
    {
        var user = await _context.Users
            .Include(u => u.Role)
            .FirstOrDefaultAsync(u => u.Username == request.Username && u.IsActive);

        if (user == null || !_passwordService.VerifyPassword(request.Password, user.PasswordHash))
        {
            await LogAuditAsync(null, $"Failed login attempt for username: '{request.Username}'.");
            return Unauthorized(new { message = "Invalid username or password." });
        }

        var accessToken = _tokenService.GenerateToken(user.Id, user.Username, user.FullName, user.Role.Name);
        var refreshToken = _tokenService.GenerateRefreshToken();

        user.RefreshToken = refreshToken;
        user.RefreshTokenExpiry = DateTime.UtcNow.AddDays(RefreshTokenExpiryDays);
        user.UpdatedAt = DateTime.UtcNow;

        var passwordExpired = user.PasswordChangedAt.HasValue &&
                              (DateTime.UtcNow - user.PasswordChangedAt.Value).TotalDays > 30;

        await LogAuditAsync(user.Id, $"User '{user.Username}' logged in successfully.");
        await _context.SaveChangesAsync();

        var expiryMinutes = _configuration.GetValue<int>("JwtSettings:ExpiryMinutes");

        return Ok(new LoginResponseDto
        {
            Token = accessToken,
            RefreshToken = refreshToken,
            Username = user.Username,
            FullName = user.FullName,
            Role = user.Role.Name,
            ExpiresAt = DateTime.UtcNow.AddMinutes(expiryMinutes > 0 ? expiryMinutes : 60),
            MustChangePassword = user.MustChangePassword,
            PasswordExpired = passwordExpired
        });
    }

    // ─── POST /api/auth/refresh-token ─────────────────────────────────────────
    [HttpPost("refresh-token")]
    [AllowAnonymous]
    public async Task<IActionResult> RefreshToken([FromBody] RefreshTokenRequestDto request)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken))
            return BadRequest(new { message = "Refresh token is required." });

        var user = await _context.Users
            .Include(u => u.Role)
            .FirstOrDefaultAsync(u =>
                u.RefreshToken == request.RefreshToken &&
                u.IsActive);

        if (user == null)
            return Unauthorized(new { message = "Invalid refresh token." });

        if (user.RefreshTokenExpiry == null || user.RefreshTokenExpiry < DateTime.UtcNow)
        {
            // Clear expired token
            user.RefreshToken = null;
            user.RefreshTokenExpiry = null;
            await _context.SaveChangesAsync();
            return Unauthorized(new { message = "Refresh token has expired. Please log in again." });
        }

        // Rotate: issue new access + refresh token
        var newAccessToken = _tokenService.GenerateToken(user.Id, user.Username, user.FullName, user.Role.Name);
        var newRefreshToken = _tokenService.GenerateRefreshToken();

        user.RefreshToken = newRefreshToken;
        user.RefreshTokenExpiry = DateTime.UtcNow.AddDays(RefreshTokenExpiryDays);
        user.UpdatedAt = DateTime.UtcNow;

        await LogAuditAsync(user.Id, "Access token refreshed.");
        await _context.SaveChangesAsync();

        var expiryMinutes = _configuration.GetValue<int>("JwtSettings:ExpiryMinutes");

        return Ok(new LoginResponseDto
        {
            Token = newAccessToken,
            RefreshToken = newRefreshToken,
            Username = user.Username,
            FullName = user.FullName,
            Role = user.Role.Name,
            ExpiresAt = DateTime.UtcNow.AddMinutes(expiryMinutes > 0 ? expiryMinutes : 60)
        });
    }

    // ─── POST /api/auth/logout ────────────────────────────────────────────────
    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout()
    {
        var userId = GetCurrentUserId();
        var user = await _context.Users.FindAsync(userId);

        if (user != null)
        {
            user.RefreshToken = null;
            user.RefreshTokenExpiry = null;
            user.UpdatedAt = DateTime.UtcNow;
        }

        await LogAuditAsync(userId, "User logged out.");
        await _context.SaveChangesAsync();

        return Ok(new { message = "Logged out successfully." });
    }

    // ─── GET /api/auth/me ─────────────────────────────────────────────────────
    [HttpGet("me")]
    [Authorize]
    public async Task<IActionResult> GetCurrentUser()
    {
        var userId = GetCurrentUserId();
        var user = await _context.Users
            .Include(u => u.Role)
            .FirstOrDefaultAsync(u => u.Id == userId);

        if (user == null)
            return NotFound(new { message = "User not found." });

        return Ok(new
        {
            user.Id,
            user.Username,
            user.FullName,
            user.Email,
            user.Phone,
            Role = user.Role.Name
        });
    }

    // ─── POST /api/auth/change-password ──────────────────────────────────────
    [HttpPost("change-password")]
    [Authorize]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordDto request)
    {
        if (string.IsNullOrWhiteSpace(request.NewPassword) || request.NewPassword.Length < 8)
            return BadRequest(new { message = "New password must be at least 8 characters." });

        var userId = GetCurrentUserId();
        var user = await _context.Users.FindAsync(userId);

        if (user == null)
            return NotFound(new { message = "User not found." });

        if (!_passwordService.VerifyPassword(request.CurrentPassword, user.PasswordHash))
            return BadRequest(new { message = "Current password is incorrect." });

        user.PasswordHash = _passwordService.HashPassword(request.NewPassword);
        user.MustChangePassword = false;
        user.PasswordChangedAt = DateTime.UtcNow;
        user.RefreshToken = null;
        user.RefreshTokenExpiry = null;
        user.UpdatedAt = DateTime.UtcNow;

        await LogAuditAsync(userId, $"User '{user.Username}' changed their password.");
        await _context.SaveChangesAsync();

        return Ok(new { message = "Password changed successfully. Please log in again." });
    }

    // ─── POST /api/auth/forgot-password ──────────────────────────────────────
    [HttpPost("forgot-password")]
    [AllowAnonymous]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequestDto request)
    {
        var user = await _context.Users
            .FirstOrDefaultAsync(u => u.Username == request.Username && u.IsActive);

        // Always return 200 to prevent username enumeration
        if (user == null)
            return Ok(new { message = "If the account exists, a reset token has been generated." });

        var resetToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

        user.PasswordResetToken = resetToken;
        user.PasswordResetTokenExpiry = DateTime.UtcNow.AddMinutes(ResetTokenExpiryMinutes);
        user.UpdatedAt = DateTime.UtcNow;

        await LogAuditAsync(user.Id, $"Password reset token generated for '{user.Username}'.");
        await _context.SaveChangesAsync();

        // In production this token would be emailed. We return it directly for now.
        return Ok(new
        {
            message = "Password reset token generated. In production this would be sent via email.",
            resetToken,                                          // remove once email is wired up
            expiresAt = user.PasswordResetTokenExpiry
        });
    }

    // ─── POST /api/auth/reset-password ───────────────────────────────────────
    [HttpPost("reset-password")]
    [AllowAnonymous]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequestDto request)
    {
        if (string.IsNullOrWhiteSpace(request.Token) || string.IsNullOrWhiteSpace(request.NewPassword))
            return BadRequest(new { message = "Token and new password are required." });

        if (request.NewPassword.Length < 8)
            return BadRequest(new { message = "Password must be at least 8 characters." });

        var user = await _context.Users
            .FirstOrDefaultAsync(u => u.PasswordResetToken == request.Token && u.IsActive);

        if (user == null)
            return BadRequest(new { message = "Invalid reset token." });

        if (user.PasswordResetTokenExpiry == null || user.PasswordResetTokenExpiry < DateTime.UtcNow)
        {
            user.PasswordResetToken = null;
            user.PasswordResetTokenExpiry = null;
            await _context.SaveChangesAsync();
            return BadRequest(new { message = "Reset token has expired. Please request a new one." });
        }

        user.PasswordHash = _passwordService.HashPassword(request.NewPassword);
        user.PasswordResetToken = null;
        user.PasswordResetTokenExpiry = null;
        user.RefreshToken = null;           // invalidate any active sessions
        user.RefreshTokenExpiry = null;
        user.UpdatedAt = DateTime.UtcNow;

        await LogAuditAsync(user.Id, $"Password reset successfully for '{user.Username}'.");
        await _context.SaveChangesAsync();

        return Ok(new { message = "Password reset successfully. Please log in with your new password." });
    }

    // ─── Helper ──────────────────────────────────────────────────────────────
    private async Task LogAuditAsync(int? userId, string action)
    {
        _context.AuditLogs.Add(new AuditLog
        {
            Action = action,
            IpAddress = GetClientIp(),
            DeviceInfo = GetDeviceInfo(),
            Timestamp = DateTime.UtcNow,
            UserId = userId,
            CreatedAt = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();
    }
}
