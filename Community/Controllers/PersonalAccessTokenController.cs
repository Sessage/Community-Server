using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Klassenbibliothek.Data;
using TodoSuite.Server.Auth;

namespace TodoSuite.Server.Controllers;

[ApiController]
[Route("api/tokens")]
[Authorize(Policy = "MobileApi")]
public class PersonalAccessTokenController(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    UserManager<ApplicationUser> userManager,
    IConfiguration configuration) : ControllerBase
{
    private const string TokenPrefix = "tsa_";

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<TokenListItem>>> ListTokens()
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        await using var db = await dbFactory.CreateDbContextAsync();
        var tokens = await db.PersonalAccessTokens
            .Where(t => t.UserId == userId)
            .OrderByDescending(t => t.CreatedAtUtc)
            .Select(t => new TokenListItem(t.Id, t.Name, !t.AllowWrite, t.CreatedAtUtc, t.ExpiresAtUtc, t.LastUsedAtUtc))
            .ToListAsync();

        return Ok(tokens);
    }

    [HttpPost]
    public async Task<ActionResult<CreateTokenResponse>> CreateToken([FromBody] CreateTokenRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest("Name darf nicht leer sein.");

        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return Unauthorized();

        // Zufälligen Token generieren: tsa_<32 Bytes als Base64Url>
        var randomBytes = RandomNumberGenerator.GetBytes(32);
        var rawToken = TokenPrefix + Convert.ToBase64String(randomBytes)
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

        var hash = PersonalAccessTokenAuthHandler.HashToken(rawToken);

        var configuredLifetime = configuration.GetValue("PersonalAccessTokens:LifetimeDays", 90);
        var lifetimeDays = Math.Clamp(configuredLifetime, 1, 365);
        var createdAtUtc = DateTime.UtcNow;
        var entity = new PersonalAccessTokenEntity
        {
            UserId = userId,
            Name = request.Name.Trim(),
            TokenHash = hash,
            AllowWrite = !request.ReadOnly,
            CreatedAtUtc = createdAtUtc,
            ExpiresAtUtc = createdAtUtc.AddDays(lifetimeDays)
        };

        await using var db = await dbFactory.CreateDbContextAsync();
        db.PersonalAccessTokens.Add(entity);
        await db.SaveChangesAsync();

        return Ok(new CreateTokenResponse(entity.Id, entity.Name, !entity.AllowWrite, rawToken, entity.CreatedAtUtc, entity.ExpiresAtUtc));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteToken(Guid id)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        await using var db = await dbFactory.CreateDbContextAsync();
        var token = await db.PersonalAccessTokens
            .FirstOrDefaultAsync(t => t.Id == id && t.UserId == userId);

        if (token is null) return NotFound();

        db.PersonalAccessTokens.Remove(token);
        await db.SaveChangesAsync();

        return NoContent();
    }

    private string? GetUserId() =>
        User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");

    public record CreateTokenRequest(string Name, bool ReadOnly = false);
    public record TokenListItem(Guid Id, string Name, bool ReadOnly, DateTime CreatedAtUtc, DateTime ExpiresAtUtc, DateTime? LastUsedAtUtc);
    public record CreateTokenResponse(Guid Id, string Name, bool ReadOnly, string Token, DateTime CreatedAtUtc, DateTime ExpiresAtUtc);
}
