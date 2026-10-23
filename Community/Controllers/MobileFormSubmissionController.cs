using Microsoft.AspNetCore.Http;
using System.Security.Claims;
using System.Text.Json;
using Klassenbibliothek.Data;
using Klassenbibliothek.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace TodoSuite.Server.Controllers;

/// <summary>Submission transport for signed-in app users, including public forms outside their lists.
/// Publication, password, private-list access, file policy and idempotency remain server-owned.</summary>
[ApiController]
[Route("api/mobile/forms")]
[Authorize(Policy = "MobileSession")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class MobileFormSubmissionController(ITodoFormService formService) : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const long MaxAttachmentSizeBytes = 25L * 1024 * 1024;
    private string ResolveUserId() => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")
        ?? throw new UnauthorizedAccessException();
    [HttpGet("public/{slug}")]
    public async Task<ActionResult<TodoFormEntity>> GetFormForSubmission(string slug, CancellationToken token)
    {
        Response.Headers.CacheControl = "no-store";
        var form = await formService.GetFormForSubmissionAsync(slug, ResolveUserId(), token);
        return form is null ? NotFound() : Ok(form.PublicationStatus == TodoFormPublicationStatus.PasswordProtected
            ? TodoFormSubmissionProjection.Locked(form) : TodoFormSubmissionProjection.Create(form));
    }

    [HttpPost("public/{slug}/unlock")]
    public async Task<ActionResult<TodoFormEntity>> UnlockForm(string slug, TodoFormPasswordRequest request, CancellationToken token)
    {
        Response.Headers.CacheControl = "no-store";
        var form = await formService.GetVerifiedFormForSubmissionAsync(slug, ResolveUserId(), request.Password,
            HttpContext.Connection.RemoteIpAddress?.ToString(), token);
        return form is null ? NotFound() : Ok(TodoFormSubmissionProjection.Create(form));
    }

    [HttpPost("{formId:guid}/verify-password")]
    public async Task<ActionResult<bool>> VerifyFormPassword(Guid formId, TodoFormPasswordRequest request, CancellationToken token)
        => Ok(await formService.VerifySubmissionPasswordAsync(formId, ResolveUserId(), request.Password,
            HttpContext.Connection.RemoteIpAddress?.ToString(), token));

    [HttpPost("submit")]
    [RequestSizeLimit(260 * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 260 * 1024 * 1024, ValueLengthLimit = 1024 * 1024)]
    public async Task<ActionResult<TodoFormSubmitResult>> SubmitForm([FromForm] string payload,
        [FromForm] List<IFormFile>? files, CancellationToken token)
    {
        files ??= [];
        TodoFormSubmissionPayload? request;
        try { request = JsonSerializer.Deserialize<TodoFormSubmissionPayload>(payload, JsonOptions); }
        catch (JsonException) { return BadRequest(); }
        if (request is null || request.Values is null || request.Values.Count > 200 || files.Count > 10
            || files.Any(file => file.Length > MaxAttachmentSizeBytes)) return BadRequest();
        var attachments = files.Select(file => new TodoFormSubmitAttachment(Path.GetFileName(file.FileName),
            file.ContentType, file.Length, _ => file.OpenReadStream())).ToList();
        return Ok(await formService.SubmitAsync(new TodoFormSubmitRequest(request.FormId, request.Values, attachments,
            request.SubmissionKey, request.IssuedAtUtc, request.Honeypot, request.Password,
            HttpContext.Connection.RemoteIpAddress?.ToString()), ResolveUserId(), token));
    }

}
