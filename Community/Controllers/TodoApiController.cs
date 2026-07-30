using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Klassenbibliothek.Data;
using Klassenbibliothek.Localization;
using Klassenbibliothek.Services;

namespace TodoSuite.Server.Controllers;

[ApiController]
[Route("api")]
[Authorize(Policy = "MobileApi")]
public class TodoApiController : ControllerBase
{
    private const long MaxAttachmentSizeBytes = 25L * 1024 * 1024;

    [HttpGet("lists")]
    public async Task<IActionResult> GetLists([FromServices] ITodoListService listService, CancellationToken ct)
    {
        var userId = ResolveUserId();
        return Ok(await listService.GetListsAsync(userId, ct));
    }

    [HttpPost("lists")]
    public async Task<IActionResult> CreateList([FromBody] TodoListEntity model, [FromServices] ITodoListService listService, CancellationToken ct)
    {
        var userId = ResolveUserId();
        return Ok(await listService.AddListAsync(userId, model, ct));
    }

    [HttpPut("lists/{listId:guid}")]
    public async Task<IActionResult> UpdateList(Guid listId, [FromBody] TodoListEntity model, [FromServices] ITodoListService listService, CancellationToken ct)
    {
        var userId = ResolveUserId();
        model.Id = listId;
        var updated = await listService.UpdateListAsync(userId, model, ct);
        return updated is null ? NotFound() : Ok(updated);
    }

    [HttpPost("lists/{listId:guid}/tasks")]
    public async Task<IActionResult> CreateTask(Guid listId, [FromBody] TodoTaskEntity task, [FromServices] ITodoTaskService taskService, CancellationToken ct)
    {
        var userId = ResolveUserId();
        var created = await taskService.AddTaskAsync(userId, listId, task, ct);
        return created is null ? NotFound() : Ok(created);
    }

    [HttpPut("lists/{listId:guid}/tasks/{taskId:guid}")]
    public async Task<IActionResult> UpdateTask(Guid listId, Guid taskId, [FromBody] TodoTaskEntity task, [FromServices] ITodoTaskService taskService, CancellationToken ct)
    {
        var userId = ResolveUserId();
        task.Id = taskId;
        var updated = await taskService.UpdateTaskAsync(userId, listId, task, ct);
        return updated is null ? NotFound() : Ok(updated);
    }

    [HttpPost("lists/{listId:guid}/tasks/{taskId:guid}/attachments")]
    [RequestSizeLimit(MaxAttachmentSizeBytes + 1024 * 1024)]
    public async Task<IActionResult> AddAttachment(Guid listId, Guid taskId, [FromServices] ITodoAttachmentService attachmentService, CancellationToken ct)
    {
        var userId = ResolveUserId();
        var localizer = HttpContext.RequestServices.GetRequiredService<IStringLocalizer<SharedResource>>();
        if (!Request.HasFormContentType)
            return BadRequest(localizer["Err_Upload_FormDataExpected"].Value);

        var file = (await Request.ReadFormAsync(ct)).Files.FirstOrDefault();
        if (file is null)
            return BadRequest(localizer["Err_Upload_NoFileReceived"].Value);

        if (file.Length <= 0 || file.Length > MaxAttachmentSizeBytes)
            return BadRequest($"Datei ist zu gross. Maximal erlaubt sind {MaxAttachmentSizeBytes / 1024 / 1024} MB.");

        await using var stream = file.OpenReadStream();
        var att = await attachmentService.AddAttachmentAsync(userId, listId, taskId, file.FileName, stream, ct);
        return att is null ? NotFound() : Ok(att);
    }

    [HttpGet("attachments/{attachmentId:guid}")]
    public async Task<IActionResult> GetAttachment(Guid attachmentId, [FromQuery] Guid listId, [FromServices] ITodoAttachmentService attachmentService, CancellationToken ct)
    {
        var userId = ResolveUserId();
        var res = await attachmentService.GetAttachmentStreamAsync(userId, listId, attachmentId, ct);
        if (res is null) return NotFound();
        return File(res.Value.Stream, "application/octet-stream", res.Value.FileName);
    }

    private string ResolveUserId()
    {
        var id = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        return string.IsNullOrWhiteSpace(id) ? "gast" : id;
    }
}
