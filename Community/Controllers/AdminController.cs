using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Klassenbibliothek.Data;
using Klassenbibliothek.Localization;
using Klassenbibliothek.Administration;
using TodoSuite.Server.Services;

namespace TodoSuite.Server.Controllers;

/// <summary>
/// Exposes the Community administration API for managing local users and their roles.
/// Every action requires an administrator identity; user lifecycle work is delegated to
/// ASP.NET Core Identity so password policy, normalization, and security stamps stay consistent.
/// </summary>
[ApiController]
[Route("api/mobile/admin")]
[Authorize(Policy = "MobileApiAdmin")]
public class AdminController : ControllerBase
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IStringLocalizer<SharedResource> _localizer;
    private readonly IAuditEventSink _audit;
    private readonly UserAccountArtifactCleanupService _accountArtifactCleanup;
    private readonly ILogger<AdminController> _logger;

    public AdminController(
        UserManager<ApplicationUser> userManager,
        IStringLocalizer<SharedResource> localizer,
        IAuditEventSink audit,
        UserAccountArtifactCleanupService accountArtifactCleanup,
        ILogger<AdminController> logger)
    {
        _userManager = userManager;
        _localizer = localizer;
        _audit = audit;
        _accountArtifactCleanup = accountArtifactCleanup;
        _logger = logger;
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
            // Escape LIKE metacharacters before adding the surrounding wildcard so user input
            // cannot broaden the intended literal substring search.
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
        var email = request.Email?.Trim();
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(request.Password))
            return BadRequest(new { Error = _localizer["Err_Admin_EmailAndPasswordRequired"].Value });

        var existing = await _userManager.FindByEmailAsync(email);
        if (existing is not null)
            return Conflict(new { Error = _localizer["Err_Admin_UserAlreadyExists"].Value });

        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true
        };

        var result = await _userManager.CreateAsync(user, request.Password);
        if (!result.Succeeded)
            return BadRequest(new { Errors = result.Errors.Select(e => e.Description).ToArray() });

        if (request.IsAdmin)
        {
            var roleResult = await _userManager.AddToRoleAsync(user, "Admin");
            if (!roleResult.Succeeded)
            {
                // Creating a requested administrator as a normal user would be a
                // misleading partial success. Roll the account back instead.
                await _userManager.DeleteAsync(user);
                return BadRequest(new { Error = IdentityErrors(roleResult) });
            }
        }

        await TryRecordAuditAsync("user-created", $"userId={user.Id}; isAdmin={request.IsAdmin}");

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
        // Prevent an administrator from removing the permission needed to restore their own role.
        if (!request.IsAdmin && isCurrentlyAdmin && user.Id == currentCallerId)
            return BadRequest(new { Error = _localizer["Err_Admin_CannotRemoveOwnAdminRole"].Value });

        IdentityResult? roleResult = null;
        if (request.IsAdmin && !isCurrentlyAdmin)
            roleResult = await _userManager.AddToRoleAsync(user, "Admin");
        else if (!request.IsAdmin && isCurrentlyAdmin)
            roleResult = await _userManager.RemoveFromRoleAsync(user, "Admin");

        if (roleResult is { Succeeded: false })
            return BadRequest(new { Error = IdentityErrors(roleResult) });

        var isAdmin = await _userManager.IsInRoleAsync(user, "Admin");
        await TryRecordAuditAsync("role-updated", $"userId={user.Id}; isAdmin={isAdmin}");
        return Ok(new AdminUserDto(user.Id, user.Email ?? string.Empty, user.UserName ?? string.Empty, isAdmin));
    }

    [HttpDelete("users/{userId}")]
    public async Task<IActionResult> DeleteUser(string userId)
    {
        var currentCallerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        // Self-deletion follows the account-owned flow, which requires password verification and
        // avoids accidentally removing the administrator currently performing maintenance.
        if (userId == currentCallerId)
            return BadRequest(new { Error = _localizer["Err_Admin_CannotDeleteOwnAccount"].Value });

        var user = await _userManager.FindByIdAsync(userId);
        if (user is null)
            return NotFound();

        var profilePicturePath = user.ProfilePicturePath;
        var result = await _userManager.DeleteAsync(user);
        if (!result.Succeeded)
            return BadRequest(new { Errors = result.Errors.Select(e => e.Description).ToArray() });

        await _accountArtifactCleanup.CleanupAfterUserDeletionAsync(
            userId,
            profilePicturePath,
            CancellationToken.None);
        await TryRecordAuditAsync("user-deleted", $"userId={userId}");

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

    private static string IdentityErrors(IdentityResult result)
        => string.Join(" ", result.Errors.Select(error => error.Description));

    private async Task TryRecordAuditAsync(string action, string details)
    {
        try
        {
            await _audit.RecordAsync(
                "users",
                action,
                User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty,
                details,
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            // The identity mutation has already completed at this point. Logging the
            // audit failure avoids returning a misleading error that invites retries.
            _logger.LogError(exception, "Audit-Eintrag {AuditAction} konnte nicht gespeichert werden.", action);
        }
    }
}
