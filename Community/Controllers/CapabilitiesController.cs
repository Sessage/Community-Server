using Klassenbibliothek.Features;
using Klassenbibliothek.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace TodoSuite.Server.Controllers;

[ApiController]
[Route("api/capabilities")]
[Authorize(Policy = "MobileApi")]
public sealed class CapabilitiesController(
    IProductFeatureCatalog featureCatalog,
    IDirectorySharingService directorySharing) : ControllerBase
{
    [HttpGet]
    public ActionResult<ProductCapabilitiesDocument> GetCapabilities()
    {
        var capabilities = featureCatalog.GetCapabilities();
        if (directorySharing.IsAvailable
            || !capabilities.Capabilities.Contains(ProductFeatureIds.IdentityGovernance, StringComparer.Ordinal))
            return Ok(capabilities);

        // The licensed feature still requires a configured directory connection.
        // Mobile clients consume this document as runtime capabilities and must not
        // offer a directory tab that the connected server cannot service.
        return Ok(capabilities with
        {
            Capabilities = capabilities.Capabilities
                .Where(capability => !string.Equals(
                    capability, ProductFeatureIds.IdentityGovernance, StringComparison.Ordinal))
                .ToArray()
        });
    }
}
