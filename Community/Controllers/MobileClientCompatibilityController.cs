using Microsoft.AspNetCore.Mvc;
using TodoSuite.Server.Services;

namespace TodoSuite.Server.Controllers;

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
