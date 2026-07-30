using Klassenbibliothek.Features;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace TodoSuite.Server.Controllers;

[ApiController]
[Route("api/capabilities")]
[Authorize(Policy = "MobileApi")]
public sealed class CapabilitiesController(IProductFeatureCatalog featureCatalog) : ControllerBase
{
    [HttpGet]
    public ActionResult<ProductCapabilitiesDocument> GetCapabilities()
        => Ok(featureCatalog.GetCapabilities());
}
