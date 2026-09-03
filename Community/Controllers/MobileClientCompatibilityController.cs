using Microsoft.AspNetCore.Mvc;
using TodoSuite.Server.Services;

namespace TodoSuite.Server.Controllers;

/// <summary>
/// Publishes the minimum and recommended mobile client versions before authentication.
/// The response contains no tenant data and lets outdated clients fail with an actionable update message.
/// </summary>
[ApiController]
[Route("api/mobile/client-compatibility")]
public sealed class MobileClientCompatibilityController(ClientCompatibilityService compatibility) : ControllerBase
{
    [HttpGet]
    public ActionResult<ClientCompatibilityResult> Get()
    {
        var version = Request.Headers["X-Sessage-App-Version"].FirstOrDefault();
        return Ok(compatibility.Check(version));
    }
}
