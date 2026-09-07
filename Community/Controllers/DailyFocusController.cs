using System.Security.Claims;
using Klassenbibliothek.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace TodoSuite.Server.Controllers;

[ApiController]
[Route("api/my-day")]
[Authorize(Policy = "MobileApi")]
public sealed class DailyFocusController(IDailyFocusService focus) : ControllerBase
{
    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")
        ?? throw new UnauthorizedAccessException();

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] string timeZoneId = "UTC", CancellationToken ct = default)
    {
        try { return Ok(await focus.GetAsync(UserId, timeZoneId, ct)); }
        catch (TimeZoneNotFoundException) { return BadRequest("Unknown timezone."); }
        catch (InvalidTimeZoneException) { return BadRequest("Invalid timezone."); }
    }

    [HttpPut("tasks")]
    public async Task<IActionResult> SetMany(DailyFocusBatchChange change, CancellationToken ct)
    {
        try { return Ok(await focus.SetManyAsync(UserId, change, ct)); }
        catch (ArgumentException) { return BadRequest("Select between 1 and 500 valid tasks."); }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (InvalidOperationException) { return Conflict("Load My Day first."); }
    }

    [HttpPut("tasks/{taskId:guid}")]
    public async Task<IActionResult> Set(Guid taskId, DailyFocusChange change, CancellationToken ct)
    {
        try { return Ok(await focus.SetAsync(UserId, taskId, change, ct)); }
        catch (ArgumentException) { return BadRequest("Invalid task ID."); }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (InvalidOperationException) { return Conflict("Load My Day first."); }
    }
}
