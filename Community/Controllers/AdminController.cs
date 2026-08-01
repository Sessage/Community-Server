using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Klassenbibliothek.Data;
using Klassenbibliothek.Localization;
using Klassenbibliothek.Administration;

namespace TodoSuite.Server.Controllers;

[ApiController]
[Route("api/mobile/admin")]
[Authorize(Policy = "MobileApiAdmin")]
public class AdminController : ControllerBase
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IStringLocalizer<SharedResource> _localizer;
    private readonly IAuditEventSink _audit;

    public AdminController(UserManager<ApplicationUser> userManager, IStringLocalizer<SharedResource> localizer, IAuditEventSink audit)
    {
        _userManager = userManager;
        _localizer = localizer;
        _audit = audit;
    }

    [HttpGet("users")]
    public async Task<ActionResult<IReadOnlyList<AdminUserDto>>> GetUsers(
        [FromQuery] string? search = null,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 100)
    {
        var normalizedSearch = (search ?? "").Trim();
        skip = Math.Max(0, skip);
        take = Math.Clamp(take, 1, 500);

        var query = _userManager.Users.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(normalizedSearch))
        {
            query = query.Where(u =>
                (u.Email != null && EF.Functions.ILike(u.Email, $"%{EscapeLikePattern(normalizedSearch)}%", "\\"))
                || (u.UserName != null && EF.Functions.ILike(u.UserName, $"%{EscapeLikePattern(normalizedSearch)}%", "\\")));
        }

        var users = await query
            .OrderBy(u => u.Email)
            .ThenBy(u => u.UserName)
            .Skip(skip)
            .Take(take)
            .ToListAsync();

        var result = new List<AdminUserDto>();

        foreach (var u in users)
        {
            var isAdmin = await _userManager.IsInRoleAsync(u, "Admin");
            result.Add(new AdminUserDto(u.Id, u.Email ?? string.Empty, u.UserName ?? string.Empty, isAdmin));
        }

        return Ok(result);
    }

    [HttpPost("users")]
    public async Task<ActionResult<AdminUserDto>> CreateUser([FromBody] CreateUserRequest request)
    {
        var existing = await _userManager.FindByEmailAsync(request.Email);
        if (existing is not null)
            return Conflict(new { Error = _localizer["Err_Admin_UserAlreadyExists"].Value });

        var user = new ApplicationUser
        {
            UserName = request.Email,
            Email = request.Email,
            EmailConfirmed = true
        };

        var result = await _userManager.CreateAsync(user, request.Password);
        if (!result.Succeeded)
            return BadRequest(new { Errors = result.Errors.Select(e => e.Description).ToArray() });

        if (request.IsAdmin)
            await _userManager.AddToRoleAsync(user, "Admin");

        await _audit.RecordAsync("users", "user-created", User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty,
            $"userId={user.Id}; isAdmin={request.IsAdmin}", HttpContext.RequestAborted);

        return Ok(new AdminUserDto(user.Id, user.Email!, user.UserName!, request.IsAdmin));
    }

    [HttpPut("users/{userId}")]
    public async Task<ActionResult<AdminUserDto>> UpdateUser(string userId, [FromBody] UpdateUserRequest request)
    {
        var user = await _userManager.FindByIdAsync(userId);
        if (user is null)
            return NotFound();

        var currentCallerId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        var isCurrentlyAdmin = await _userManager.IsInRoleAsync(user, "Admin");
        if (request.IsAdmin && !isCurrentlyAdmin)
            await _userManager.AddToRoleAsync(user, "Admin");
        else if (!request.IsAdmin && isCurrentlyAdmin && user.Id != currentCallerId)
            await _userManager.RemoveFromRoleAsync(user, "Admin");

        var isAdmin = await _userManager.IsInRoleAsync(user, "Admin");
        await _audit.RecordAsync("users", "role-updated", currentCallerId ?? string.Empty,
            $"userId={user.Id}; isAdmin={isAdmin}", HttpContext.RequestAborted);
        return Ok(new AdminUserDto(user.Id, user.Email ?? string.Empty, user.UserName ?? string.Empty, isAdmin));
    }

    [HttpDelete("users/{userId}")]
    public async Task<IActionResult> DeleteUser(string userId)
    {
        var currentCallerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == currentCallerId)
            return BadRequest(new { Error = _localizer["Err_Admin_CannotDeleteOwnAccount"].Value });

        var user = await _userManager.FindByIdAsync(userId);
        if (user is null)
            return NotFound();

        var result = await _userManager.DeleteAsync(user);
        if (!result.Succeeded)
            return BadRequest(new { Errors = result.Errors.Select(e => e.Description).ToArray() });

        await _audit.RecordAsync("users", "user-deleted", currentCallerId ?? string.Empty,
            $"userId={userId}", HttpContext.RequestAborted);

        return NoContent();
    }

    public record AdminUserDto(string Id, string Email, string UserName, bool IsAdmin);
    public record CreateUserRequest(string Email, string Password, bool IsAdmin);
    public record UpdateUserRequest(bool IsAdmin);

    private static string EscapeLikePattern(string value)
        => value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
}
