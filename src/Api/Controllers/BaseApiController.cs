using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;

namespace CashLoanManagement.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public abstract class BaseApiController : ControllerBase
{
    protected int GetCurrentUserId()
    {
        var value = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (int.TryParse(value, out var id))
            return id;
        throw new UnauthorizedAccessException("Unable to resolve authenticated user ID.");
    }

    protected string GetClientIp() =>
        HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    protected string GetDeviceInfo() =>
        Request.Headers.UserAgent.ToString();
}
