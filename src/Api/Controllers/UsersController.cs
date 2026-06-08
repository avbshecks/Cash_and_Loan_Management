using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using CashLoanManagement.Application.Common.Interfaces;
using CashLoanManagement.Application.DTOs.Auth;
using CashLoanManagement.Domain.Entities;
using CashLoanManagement.Infrastructure.Persistence;

namespace CashLoanManagement.Api.Controllers;

[Authorize(Roles = "Admin")]
public class UsersController : BaseApiController
{
    private readonly CashLoanDbContext _context;
    private readonly IPasswordService _passwordService;

    public UsersController(CashLoanDbContext context, IPasswordService passwordService)
    {
        _context = context;
        _passwordService = passwordService;
    }

    // ─── GET /api/users ───────────────────────────────────────────────────────
    [HttpGet]
    public async Task<IActionResult> GetUsers()
    {
        var users = await _context.Users
            .Include(u => u.Role)
            .OrderBy(u => u.FullName)
            .Select(u => new
            {
                u.Id,
                u.Username,
                u.FullName,
                u.Email,
                u.Phone,
                u.IsActive,
                u.MustChangePassword,
                u.PasswordChangedAt,
                Role = new { u.Role.Id, u.Role.Name },
                PasswordExpired = u.PasswordChangedAt.HasValue &&
                                  (DateTime.UtcNow - u.PasswordChangedAt.Value).TotalDays > 30
            })
            .ToListAsync();

        return Ok(users);
    }

    // ─── GET /api/users/roles ─────────────────────────────────────────────────
    [HttpGet("roles")]
    public async Task<IActionResult> GetRoles()
    {
        var roles = await _context.Roles
            .Select(r => new { r.Id, r.Name, r.Description })
            .ToListAsync();
        return Ok(roles);
    }

    // ─── POST /api/users ──────────────────────────────────────────────────────
    [HttpPost]
    public async Task<IActionResult> CreateUser([FromBody] CreateUserDto request)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.DefaultPassword))
            return BadRequest(new { message = "Username and default password are required." });

        if (request.DefaultPassword.Length < 6)
            return BadRequest(new { message = "Default password must be at least 6 characters." });

        var exists = await _context.Users.AnyAsync(u => u.Username == request.Username);
        if (exists)
            return BadRequest(new { message = $"Username '{request.Username}' is already taken." });

        var roleExists = await _context.Roles.AnyAsync(r => r.Id == request.RoleId);
        if (!roleExists)
            return BadRequest(new { message = "Invalid role selected." });

        var user = new User
        {
            Username = request.Username,
            FullName = request.FullName,
            Email = request.Email,
            Phone = request.Phone,
            RoleId = request.RoleId,
            PasswordHash = _passwordService.HashPassword(request.DefaultPassword),
            IsActive = true,
            MustChangePassword = true,          // force change on first login
            PasswordChangedAt = null,           // null = never changed
            CreatedAt = DateTime.UtcNow
        };

        _context.Users.Add(user);

        _context.AuditLogs.Add(new AuditLog
        {
            Action = $"Admin created user '{request.Username}' with role ID {request.RoleId}.",
            IpAddress = GetClientIp(),
            DeviceInfo = GetDeviceInfo(),
            Timestamp = DateTime.UtcNow,
            UserId = GetCurrentUserId(),
            CreatedAt = DateTime.UtcNow
        });

        await _context.SaveChangesAsync();

        return Ok(new
        {
            message = $"User '{request.Username}' created. They must change their password on first login.",
            userId = user.Id,
            username = user.Username
        });
    }

    // ─── PUT /api/users/{id} ──────────────────────────────────────────────────
    [HttpPut("{id:int}")]
    public async Task<IActionResult> UpdateUser(int id, [FromBody] UpdateUserDto request)
    {
        var user = await _context.Users.FindAsync(id);
        if (user == null)
            return NotFound(new { message = "User not found." });

        if (request.RoleId.HasValue)
        {
            var roleExists = await _context.Roles.AnyAsync(r => r.Id == request.RoleId.Value);
            if (!roleExists)
                return BadRequest(new { message = "Invalid role." });
            user.RoleId = request.RoleId.Value;
        }

        if (!string.IsNullOrWhiteSpace(request.FullName))   user.FullName = request.FullName;
        if (!string.IsNullOrWhiteSpace(request.Email))      user.Email = request.Email;
        if (!string.IsNullOrWhiteSpace(request.Phone))      user.Phone = request.Phone;
        if (request.IsActive.HasValue)                      user.IsActive = request.IsActive.Value;

        user.UpdatedAt = DateTime.UtcNow;

        _context.AuditLogs.Add(new AuditLog
        {
            Action = $"Admin updated user '{user.Username}'.",
            IpAddress = GetClientIp(),
            DeviceInfo = GetDeviceInfo(),
            Timestamp = DateTime.UtcNow,
            UserId = GetCurrentUserId(),
            CreatedAt = DateTime.UtcNow
        });

        await _context.SaveChangesAsync();
        return Ok(new { message = "User updated successfully." });
    }

    // ─── POST /api/users/{id}/reset-password ──────────────────────────────────
    [HttpPost("{id:int}/reset-password")]
    public async Task<IActionResult> AdminResetPassword(int id, [FromBody] AdminResetPasswordDto request)
    {
        if (string.IsNullOrWhiteSpace(request.NewPassword) || request.NewPassword.Length < 6)
            return BadRequest(new { message = "New password must be at least 6 characters." });

        var user = await _context.Users.FindAsync(id);
        if (user == null)
            return NotFound(new { message = "User not found." });

        user.PasswordHash = _passwordService.HashPassword(request.NewPassword);
        user.MustChangePassword = true;     // force change on next login
        user.PasswordChangedAt = null;      // reset expiry clock
        user.RefreshToken = null;
        user.RefreshTokenExpiry = null;
        user.UpdatedAt = DateTime.UtcNow;

        _context.AuditLogs.Add(new AuditLog
        {
            Action = $"Admin reset password for user '{user.Username}'. Must change on next login.",
            IpAddress = GetClientIp(),
            DeviceInfo = GetDeviceInfo(),
            Timestamp = DateTime.UtcNow,
            UserId = GetCurrentUserId(),
            CreatedAt = DateTime.UtcNow
        });

        await _context.SaveChangesAsync();
        return Ok(new { message = $"Password reset for '{user.Username}'. They must change it on next login." });
    }

    // ─── DELETE /api/users/{id} (deactivate) ─────────────────────────────────
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> DeactivateUser(int id)
    {
        var currentUserId = GetCurrentUserId();
        if (id == currentUserId)
            return BadRequest(new { message = "You cannot deactivate your own account." });

        var user = await _context.Users.FindAsync(id);
        if (user == null)
            return NotFound(new { message = "User not found." });

        user.IsActive = false;
        user.RefreshToken = null;
        user.RefreshTokenExpiry = null;
        user.UpdatedAt = DateTime.UtcNow;

        _context.AuditLogs.Add(new AuditLog
        {
            Action = $"Admin deactivated user '{user.Username}'.",
            IpAddress = GetClientIp(),
            DeviceInfo = GetDeviceInfo(),
            Timestamp = DateTime.UtcNow,
            UserId = currentUserId,
            CreatedAt = DateTime.UtcNow
        });

        await _context.SaveChangesAsync();
        return Ok(new { message = $"User '{user.Username}' deactivated." });
    }
}

// ─── Additional DTOs ──────────────────────────────────────────────────────────
public class UpdateUserDto
{
    public string? FullName { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public int? RoleId { get; set; }
    public bool? IsActive { get; set; }
}

public class AdminResetPasswordDto
{
    public string NewPassword { get; set; } = string.Empty;
}
