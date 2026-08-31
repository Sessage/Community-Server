using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Klassenbibliothek.Data;
using TodoSuite.Server.Services;

namespace TodoSuite.Server.Controllers;

[ApiController]
[Route("api/tokens")]
[Authorize(Policy = "MobileApi")]
public class PersonalAccessTokenController(
    UserManager<ApplicationUser> userManager,
    PersonalAccessTokenService tokenService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<TokenListItem>>> ListTokens(CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var tokens = await tokenService.ListAsync(userId, cancellationToken);

        return Ok(tokens.Select(token => new TokenListItem(
            token.Id,
            token.Name,
            token.ReadOnly,
            token.CreatedAtUtc,
            token.ExpiresAtUtc,
            token.LastUsedAtUtc)));
    }

    [HttpPost]
    public async Task<ActionResult<CreateTokenResponse>> CreateToken(
        [FromBody] CreateTokenRequest request,
        CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return Unauthorized();

        try
        {
            var created = await tokenService.CreateAsync(
                userId,
                request.Name,
                request.ReadOnly,
                cancellationToken);
            return Ok(new CreateTokenResponse(
                created.Id,
                created.Name,
                created.ReadOnly,
                created.Token,
                created.CreatedAtUtc,
                created.ExpiresAtUtc));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (PersonalAccessTokenLimitExceededException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteToken(Guid id, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        return await tokenService.DeleteAsync(userId, id, cancellationToken)
            ? NoContent()
            : NotFound();
    }

    private string? GetUserId() =>
        User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");

    public record CreateTokenRequest(string Name, bool ReadOnly = false);
    public record TokenListItem(Guid Id, string Name, bool ReadOnly, DateTime CreatedAtUtc, DateTime ExpiresAtUtc, DateTime? LastUsedAtUtc);
    public record CreateTokenResponse(Guid Id, string Name, bool ReadOnly, string Token, DateTime CreatedAtUtc, DateTime ExpiresAtUtc);
}
