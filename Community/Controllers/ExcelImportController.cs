using Klassenbibliothek.Localization;
using System.Security.Claims;
using Klassenbibliothek.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace TodoSuite.Server.Controllers;

[ApiController]
[Authorize(Policy = "MobileApi")]
[Route("api/mobile/lists/{listId:guid}/excel-import")]
[RequestSizeLimit(30 * 1024 * 1024)]
public sealed class ExcelImportController(IExcelTaskImportService imports) : ControllerBase
{
    [HttpPost("validate")]
    public Task<IActionResult> Validate(Guid listId, [FromBody] ExcelImportRequest request)
        => Execute(listId, request, false);

    [HttpPost]
    public Task<IActionResult> Import(Guid listId, [FromBody] ExcelImportRequest request)
        => Execute(listId, request, true);

    private async Task<IActionResult> Execute(Guid listId, ExcelImportRequest request, bool persist)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();
        if (request.Sheet is null || request.Mappings is null || request.ImportId == Guid.Empty)
            return BadRequest(new { error = SharedText.Get("ExcelImport_IncompleteRequest") });
        try
        {
            if (persist)
                return Ok(await imports.ImportExcelAsync(userId, listId, request.Sheet, request.Date1904, request.Mappings, request.ImportId));
            return Ok(await imports.ValidateExcelImportAsync(userId, listId, request.Sheet, request.Date1904, request.Mappings));
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        catch (WorkspaceConcurrencyException ex) { return Conflict(new { error = ex.Message }); }
    }
}
