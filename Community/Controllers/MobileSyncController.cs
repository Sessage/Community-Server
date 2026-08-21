using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Klassenbibliothek.Data;
using Klassenbibliothek.Services;

namespace TodoSuite.Server.Controllers;

/// <summary>
/// Stable transport boundary for the shared mobile client. In addition to mapping DTOs it
/// enforces sync-token conflicts, so delayed offline writes cannot silently replace newer
/// changes from another device or the Web UI.
/// </summary>
[ApiController]
[Route("api/mobile")]
[Authorize(Policy = "MobileApi")]
public class MobileSyncController : ControllerBase
{
    private const long MaxAttachmentSizeBytes = 25L * 1024 * 1024;
    private const int MaxAttachmentChunkSizeBytes = 512 * 1024;
    private const int MaxChunkEnvelopeBytes = 64 * 1024;
    private const int MaxActiveChunkSessionsPerUser = 5;
    private const long MaxAttachmentBase64Chars = ((MaxAttachmentSizeBytes + 2) / 3) * 4;
    private static readonly byte[] ChunkEnvelopeMagic = Encoding.ASCII.GetBytes("TSU1");
    private static readonly JsonSerializerOptions ChunkEnvelopeJson = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions WorkspaceEtagJson = new(JsonSerializerDefaults.Web)
    {
        ReferenceHandler = ReferenceHandler.IgnoreCycles
    };
    private static readonly SemaphoreSlim ChunkSessionGate = new(1, 1);

    [HttpGet("lists")]
    public async Task<ActionResult<IReadOnlyList<TodoListEntity>>> GetLists([FromServices] ITodoListService listService, CancellationToken token)
    {
        var userId = ResolveUserId();
        var lists = await listService.GetListsAsync(userId, token);
        ApplySyncTokens(lists);
        var etag = CreateWorkspaceEtag(lists);
        if (Request.Headers.IfNoneMatch.Any(value => string.Equals(value, etag, StringComparison.Ordinal)))
            return StatusCode(StatusCodes.Status304NotModified);
        Response.Headers.ETag = etag;
        return Ok(lists);
    }

    [HttpPost("lists")]
    public async Task<ActionResult<TodoListEntity>> CreateList([FromBody] TodoListEntity model, [FromServices] ITodoListService listService, CancellationToken token)
    {
        var userId = ResolveUserId();
        var created = await listService.AddListAsync(userId, model, token);
        created.SyncToken = MobileSyncFingerprint.ForList(created);
        created.SyncVersion = created.ContentVersion;
        return Ok(created);
    }

    [HttpGet("templates")]
    public async Task<ActionResult<IReadOnlyList<TodoListEntity>>> GetTemplates([FromServices] ITodoListService listService, CancellationToken token)
    {
        var userId = ResolveUserId();
        return Ok(await listService.GetTemplatesAsync(userId, token));
    }

    [HttpPost("templates/{templateId:guid}/instantiate")]
    public async Task<ActionResult<TodoListEntity>> CreateListFromTemplate(
        Guid templateId,
        [FromBody] CreateListFromTemplateRequest request,
        [FromServices] ITodoListService listService,
        CancellationToken token)
    {
        var userId = ResolveUserId();
        var list = await listService.CreateListFromTemplateAsync(userId, templateId, request.Name, token);
        return Ok(list);
    }

    [HttpPut("lists/{listId:guid}")]
    public async Task<ActionResult<TodoListEntity>> UpdateList(Guid listId, [FromBody] TodoListEntity model, [FromServices] ITodoListService listService, CancellationToken token)
    {
        var userId = ResolveUserId();
        model.Id = listId;
        var current = await listService.GetListAsync(userId, listId, token);
        if (current is null) return NotFound();
        if (HasSyncConflict(model.SyncToken, MobileSyncFingerprint.ForList(current)))
            return Conflict(new { error = "Die Liste wurde auf einem anderen Gerät geändert.", entity = "list", listId });

        TodoListEntity? updated;
        try { updated = await listService.UpdateListAsync(userId, model, token); }
        catch (WorkspaceConcurrencyException ex) { return Conflict(new { error = ex.Message, entity = "list", listId }); }
        if (updated is null) return NotFound();
        var refreshed = await listService.GetListAsync(userId, listId, token) ?? updated;
        refreshed.SyncToken = MobileSyncFingerprint.ForList(refreshed);
        refreshed.SyncVersion = refreshed.ContentVersion;
        return Ok(refreshed);
    }

    [HttpPut("lists/{listId:guid}/columns/{columnName}/done")]
    public async Task<ActionResult> SetDoneColumn(
        Guid listId,
        string columnName,
        [FromBody] SetDoneColumnRequest request,
        [FromServices] ITodoColumnService columnService,
        CancellationToken token)
    {
        var userId = ResolveUserId();
        await columnService.SetDoneColumnAsync(userId, listId, columnName, request.IsDone, token);
        return Ok();
    }

    [HttpPut("lists/{listId:guid}/table-columns/order")]
    public async Task<ActionResult<IReadOnlyList<string>>> SetTableColumnOrder(
        Guid listId,
        [FromBody] IReadOnlyList<string> orderedColumnKeys,
        [FromServices] ITodoTableColumnOrderService tableColumnOrderService,
        CancellationToken token)
    {
        var userId = ResolveUserId();
        var saved = await tableColumnOrderService.SetTableColumnOrderAsync(userId, listId, orderedColumnKeys, token);
        return Ok(saved);
    }

    [HttpPut("lists/{listId:guid}/table-columns/hidden")]
    public async Task<ActionResult<IReadOnlyList<string>>> SetTableHiddenColumns(
        Guid listId,
        [FromBody] IReadOnlyList<string> hiddenColumnKeys,
        [FromServices] ITodoTableColumnOrderService tableColumnOrderService,
        CancellationToken token)
    {
        var userId = ResolveUserId();
        var saved = await tableColumnOrderService.SetTableHiddenColumnsAsync(userId, listId, hiddenColumnKeys, token);
        return Ok(saved);
    }

    [HttpDelete("lists/{listId:guid}")]
    public async Task<ActionResult> DeleteList(Guid listId, [FromServices] ITodoListService listService, CancellationToken token)
    {
        var userId = ResolveUserId();
        var deleted = await listService.DeleteListAsync(userId, listId, token);
        return deleted ? Ok() : NotFound();
    }

    [HttpPost("lists/{listId:guid}/labels")]
    public async Task<ActionResult<TodoLabelEntity>> CreateLabel(
        Guid listId,
        [FromBody] LabelRequest request,
        [FromServices] ITodoLabelService labelService,
        CancellationToken token)
    {
        var userId = ResolveUserId();
        var created = await labelService.AddLabelAsync(userId, listId, request.Title, request.BackgroundColor, token, request.Id);
        return created is null ? NotFound() : Ok(created);
    }

    [HttpPut("lists/{listId:guid}/labels/{labelId:guid}")]
    public async Task<ActionResult<TodoLabelEntity>> UpdateLabel(
        Guid listId,
        Guid labelId,
        [FromBody] LabelRequest request,
        [FromServices] ITodoLabelService labelService,
        CancellationToken token)
    {
        var userId = ResolveUserId();
        var updated = await labelService.UpdateLabelAsync(userId, listId, labelId, request.Title, request.BackgroundColor, token);
        return updated is null ? NotFound() : Ok(updated);
    }

    [HttpDelete("lists/{listId:guid}/labels/{labelId:guid}")]
    public async Task<ActionResult> DeleteLabel(
        Guid listId,
        Guid labelId,
        [FromServices] ITodoLabelService labelService,
        CancellationToken token)
    {
        var userId = ResolveUserId();
        var deleted = await labelService.DeleteLabelAsync(userId, listId, labelId, token);
        return deleted ? Ok() : NotFound();
    }

    [HttpPost("lists/{listId:guid}/tasks")]
    public async Task<ActionResult<TodoTaskEntity>> CreateTask(Guid listId, [FromBody] TodoTaskEntity model, [FromServices] ITodoTaskService taskService, [FromServices] ITodoListService listService, CancellationToken token)
    {
        var userId = ResolveUserId();
        var created = await taskService.AddTaskAsync(userId, listId, model, token);
        if (created is null) return NotFound();
        var refreshed = (await listService.GetListAsync(userId, listId, token))?.Tasks.FirstOrDefault(task => task.Id == created.Id) ?? created;
        refreshed.SyncToken = MobileSyncFingerprint.ForTask(refreshed);
        refreshed.SyncVersion = refreshed.ContentVersion;
        return Ok(refreshed);
    }

    [HttpGet("lists/{listId:guid}/automations")]
    public async Task<ActionResult<IReadOnlyList<TodoAutomationRuleEntity>>> GetAutomations(
        Guid listId,
        [FromServices] ITodoAutomationService automationService,
        CancellationToken token)
        => Ok(await automationService.GetRulesAsync(ResolveUserId(), listId, token));

    [HttpPost("lists/{listId:guid}/automations")]
    public async Task<ActionResult<TodoAutomationRuleEntity>> SaveAutomation(
        Guid listId,
        [FromBody] TodoAutomationRuleEntity rule,
        [FromServices] ITodoAutomationService automationService,
        CancellationToken token)
        => Ok(await automationService.SaveRuleAsync(ResolveUserId(), listId, rule, token));

    [HttpPut("lists/{listId:guid}/automations/{ruleId:guid}/enabled")]
    public async Task<ActionResult> SetAutomationEnabled(
        Guid listId,
        Guid ruleId,
        [FromBody] bool enabled,
        [FromServices] ITodoAutomationService automationService,
        CancellationToken token)
    {
        await automationService.SetRuleEnabledAsync(ResolveUserId(), listId, ruleId, enabled, token);
        return Ok();
    }

    [HttpDelete("lists/{listId:guid}/automations/{ruleId:guid}")]
    public async Task<ActionResult> DeleteAutomation(
        Guid listId,
        Guid ruleId,
        [FromServices] ITodoAutomationService automationService,
        CancellationToken token)
        => await automationService.DeleteRuleAsync(ResolveUserId(), listId, ruleId, token) ? Ok() : NotFound();

    [HttpPut("lists/{listId:guid}/tasks/{taskId:guid}")]
    public async Task<ActionResult<TodoTaskEntity>> UpdateTask(Guid listId, Guid taskId, [FromBody] TodoTaskEntity model, [FromServices] ITodoTaskService taskService, [FromServices] ITodoListService listService, CancellationToken token)
    {
        var userId = ResolveUserId();
        model.Id = taskId;
        var current = (await listService.GetListAsync(userId, listId, token))?.Tasks.FirstOrDefault(task => task.Id == taskId);
        if (current is null) return NotFound();
        if (HasSyncConflict(model.SyncToken, MobileSyncFingerprint.ForTask(current)))
            return Conflict(new { error = "Die Aufgabe wurde auf einem anderen Gerät geändert.", entity = "task", listId, taskId });

        TodoTaskEntity? updated;
        try { updated = await taskService.UpdateTaskAsync(userId, listId, model, token); }
        catch (WorkspaceConcurrencyException ex) { return Conflict(new { error = ex.Message, entity = "task", listId, taskId }); }
        if (updated is null) return NotFound();
        var refreshed = (await listService.GetListAsync(userId, listId, token))?.Tasks.FirstOrDefault(task => task.Id == taskId) ?? updated;
        refreshed.SyncToken = MobileSyncFingerprint.ForTask(refreshed);
        refreshed.SyncVersion = refreshed.ContentVersion;
        return Ok(refreshed);
    }

    [HttpPut("lists/{listId:guid}/tasks/{taskId:guid}/watching")]
    public async Task<ActionResult> SetTaskWatching(
        Guid listId,
        Guid taskId,
        [FromBody] SetWatchingRequest request,
        [FromServices] ITodoTaskService taskService,
        CancellationToken token)
    {
        var userId = ResolveUserId();
        var updated = await taskService.SetTaskWatchingAsync(userId, listId, taskId, request.Watching, token);
        return updated ? Ok() : NotFound();
    }

    [HttpPut("lists/{listId:guid}/watching")]
    public async Task<ActionResult> SetListWatching(
        Guid listId,
        [FromBody] SetWatchingRequest request,
        [FromServices] ITodoListService listService,
        CancellationToken token)
    {
        var userId = ResolveUserId();
        var updated = await listService.SetListWatchingAsync(userId, listId, request.Watching, token);
        return updated ? Ok() : NotFound();
    }

    [HttpPost("lists/{listId:guid}/tasks/{taskId:guid}/move")]
    public async Task<ActionResult<TodoTaskEntity>> MoveTask(
        Guid listId,
        Guid taskId,
        [FromBody] MoveTaskRequest request,
        [FromServices] ITodoTaskService taskService,
        CancellationToken token)
    {
        var userId = ResolveUserId();
        var moved = await taskService.MoveTaskToListAsync(userId, listId, request.ToListId, taskId, request.DesiredTargetColumn, token);
        return moved is null ? NotFound() : Ok(moved);
    }

    [HttpDelete("lists/{listId:guid}/tasks/{taskId:guid}")]
    public async Task<ActionResult> DeleteTask(Guid listId, Guid taskId, [FromServices] ITodoTaskService taskService, CancellationToken token)
    {
        var userId = ResolveUserId();
        var deleted = await taskService.DeleteTaskAsync(userId, listId, taskId, token);
        return deleted ? Ok() : NotFound();
    }

    [HttpPost("lists/{listId:guid}/custom-fields")]
    public async Task<ActionResult<TodoCustomFieldDefinitionEntity>> CreateCustomField(Guid listId, [FromBody] TodoCustomFieldDefinitionEntity field, [FromServices] ITodoCustomFieldService customFieldService, CancellationToken token)
    {
        var userId = ResolveUserId();
        var created = await customFieldService.AddFieldAsync(userId, listId, field, token);
        return created is null ? NotFound() : Ok(created);
    }

    [HttpPut("lists/{listId:guid}/custom-fields/{fieldId:guid}")]
    public async Task<ActionResult<TodoCustomFieldDefinitionEntity>> UpdateCustomField(Guid listId, Guid fieldId, [FromBody] TodoCustomFieldDefinitionEntity field, [FromServices] ITodoCustomFieldService customFieldService, CancellationToken token)
    {
        var userId = ResolveUserId();
        field.Id = fieldId;
        var updated = await customFieldService.UpdateFieldAsync(userId, listId, field, token);
        return updated is null ? NotFound() : Ok(updated);
    }

    [HttpDelete("lists/{listId:guid}/custom-fields/{fieldId:guid}")]
    public async Task<ActionResult> DeleteCustomField(Guid listId, Guid fieldId, [FromServices] ITodoCustomFieldService customFieldService, CancellationToken token)
    {
        var userId = ResolveUserId();
        var deleted = await customFieldService.DeleteFieldAsync(userId, listId, fieldId, token);
        return deleted ? Ok() : NotFound();
    }

    [HttpPost("lists/{listId:guid}/tasks/{taskId:guid}/approval")]
    public async Task<ActionResult<TodoTaskEntity>> DecideApproval(
        Guid listId,
        Guid taskId,
        [FromBody] ApprovalDecisionRequest request,
        [FromServices] ITodoTaskService taskService,
        [FromServices] ITodoListService listService,
        CancellationToken token)
    {
        var userId = ResolveUserId();
        var updated = await taskService.DecideApprovalAsync(userId, listId, taskId, request.Approved, token);
        if (updated is null) return NotFound();
        var refreshed = (await listService.GetListAsync(userId, listId, token))?.Tasks.FirstOrDefault(task => task.Id == taskId) ?? updated;
        refreshed.SyncToken = MobileSyncFingerprint.ForTask(refreshed);
        refreshed.SyncVersion = refreshed.ContentVersion;
        return Ok(refreshed);
    }

    [HttpPut("lists/{listId:guid}/custom-fields/reorder")]
    public async Task<ActionResult> ReorderCustomFields(
        Guid listId,
        [FromBody] IReadOnlyList<Guid> orderedFieldIds,
        [FromServices] ITodoCustomFieldService customFieldService,
        CancellationToken token)
    {
        await customFieldService.ReorderFieldsAsync(ResolveUserId(), listId, orderedFieldIds, token);
        return Ok();
    }

    [HttpPut("lists/{listId:guid}/tasks/reorder")]
    public async Task<ActionResult> ReorderListTasks(
        Guid listId,
        [FromBody] IReadOnlyList<Guid> orderedTaskIds,
        [FromServices] ITodoTaskService taskService,
        CancellationToken token)
    {
        var userId = ResolveUserId();
        await taskService.ReorderListAsync(userId, listId, orderedTaskIds, token);
        return Ok();
    }

    [HttpPut("lists/{listId:guid}/kanban/reorder")]
    public async Task<ActionResult> ReorderKanbanColumnTasks(
        Guid listId,
        [FromBody] ReorderKanbanTasksRequest request,
        [FromServices] ITodoTaskService taskService,
        CancellationToken token)
    {
        var userId = ResolveUserId();
        await taskService.ReorderKanbanColumnAsync(userId, listId, request.Column, request.OrderedTaskIds, token);
        return Ok();
    }

    /* -------- Comments -------- */

    [HttpPost("lists/{listId:guid}/tasks/{taskId:guid}/comments")]
    public async Task<ActionResult<TodoCommentEntity>> AddComment(
        Guid listId, Guid taskId,
        [FromBody] AddCommentRequest request,
        [FromServices] ITodoCommentService commentService,
        CancellationToken token)
    {
        var userId = ResolveUserId();
        var comment = await commentService.AddCommentAsync(userId, listId, taskId, request.Message, token, request.Id);
        return comment is null ? NotFound() : Ok(comment);
    }

    [HttpDelete("lists/{listId:guid}/tasks/{taskId:guid}/comments/{commentId:guid}")]
    public async Task<ActionResult> RemoveComment(
        Guid listId, Guid taskId, Guid commentId,
        [FromServices] ITodoCommentService commentService,
        CancellationToken token)
    {
        var userId = ResolveUserId();
        var removed = await commentService.RemoveCommentAsync(userId, listId, taskId, commentId, token);
        return removed ? Ok() : NotFound();
    }

    /* -------- Attachments -------- */

    // deprecated: Temporary diagnostics endpoint; not used by the app or server code.
    // [HttpPost("attachments/upload-ping")]
    // [IgnoreAntiforgeryToken]
    // [RequestSizeLimit(1024)]
    // public async Task<ActionResult<string>> UploadPing(CancellationToken token)
    // {
    //     using var reader = new StreamReader(Request.Body, leaveOpen: false);
    //     var body = await reader.ReadToEndAsync(token);
    //     return Ok($"pong:{body.Length}");
    // }

    [HttpPost("lists/{listId:guid}/tasks/{taskId:guid}/attachments/raw")]
    [IgnoreAntiforgeryToken]
    [RequestSizeLimit(MaxAttachmentSizeBytes + 1024 * 1024)]
    public async Task<ActionResult<TodoAttachmentEntity>> AddAttachmentRaw(
        Guid listId,
        Guid taskId,
        [FromQuery] string? fileName,
        [FromServices] ITodoAttachmentService attachmentService,
        CancellationToken token)
    {
        var userId = ResolveUserId();
        var contentLength = Request.ContentLength;
        if (contentLength is null or <= 0)
            return BadRequest("Keine Datei empfangen.");

        if (contentLength > MaxAttachmentSizeBytes)
            return BadRequest($"Datei ist zu gross. Maximal erlaubt sind {MaxAttachmentSizeBytes / 1024 / 1024} MB.");

        try
        {
            var attachment = await attachmentService.AddAttachmentAsync(
                userId,
                listId,
                taskId,
                string.IsNullOrWhiteSpace(fileName) ? "datei" : fileName,
                Request.Body,
                token);

            return attachment is null ? NotFound() : Ok(attachment);
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (IOException ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, $"Upload konnte nicht gespeichert werden: {ex.Message}");
        }
    }

    [HttpPost("lists/{listId:guid}/tasks/{taskId:guid}/attachments/base64")]
    [IgnoreAntiforgeryToken]
    [RequestSizeLimit(MaxAttachmentSizeBytes * 2)]
    public async Task<ActionResult<TodoAttachmentEntity>> AddAttachmentBase64(
        Guid listId,
        Guid taskId,
        [FromBody] UploadAttachmentRequest request,
        [FromServices] ITodoAttachmentService attachmentService,
        CancellationToken token)
    {
        var userId = ResolveUserId();
        if (request is null || string.IsNullOrWhiteSpace(request.ContentBase64))
            return BadRequest("Keine Datei empfangen.");

        if (request.ContentBase64.Length > MaxAttachmentBase64Chars)
            return BadRequest($"Datei ist zu gross. Maximal erlaubt sind {MaxAttachmentSizeBytes / 1024 / 1024} MB.");

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(request.ContentBase64);
        }
        catch (FormatException)
        {
            return BadRequest("Upload konnte nicht gelesen werden: ungueltiger Dateiinhalt.");
        }

        if (bytes.Length <= 0 || bytes.Length > MaxAttachmentSizeBytes)
            return BadRequest($"Datei ist zu gross. Maximal erlaubt sind {MaxAttachmentSizeBytes / 1024 / 1024} MB.");

        try
        {
            await using var stream = new MemoryStream(bytes);
            var attachment = await attachmentService.AddAttachmentAsync(userId, listId, taskId, request.FileName, stream, token, request.Id);
            return attachment is null ? NotFound() : Ok(attachment);
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (IOException ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, $"Upload konnte nicht gespeichert werden: {ex.Message}");
        }
    }

    [HttpPost("lists/{listId:guid}/tasks/{taskId:guid}/attachments")]
    [IgnoreAntiforgeryToken]
    [RequestSizeLimit(MaxAttachmentSizeBytes + 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxAttachmentSizeBytes + 1024 * 1024)]
    public async Task<ActionResult<TodoAttachmentEntity>> AddAttachment(
        Guid listId,
        Guid taskId,
        [FromServices] ITodoAttachmentService attachmentService,
        CancellationToken token)
    {
        var userId = ResolveUserId();
        if (!Request.HasFormContentType)
            return BadRequest("Multipart-Formulardaten erwartet.");

        IFormFile? file;
        try
        {
            file = (await Request.ReadFormAsync(token)).Files.FirstOrDefault();
        }
        catch (InvalidDataException ex)
        {
            return BadRequest($"Upload konnte nicht gelesen werden: {ex.Message}");
        }
        catch (BadHttpRequestException ex)
        {
            return BadRequest($"Upload konnte nicht gelesen werden: {ex.Message}");
        }

        if (file is null)
            return BadRequest("Keine Datei empfangen.");

        if (file.Length <= 0 || file.Length > MaxAttachmentSizeBytes)
            return BadRequest($"Datei ist zu gross. Maximal erlaubt sind {MaxAttachmentSizeBytes / 1024 / 1024} MB.");

        try
        {
            await using var stream = file.OpenReadStream();
            var attachment = await attachmentService.AddAttachmentAsync(userId, listId, taskId, file.FileName, stream, token);
            return attachment is null ? NotFound() : Ok(attachment);
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (IOException ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, $"Upload konnte nicht gespeichert werden: {ex.Message}");
        }
    }

    public sealed record UploadAttachmentRequest(string FileName, string ContentBase64, Guid? Id = null);

    [HttpPost("lists/{listId:guid}/tasks/{taskId:guid}/attachments/chunk-sessions")]
    [IgnoreAntiforgeryToken]
    public async Task<ActionResult<StartChunkUploadResponse>> StartAttachmentChunkUpload(
        Guid listId,
        Guid taskId,
        [FromBody] StartChunkUploadRequest request,
        [FromServices] IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken token)
    {
        if (request is null)
            return BadRequest("Upload konnte nicht vorbereitet werden.");

        if (request.TotalBytes <= 0)
            return BadRequest("Keine Datei empfangen.");

        if (request.TotalBytes > MaxAttachmentSizeBytes)
            return BadRequest($"Datei ist zu gross. Maximal erlaubt sind {MaxAttachmentSizeBytes / 1024 / 1024} MB.");

        var userId = ResolveUserId();
        var userEmail = ResolveUserEmail();
        if (!await CanUploadAttachmentAsync(dbFactory, userId, userEmail, listId, taskId, token))
            return Forbid();

        await ChunkSessionGate.WaitAsync(token);
        try
        {
            CleanupExpiredChunkUploads();
            if (await CountActiveChunkSessionsAsync(userId, token) >= MaxActiveChunkSessionsPerUser)
                return StatusCode(StatusCodes.Status429TooManyRequests, "Zu viele parallele Uploads. Bitte laufende Uploads zuerst abschliessen.");

            var uploadId = Guid.NewGuid();
            var session = new ChunkUploadSession(
                userId,
                userEmail,
                listId,
                taskId,
                string.IsNullOrWhiteSpace(request.FileName) ? "datei" : request.FileName,
                request.TotalBytes,
                DateTime.UtcNow);

            Directory.CreateDirectory(Path.GetDirectoryName(GetChunkSessionPath(uploadId))!);
            await System.IO.File.WriteAllTextAsync(GetChunkSessionPath(uploadId), JsonSerializer.Serialize(session, ChunkEnvelopeJson), token);
            return Ok(new StartChunkUploadResponse(uploadId));
        }
        finally
        {
            ChunkSessionGate.Release();
        }
    }

    public sealed record StartChunkUploadRequest(string FileName, long TotalBytes);
    public sealed record StartChunkUploadResponse(Guid UploadId);
    private sealed record ChunkUploadSession(string UserId, string? UserEmail, Guid ListId, Guid TaskId, string FileName, long TotalBytes, DateTime CreatedAtUtc);
    public sealed record JsonChunkUploadRequest(int ChunkNumber, int TotalChunks, string? FileName, string ContentBase64);
    public sealed record JsonChunkUploadResult(bool Completed, TodoAttachmentEntity? Attachment, string? Error);

    [HttpPost("lists/{listId:guid}/tasks/{taskId:guid}/attachments/chunks/{uploadId:guid}/json")]
    [IgnoreAntiforgeryToken]
    [RequestSizeLimit(256 * 1024)]
    public async Task<ActionResult> AddAttachmentJsonChunk(
        Guid listId,
        Guid taskId,
        Guid uploadId,
        [FromBody] JsonChunkUploadRequest request,
        [FromServices] ITodoAttachmentService attachmentService,
        CancellationToken token)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.ContentBase64))
            return BadRequest("Kein Chunk empfangen.");

        if (request.ChunkNumber <= 0 || request.TotalChunks <= 0 || request.ChunkNumber > request.TotalChunks)
            return BadRequest("Upload-Chunk enthält ungültige Metadaten.");

        if (request.ContentBase64.Length > ((MaxAttachmentChunkSizeBytes + 2) / 3) * 4)
            return BadRequest($"Upload-Chunk ist zu gross. Maximal erlaubt sind {MaxAttachmentChunkSizeBytes / 1024} KB.");

        byte[] chunkBytes;
        try
        {
            chunkBytes = Convert.FromBase64String(request.ContentBase64);
        }
        catch (FormatException)
        {
            return BadRequest("Upload-Chunk konnte nicht gelesen werden.");
        }

        if (chunkBytes.Length <= 0)
            return BadRequest("Kein Chunk empfangen.");

        if (chunkBytes.Length > MaxAttachmentChunkSizeBytes)
            return BadRequest($"Upload-Chunk ist zu gross. Maximal erlaubt sind {MaxAttachmentChunkSizeBytes / 1024} KB.");

        try
        {
            var session = await TryReadChunkSessionAsync(uploadId, token);
            if (session is null)
                return BadRequest("Upload-Session wurde nicht gefunden oder ist abgelaufen.");
            if (!IsSessionForRequest(session, ResolveUserId(), ResolveUserEmail(), listId, taskId))
                return Forbid();

            var path = GetChunkUploadPath(uploadId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            long uploadLength;
            await using (var target = System.IO.File.Open(path, FileMode.Append, FileAccess.Write, FileShare.None))
            {
                await target.WriteAsync(chunkBytes.AsMemory(), token);
                uploadLength = target.Length;
            }

            if (uploadLength > MaxAttachmentSizeBytes)
            {
                try { System.IO.File.Delete(path); } catch { }
                try { System.IO.File.Delete(GetChunkSessionPath(uploadId)); } catch { }
                return BadRequest($"Datei ist zu gross. Maximal erlaubt sind {MaxAttachmentSizeBytes / 1024 / 1024} MB.");
            }

            if (uploadLength > session.TotalBytes)
            {
                try { System.IO.File.Delete(path); } catch { }
                try { System.IO.File.Delete(GetChunkSessionPath(uploadId)); } catch { }
                return BadRequest("Upload ist groesser als erwartet.");
            }

            if (request.ChunkNumber < request.TotalChunks)
                return Ok(new JsonChunkUploadResult(false, null, null));

            var finalFileName = !string.IsNullOrWhiteSpace(request.FileName)
                ? request.FileName
                : session!.FileName;

            if (uploadLength != session.TotalBytes)
                return Ok(new JsonChunkUploadResult(true, null, "Upload ist unvollstaendig."));

            var result = await TryCompleteChunkUploadAsync(
                listId,
                taskId,
                uploadId,
                finalFileName,
                attachmentService,
                token);
            return Ok(result);
        }
        catch (IOException ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, $"Upload-Chunk konnte nicht gespeichert werden: {ex.Message}");
        }
    }

    [HttpPost("lists/{listId:guid}/tasks/{taskId:guid}/attachments/chunks/{uploadId:guid}")]
    [IgnoreAntiforgeryToken]
    [RequestSizeLimit(MaxAttachmentChunkSizeBytes + 64 * 1024)]
    public async Task<ActionResult> AddAttachmentChunk(
        Guid listId,
        Guid taskId,
        Guid uploadId,
        [FromQuery] bool complete,
        [FromQuery] string? fileName,
        [FromServices] ITodoAttachmentService attachmentService,
        CancellationToken token)
    {
        var contentLength = Request.ContentLength;
        if (contentLength is null or <= 0)
            return BadRequest("Kein Chunk empfangen.");

        if (contentLength > MaxAttachmentChunkSizeBytes + MaxChunkEnvelopeBytes)
            return BadRequest($"Upload-Chunk ist zu gross. Maximal erlaubt sind {MaxAttachmentChunkSizeBytes / 1024} KB.");

        try
        {
            var session = await TryReadChunkSessionAsync(uploadId, token);
            if (session is null)
                return BadRequest("Upload-Session wurde nicht gefunden oder ist abgelaufen.");
            if (!IsSessionForRequest(session, ResolveUserId(), ResolveUserEmail(), listId, taskId))
                return Forbid();

            byte[] requestBytes;
            await using (var requestBuffer = new MemoryStream())
            {
                await Request.Body.CopyToAsync(requestBuffer, token);
                requestBytes = requestBuffer.ToArray();
            }

            if (!TryReadChunkEnvelope(requestBytes, out var envelope, out var payloadOffset, out var parseError))
                return BadRequest(parseError ?? "Upload-Chunk konnte nicht gelesen werden.");

            var payloadLength = requestBytes.Length - payloadOffset;
            if (payloadLength <= 0)
                return BadRequest("Kein Chunk empfangen.");

            if (payloadLength > MaxAttachmentChunkSizeBytes)
                return BadRequest($"Upload-Chunk ist zu gross. Maximal erlaubt sind {MaxAttachmentChunkSizeBytes / 1024} KB.");

            var path = GetChunkUploadPath(uploadId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            long uploadLength;
            await using (var target = System.IO.File.Open(path, FileMode.Append, FileAccess.Write, FileShare.None))
            {
                await target.WriteAsync(requestBytes.AsMemory(payloadOffset, payloadLength), token);
                uploadLength = target.Length;
            }

            if (uploadLength > MaxAttachmentSizeBytes)
            {
                try { System.IO.File.Delete(path); } catch { }
                try { System.IO.File.Delete(GetChunkSessionPath(uploadId)); } catch { }
                return BadRequest($"Datei ist zu gross. Maximal erlaubt sind {MaxAttachmentSizeBytes / 1024 / 1024} MB.");
            }

            if (uploadLength < session.TotalBytes)
                return Ok();

            if (uploadLength > session.TotalBytes)
            {
                try { System.IO.File.Delete(path); } catch { }
                try { System.IO.File.Delete(GetChunkSessionPath(uploadId)); } catch { }
                return BadRequest("Upload ist groesser als erwartet.");
            }

            return await CompleteChunkUploadAsync(
                listId,
                taskId,
                uploadId,
                session.FileName,
                attachmentService,
                token);
        }
        catch (IOException ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, $"Upload-Chunk konnte nicht gespeichert werden: {ex.Message}");
        }
    }

    [HttpPost("lists/{listId:guid}/tasks/{taskId:guid}/attachments/chunks/{uploadId:guid}/complete")]
    [IgnoreAntiforgeryToken]
    public async Task<ActionResult> CompleteAttachmentChunkUpload(
        Guid listId,
        Guid taskId,
        Guid uploadId,
        [FromBody] CompleteChunkUploadRequest? request,
        [FromQuery] string? fileName,
        [FromServices] ITodoAttachmentService attachmentService,
        CancellationToken token)
        => await CompleteChunkUploadAsync(listId, taskId, uploadId, request?.FileName ?? fileName, attachmentService, token);
 
    public sealed record CompleteChunkUploadRequest(string? FileName);

    private async Task<ActionResult> CompleteChunkUploadAsync(
        Guid listId,
        Guid taskId,
        Guid uploadId,
        string? fileName,
        ITodoAttachmentService attachmentService,
        CancellationToken token)
    {
        var userId = ResolveUserId();
        var session = await TryReadChunkSessionAsync(uploadId, token);
        if (session is null)
            return BadRequest("Upload-Session wurde nicht gefunden oder ist abgelaufen.");
        if (!IsSessionForRequest(session, userId, ResolveUserEmail(), listId, taskId))
            return Forbid();

        if (string.IsNullOrWhiteSpace(fileName))
            fileName = session.FileName;

        var path = GetChunkUploadPath(uploadId);
        if (!System.IO.File.Exists(path))
            return BadRequest("Upload wurde nicht gefunden.");

        try
        {
            var info = new FileInfo(path);
            if (info.Length <= 0)
                return BadRequest("Keine Datei empfangen.");

            if (info.Length > MaxAttachmentSizeBytes)
                return BadRequest($"Datei ist zu gross. Maximal erlaubt sind {MaxAttachmentSizeBytes / 1024 / 1024} MB.");

            await using var stream = System.IO.File.OpenRead(path);
            var attachment = await attachmentService.AddAttachmentAsync(
                userId,
                listId,
                taskId,
                string.IsNullOrWhiteSpace(fileName) ? "datei" : fileName,
                stream,
                token);

            return attachment is null ? NotFound() : Ok(attachment);
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message);
        }
        catch (IOException ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, $"Upload konnte nicht gespeichert werden: {ex.Message}");
        }
        finally
        {
            try { System.IO.File.Delete(path); } catch { }
            try { System.IO.File.Delete(GetChunkSessionPath(uploadId)); } catch { }
        }
    }

    private async Task<JsonChunkUploadResult> TryCompleteChunkUploadAsync(
        Guid listId,
        Guid taskId,
        Guid uploadId,
        string? fileName,
        ITodoAttachmentService attachmentService,
        CancellationToken token)
    {
        var userId = ResolveUserId();
        var session = await TryReadChunkSessionAsync(uploadId, token);
        if (session is null)
            return new JsonChunkUploadResult(true, null, "Upload-Session wurde nicht gefunden oder ist abgelaufen.");
        if (!IsSessionForRequest(session, userId, ResolveUserEmail(), listId, taskId))
            return new JsonChunkUploadResult(true, null, "Keine Berechtigung zum Hochladen des Anhangs.");

        if (string.IsNullOrWhiteSpace(fileName))
            fileName = session.FileName;

        var path = GetChunkUploadPath(uploadId);
        if (!System.IO.File.Exists(path))
            return new JsonChunkUploadResult(true, null, "Upload wurde nicht gefunden.");

        try
        {
            var info = new FileInfo(path);
            if (info.Length <= 0)
                return new JsonChunkUploadResult(true, null, "Keine Datei empfangen.");

            if (info.Length > MaxAttachmentSizeBytes)
                return new JsonChunkUploadResult(true, null, $"Datei ist zu gross. Maximal erlaubt sind {MaxAttachmentSizeBytes / 1024 / 1024} MB.");

            await using var stream = System.IO.File.OpenRead(path);
            var attachment = await attachmentService.AddAttachmentAsync(
                userId,
                listId,
                taskId,
                string.IsNullOrWhiteSpace(fileName) ? "datei" : fileName,
                stream,
                token);

            return attachment is null
                ? new JsonChunkUploadResult(true, null, "Liste oder Aufgabe wurde nicht gefunden.")
                : new JsonChunkUploadResult(true, attachment, null);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new JsonChunkUploadResult(true, null, string.IsNullOrWhiteSpace(ex.Message) ? "Keine Berechtigung zum Hochladen des Anhangs." : ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return new JsonChunkUploadResult(true, null, string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message);
        }
        catch (IOException ex)
        {
            return new JsonChunkUploadResult(true, null, $"Upload konnte nicht gespeichert werden: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new JsonChunkUploadResult(true, null, $"Upload konnte nicht abgeschlossen werden: {ex.Message}");
        }
        finally
        {
            try { System.IO.File.Delete(path); } catch { }
            try { System.IO.File.Delete(GetChunkSessionPath(uploadId)); } catch { }
        }
    }

    private static string GetChunkUploadPath(Guid uploadId)
    {
        return Path.Combine(GetChunkUploadRoot(), $"{uploadId:N}.upload");
    }

    private static string GetChunkSessionPath(Guid uploadId)
    {
        return Path.Combine(GetChunkUploadRoot(), $"{uploadId:N}.json");
    }

    private static string GetChunkUploadRoot()
    {
        return Path.Combine(Path.GetTempPath(), "SessageMobileUploads");
    }

    private static void CleanupExpiredChunkUploads()
    {
        var root = GetChunkUploadRoot();
        if (!Directory.Exists(root))
            return;

        var cutoff = DateTime.UtcNow.AddHours(-4);
        foreach (var file in Directory.EnumerateFiles(root, "*.*", SearchOption.TopDirectoryOnly))
        {
            try
            {
                if (System.IO.File.GetLastWriteTimeUtc(file) < cutoff)
                    System.IO.File.Delete(file);
            }
            catch
            {
                // Best-effort cleanup; active uploads must not fail because another process has a temp file open.
            }
        }
    }

    private static async Task<ChunkUploadSession?> TryReadChunkSessionAsync(Guid uploadId, CancellationToken token)
    {
        var sessionPath = GetChunkSessionPath(uploadId);
        if (!System.IO.File.Exists(sessionPath))
            return null;

        try
        {
            await using var sessionStream = System.IO.File.OpenRead(sessionPath);
            return await JsonSerializer.DeserializeAsync<ChunkUploadSession>(sessionStream, ChunkEnvelopeJson, token);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static bool IsSessionForRequest(ChunkUploadSession session, string userId, string? userEmail, Guid listId, Guid taskId)
    {
        if (session.ListId != listId || session.TaskId != taskId)
            return false;

        if (string.Equals(session.UserId, userId, StringComparison.OrdinalIgnoreCase))
            return true;

        return !string.IsNullOrWhiteSpace(session.UserEmail)
               && !string.IsNullOrWhiteSpace(userEmail)
               && string.Equals(session.UserEmail, userEmail, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> CanUploadAttachmentAsync(
        IDbContextFactory<ApplicationDbContext> dbFactory,
        string userId,
        string? userEmail,
        Guid listId,
        Guid taskId,
        CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var list = await db.TodoLists
            .Include(l => l.Participants)
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == listId && l.DeletedAt == null, token);

        if (list is null)
            return false;

        var canWrite = string.Equals(list.OwnerId, userId, StringComparison.OrdinalIgnoreCase)
                       || list.Participants.Any(p =>
                            !p.InvitationPending
                            && p.Role != ListRole.Observer
                           && (string.Equals(p.UserId, userId, StringComparison.OrdinalIgnoreCase)
                               || (!string.IsNullOrWhiteSpace(userEmail)
                                   && string.Equals(p.Email, userEmail, StringComparison.OrdinalIgnoreCase))));

        if (!canWrite)
            return false;

        return await db.TodoTasks
            .AsNoTracking()
            .AnyAsync(t => t.Id == taskId && t.ListId == listId && t.DeletedAt == null, token);
    }

    private sealed record ChunkUploadEnvelope(int ChunkNumber, int TotalChunks, string? FileName);

    private static bool TryReadChunkEnvelope(
        byte[] requestBytes,
        out ChunkUploadEnvelope? envelope,
        out int payloadOffset,
        out string? error)
    {
        envelope = null;
        payloadOffset = 0;
        error = null;

        if (requestBytes.Length < 8 || !requestBytes.AsSpan(0, 4).SequenceEqual(ChunkEnvelopeMagic))
            return true;

        var metadataLength = BinaryPrimitives.ReadInt32LittleEndian(requestBytes.AsSpan(4, 4));
        if (metadataLength <= 0 || metadataLength > MaxChunkEnvelopeBytes)
        {
            error = "Upload-Chunk enthält ungültige Metadaten.";
            return false;
        }

        payloadOffset = 8 + metadataLength;
        if (requestBytes.Length < payloadOffset)
        {
            error = "Upload-Chunk ist unvollständig.";
            return false;
        }

        try
        {
            envelope = JsonSerializer.Deserialize<ChunkUploadEnvelope>(
                requestBytes.AsSpan(8, metadataLength),
                ChunkEnvelopeJson);
        }
        catch (JsonException)
        {
            error = "Upload-Chunk enthält ungültige Metadaten.";
            return false;
        }

        if (envelope is null || envelope.ChunkNumber <= 0 || envelope.TotalChunks <= 0 || envelope.ChunkNumber > envelope.TotalChunks)
        {
            error = "Upload-Chunk enthält ungültige Metadaten.";
            return false;
        }

        return true;
    }

    [HttpDelete("lists/{listId:guid}/tasks/{taskId:guid}/attachments/{attachmentId:guid}")]
    public async Task<ActionResult> RemoveAttachment(
        Guid listId,
        Guid taskId,
        Guid attachmentId,
        [FromServices] ITodoAttachmentService attachmentService,
        CancellationToken token)
    {
        var userId = ResolveUserId();
        var removed = await attachmentService.RemoveAttachmentAsync(userId, listId, taskId, attachmentId, token);
        return removed ? Ok() : NotFound();
    }

    [HttpGet("attachments/{attachmentId:guid}")]
    public async Task<IActionResult> GetAttachment(
        Guid attachmentId,
        [FromQuery] Guid listId,
        [FromServices] ITodoAttachmentService attachmentService,
        CancellationToken token)
    {
        var userId = ResolveUserId();
        var result = await attachmentService.GetAttachmentStreamAsync(userId, listId, attachmentId, token);
        if (result is null) return NotFound();
        return File(result.Value.Stream, "application/octet-stream", result.Value.FileName);
    }

    /* -------- Navigation Groups -------- */

    [HttpGet("groups")]
    public async Task<ActionResult<IReadOnlyList<TodoListGroupEntity>>> GetGroups(
        [FromServices] ITodoNavigationService navService, CancellationToken token)
    {
        var userId = ResolveUserId();
        return Ok(await navService.GetListGroupsAsync(userId, token));
    }

    [HttpPost("groups")]
    public async Task<ActionResult<TodoListGroupEntity>> CreateGroup(
        [FromBody] GroupNameRequest request,
        [FromServices] ITodoNavigationService navService, CancellationToken token)
    {
        var userId = ResolveUserId();
        var group = await navService.AddListGroupAsync(userId, request.Name, request.IsPortfolio, token, request.Id);
        return Ok(group);
    }

    private static async Task<int> CountActiveChunkSessionsAsync(string userId, CancellationToken token)
    {
        var root = GetChunkUploadRoot();
        if (!Directory.Exists(root))
            return 0;

        var count = 0;
        foreach (var sessionPath in Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly))
        {
            token.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(sessionPath), "N", out var uploadId))
                continue;

            var session = await TryReadChunkSessionAsync(uploadId, token);
            if (session is not null && string.Equals(session.UserId, userId, StringComparison.OrdinalIgnoreCase))
                count++;
        }

        return count;
    }

    [HttpPut("groups/{groupId:guid}/portfolio")]
    public async Task<ActionResult> SetGroupPortfolio(Guid groupId, [FromBody] GroupPortfolioRequest request,
        [FromServices] ITodoNavigationService navService, CancellationToken token)
    {
        await navService.SetListGroupPortfolioAsync(ResolveUserId(), groupId, request.IsPortfolio, token);
        return Ok();
    }

    [HttpPut("groups/{groupId:guid}/collapsed")]
    public async Task<ActionResult> SetGroupCollapsed(Guid groupId, [FromBody] GroupCollapsedRequest request,
        [FromServices] ITodoNavigationService navService, CancellationToken token)
    {
        await navService.SetListGroupCollapsedAsync(ResolveUserId(), groupId, request.IsCollapsed, token);
        return Ok();
    }

    [HttpPut("groups/{groupId:guid}")]
    public async Task<ActionResult> RenameGroup(
        Guid groupId, [FromBody] GroupNameRequest request,
        [FromServices] ITodoNavigationService navService, CancellationToken token)
    {
        var userId = ResolveUserId();
        await navService.RenameListGroupAsync(userId, groupId, request.Name, token);
        return Ok();
    }

    [HttpDelete("groups/{groupId:guid}")]
    public async Task<ActionResult> DeleteGroup(
        Guid groupId,
        [FromServices] ITodoNavigationService navService, CancellationToken token,
        [FromQuery] bool ungroupLists = true)
    {
        var userId = ResolveUserId();
        await navService.DeleteListGroupAsync(userId, groupId, ungroupLists, token);
        return Ok();
    }

    [HttpPut("groups/reorder")]
    public async Task<ActionResult> ReorderGroups(
        [FromBody] IReadOnlyList<Guid> orderedGroupIds,
        [FromServices] ITodoNavigationService navService, CancellationToken token)
    {
        var userId = ResolveUserId();
        await navService.ReorderListGroupsAsync(userId, orderedGroupIds, token);
        return Ok();
    }

    [HttpPut("navigation/lists/reorder")]
    public async Task<ActionResult> ReorderNavigationLists(
        [FromBody] ReorderNavigationListsRequest request,
        [FromServices] ITodoNavigationService navService, CancellationToken token)
    {
        var userId = ResolveUserId();
        await navService.ReorderNavigationListsAsync(userId, request.GroupId, request.OrderedListIds, token);
        return Ok();
    }

    [HttpPut("navigation/lists/{listId:guid}/move")]
    public async Task<ActionResult> MoveList(
        Guid listId, [FromBody] MoveListRequest request,
        [FromServices] ITodoNavigationService navService, CancellationToken token)
    {
        var userId = ResolveUserId();
        await navService.MoveListAsync(userId, listId, request.FromGroupId, request.ToGroupId, request.FromOrderedIds, request.ToOrderedIds, token);
        return Ok();
    }

    [HttpPut("navigation/mixed/reorder")]
    public async Task<ActionResult> ReorderMixedNavigation(
        [FromBody] IReadOnlyList<string> orderedDescriptors,
        [FromServices] ITodoNavigationService navService, CancellationToken token)
    {
        var userId = ResolveUserId();
        await navService.ReorderMixedNavigationAsync(userId, orderedDescriptors, token);
        return Ok();
    }

    /* -------- Sharing -------- */

    [HttpGet("lists/{listId:guid}/share-links")]
    public async Task<ActionResult<IReadOnlyList<ShareLinkInfo>>> GetShareLinks(
        Guid listId,
        [FromServices] IListSharingService sharingService,
        CancellationToken token)
    {
        var userId = ResolveUserId();
        var links = await sharingService.GetShareLinksAsync(userId, listId);
        return Ok(links);
    }

    [HttpPost("lists/{listId:guid}/share-links")]
    public async Task<ActionResult<ShareLinkResult>> CreateShareLink(
        Guid listId,
        [FromBody] CreateShareLinkRequest request,
        [FromServices] IListSharingService sharingService,
        CancellationToken token)
    {
        var userId = ResolveUserId();
        var (success, message, link) = await sharingService.CreateShareLinkAsync(userId, listId, request.Role, request.Comment);
        return Ok(new ShareLinkResult(success, message, link));
    }

    [HttpPut("lists/{listId:guid}/share-links/{inviteId:guid}/comment")]
    public async Task<ActionResult<OperationResult>> UpdateShareLinkComment(
        Guid listId, Guid inviteId,
        [FromBody] UpdateCommentRequest request,
        [FromServices] IListSharingService sharingService,
        CancellationToken token)
    {
        var userId = ResolveUserId();
        var (success, message) = await sharingService.UpdateShareLinkCommentAsync(userId, listId, inviteId, request.Comment);
        return Ok(new OperationResult(success, message));
    }

    [HttpDelete("lists/{listId:guid}/share-links/{inviteId:guid}")]
    public async Task<ActionResult<OperationResult>> RevokeShareLink(
        Guid listId, Guid inviteId,
        [FromServices] IListSharingService sharingService,
        CancellationToken token)
    {
        var userId = ResolveUserId();
        var (success, message) = await sharingService.RevokeShareLinkAsync(userId, listId, inviteId);
        return Ok(new OperationResult(success, message));
    }

    [HttpPost("lists/{listId:guid}/share-links/accept")]
    public async Task<ActionResult<OperationResult>> AcceptShareLink(
        Guid listId,
        [FromBody] AcceptShareLinkRequest request,
        [FromServices] IListSharingService sharingService,
        CancellationToken token)
    {
        var userId = ResolveUserId();
        var (success, message) = await sharingService.AcceptShareLinkAsync(userId, listId, request.Token);
        return Ok(new OperationResult(success, message));
    }

    [HttpPost("lists/{listId:guid}/invite-by-email")]
    public async Task<ActionResult<InviteResult>> InviteByEmail(
        Guid listId,
        [FromBody] InviteByEmailRequest request,
        [FromServices] IListSharingService sharingService,
        CancellationToken token)
    {
        var userId = ResolveUserId();
        var result = await sharingService.InviteByEmailAsync(userId, listId, request.Email, request.DisplayName, request.Role);
        return Ok(result);
    }

    [HttpPut("lists/{listId:guid}/participants/{participantId:guid}/role")]
    public async Task<ActionResult<OperationResult>> UpdateParticipantRole(
        Guid listId, Guid participantId,
        [FromBody] UpdateRoleRequest request,
        [FromServices] IListSharingService sharingService,
        CancellationToken token)
    {
        var userId = ResolveUserId();
        var (success, message) = await sharingService.UpdateParticipantRoleAsync(userId, listId, participantId, request.Role);
        return Ok(new OperationResult(success, message));
    }

    [HttpDelete("lists/{listId:guid}/participants/{participantId:guid}")]
    public async Task<ActionResult<OperationResult>> RemovePendingInvitation(
        Guid listId, Guid participantId,
        [FromServices] IListSharingService sharingService,
        CancellationToken token)
    {
        var userId = ResolveUserId();
        var (success, message) = await sharingService.RemovePendingInvitationAsync(userId, listId, participantId);
        return Ok(new OperationResult(success, message));
    }

    /* -------- Dashboards -------- */

    [HttpGet("dashboards")]
    public async Task<ActionResult<IReadOnlyList<DashboardEntity>>> GetDashboards(
        [FromServices] IDashboardService dashboardService,
        CancellationToken token)
    {
        var userId = ResolveUserId();
        return Ok(await dashboardService.GetDashboardsAsync(userId, token));
    }

    [HttpGet("portfolios/{portfolioGroupId:guid}/dashboard")]
    public async Task<ActionResult<DashboardEntity>> GetPortfolioDashboard(Guid portfolioGroupId,
        [FromServices] IDashboardService dashboardService, CancellationToken token)
    {
        var dashboard = await dashboardService.GetOrCreatePortfolioDashboardAsync(ResolveUserId(), portfolioGroupId, token);
        return dashboard is null ? NotFound() : Ok(dashboard);
    }

    [HttpPost("portfolios/{portfolioGroupId:guid}/invite")]
    public async Task<ActionResult<PortfolioInviteResult>> InvitePortfolio(Guid portfolioGroupId, [FromBody] PortfolioInviteRequest request,
        [FromServices] IPortfolioSharingService sharingService, CancellationToken token)
        => Ok(await sharingService.InviteAsync(ResolveUserId(), portfolioGroupId, request.Email, request.Role, token));

    [HttpGet("portfolios/{portfolioGroupId:guid}/can-manage")]
    public async Task<ActionResult<bool>> CanManagePortfolio(Guid portfolioGroupId,
        [FromServices] IPortfolioSharingService sharingService, CancellationToken token)
        => Ok(await sharingService.CanManageAsync(ResolveUserId(), portfolioGroupId, token));

    [HttpGet("portfolios/{portfolioGroupId:guid}/share-links")]
    public async Task<ActionResult<IReadOnlyList<ShareLinkInfo>>> GetPortfolioShareLinks(Guid portfolioGroupId, [FromServices] IPortfolioSharingService service, CancellationToken token)
        => Ok(await service.GetShareLinksAsync(ResolveUserId(), portfolioGroupId, token));

    [HttpPost("portfolios/{portfolioGroupId:guid}/share-links")]
    public async Task<ActionResult<ShareLinkResult>> CreatePortfolioShareLink(Guid portfolioGroupId, [FromBody] CreateShareLinkRequest request, [FromServices] IPortfolioSharingService service, CancellationToken token)
    {
        var result = await service.CreateShareLinkAsync(ResolveUserId(), portfolioGroupId, request.Role, request.Comment, token);
        return Ok(new ShareLinkResult(result.Success, result.Message, result.Link));
    }

    [HttpPost("portfolios/{portfolioGroupId:guid}/share-links/accept")]
    public async Task<ActionResult<OperationResult>> AcceptPortfolioShareLink(Guid portfolioGroupId, [FromBody] AcceptShareLinkRequest request, [FromServices] IPortfolioSharingService service, CancellationToken token)
    {
        var result = await service.AcceptAsync(ResolveUserId(), portfolioGroupId, request.Token, token);
        return Ok(new OperationResult(result.Success, result.Message));
    }

    [HttpPut("portfolios/{portfolioGroupId:guid}/share-links/{inviteId:guid}/comment")]
    public async Task<ActionResult<OperationResult>> UpdatePortfolioShareLinkComment(Guid portfolioGroupId, Guid inviteId, [FromBody] UpdateCommentRequest request, [FromServices] IPortfolioSharingService service, CancellationToken token)
    {
        var result = await service.UpdateShareLinkCommentAsync(ResolveUserId(), portfolioGroupId, inviteId, request.Comment, token);
        return Ok(new OperationResult(result.Success, result.Message));
    }

    [HttpDelete("portfolios/{portfolioGroupId:guid}/share-links/{inviteId:guid}")]
    public async Task<ActionResult<OperationResult>> RevokePortfolioShareLink(Guid portfolioGroupId, Guid inviteId, [FromServices] IPortfolioSharingService service, CancellationToken token)
    {
        var result = await service.RevokeShareLinkAsync(ResolveUserId(), portfolioGroupId, inviteId, token);
        return Ok(new OperationResult(result.Success, result.Message));
    }

    [HttpGet("portfolios/{portfolioGroupId:guid}/participants")]
    public async Task<ActionResult<IReadOnlyList<PortfolioParticipantEntity>>> GetPortfolioParticipants(Guid portfolioGroupId, [FromServices] IPortfolioSharingService service, CancellationToken token)
        => Ok(await service.GetParticipantsAsync(ResolveUserId(), portfolioGroupId, token));

    [HttpPut("portfolios/{portfolioGroupId:guid}/participants/{participantId:guid}/role")]
    public async Task<ActionResult<OperationResult>> UpdatePortfolioParticipantRole(Guid portfolioGroupId, Guid participantId, [FromBody] UpdateRoleRequest request, [FromServices] IPortfolioSharingService service, CancellationToken token)
    {
        var result = await service.UpdateParticipantRoleAsync(ResolveUserId(), portfolioGroupId, participantId, request.Role, token);
        return Ok(new OperationResult(result.Success, result.Message));
    }

    [HttpDelete("portfolios/{portfolioGroupId:guid}/participants/{participantId:guid}")]
    public async Task<ActionResult<OperationResult>> RemovePortfolioParticipant(Guid portfolioGroupId, Guid participantId, [FromServices] IPortfolioSharingService service, CancellationToken token)
    {
        var result = await service.RemoveParticipantAsync(ResolveUserId(), portfolioGroupId, participantId, token);
        return Ok(new OperationResult(result.Success, result.Message));
    }

    [HttpPost("dashboards")]
    public async Task<ActionResult<DashboardEntity>> CreateDashboard(
        [FromBody] DashboardEntity dashboard,
        [FromServices] IDashboardService dashboardService,
        CancellationToken token)
    {
        var userId = ResolveUserId();
        return Ok(await dashboardService.CreateDashboardAsync(userId, dashboard, token));
    }

    [HttpPut("dashboards/{dashboardId:guid}")]
    public async Task<ActionResult<DashboardEntity>> UpdateDashboard(
        Guid dashboardId,
        [FromBody] DashboardEntity dashboard,
        [FromServices] IDashboardService dashboardService,
        CancellationToken token)
    {
        var userId = ResolveUserId();
        dashboard.Id = dashboardId;
        var updated = await dashboardService.UpdateDashboardAsync(userId, dashboard, token);
        return updated is null ? NotFound() : Ok(updated);
    }

    [HttpDelete("dashboards/{dashboardId:guid}")]
    public async Task<ActionResult> DeleteDashboard(
        Guid dashboardId,
        [FromServices] IDashboardService dashboardService,
        CancellationToken token)
    {
        var userId = ResolveUserId();
        var ok = await dashboardService.DeleteDashboardAsync(userId, dashboardId, token);
        return ok ? Ok() : NotFound();
    }

    /* -------- Trash -------- */

    [HttpGet("trash/lists")]
    public async Task<ActionResult<IReadOnlyList<TodoListEntity>>> GetDeletedLists(
        [FromServices] ITodoTrashService trashService,
        CancellationToken token)
    {
        var userId = ResolveUserId();
        var lists = await trashService.GetDeletedListsAsync(userId, token);
        return Ok(lists);
    }

    [HttpGet("trash/lists/{listId:guid}/tasks")]
    public async Task<ActionResult<IReadOnlyList<TodoTaskEntity>>> GetDeletedTasks(
        Guid listId,
        [FromServices] ITodoTrashService trashService,
        CancellationToken token)
    {
        var userId = ResolveUserId();
        var tasks = await trashService.GetDeletedTasksAsync(userId, listId, token);
        return Ok(tasks);
    }

    [HttpPost("trash/lists/{listId:guid}/restore")]
    public async Task<ActionResult> RestoreList(
        Guid listId,
        [FromServices] ITodoTrashService trashService,
        CancellationToken token)
    {
        var userId = ResolveUserId();
        var ok = await trashService.RestoreListAsync(userId, listId, token);
        return ok ? Ok() : NotFound();
    }

    [HttpPost("trash/lists/{listId:guid}/tasks/{taskId:guid}/restore")]
    public async Task<ActionResult> RestoreTask(
        Guid listId, Guid taskId,
        [FromServices] ITodoTrashService trashService,
        CancellationToken token)
    {
        var userId = ResolveUserId();
        var ok = await trashService.RestoreTaskAsync(userId, listId, taskId, token);
        return ok ? Ok() : NotFound();
    }

    /* -------- Notifications -------- */

    [HttpGet("notifications")]
    public async Task<ActionResult<IReadOnlyList<UserNotificationEntity>>> GetNotifications(
        [FromServices] INotificationService notificationService,
        CancellationToken token,
        [FromQuery] int take = 20)
    {
        var userId = ResolveUserId();
        return Ok(await notificationService.GetLatestAsync(userId, take, token));
    }

    [HttpGet("notifications/unread-count")]
    public async Task<ActionResult<int>> GetUnreadNotificationCount(
        [FromServices] INotificationService notificationService,
        CancellationToken token)
    {
        var userId = ResolveUserId();
        return Ok(await notificationService.GetUnreadCountAsync(userId, token));
    }

    [HttpPost("notifications/mark-read")]
    public async Task<ActionResult> MarkNotificationsRead(
        [FromServices] INotificationService notificationService,
        CancellationToken token)
    {
        var userId = ResolveUserId();
        await notificationService.MarkAllReadAsync(userId, token);
        return Ok();
    }

    [HttpDelete("notifications/{notificationId:guid}")]
    public async Task<ActionResult> DeleteNotification(
        Guid notificationId,
        [FromServices] INotificationService notificationService,
        CancellationToken token)
    {
        var userId = ResolveUserId();
        await notificationService.DeleteNotificationAsync(userId, notificationId, token);
        return Ok();
    }

    [HttpDelete("notifications")]
    public async Task<ActionResult> DeleteAllNotifications(
        [FromServices] INotificationService notificationService,
        CancellationToken token)
    {
        var userId = ResolveUserId();
        await notificationService.DeleteAllNotificationsAsync(userId, token);
        return Ok();
    }

    [HttpGet("notification-preference")]
    public async Task<ActionResult<UserNotificationPreferenceEntity>> GetNotificationPreference(
        [FromServices] INotificationService notificationService,
        CancellationToken token)
    {
        var userId = ResolveUserId();
        return Ok(await notificationService.GetUserPreferenceAsync(userId, token));
    }

    [HttpPut("notification-preference")]
    public async Task<ActionResult> SetNotificationPreference(
        [FromBody] NotificationPreferenceRequest request,
        [FromServices] INotificationService notificationService,
        CancellationToken token)
    {
        var userId = ResolveUserId();
        await notificationService.SetUserPreferenceAsync(userId, request.Channel, request.PushContentMode, token);
        return Ok();
    }

    [HttpGet("lists/{listId:guid}/notification-rules")]
    public async Task<ActionResult<IReadOnlyList<BoardNotificationRuleEntity>>> GetBoardNotificationRules(
        Guid listId,
        [FromServices] INotificationService notificationService,
        CancellationToken token)
    {
        var userId = ResolveUserId();
        return Ok(await notificationService.GetBoardRulesAsync(userId, listId, token));
    }

    [HttpPut("lists/{listId:guid}/notification-rules/{eventType}")]
    public async Task<ActionResult> SetBoardNotificationRule(
        Guid listId,
        NotificationEventType eventType,
        [FromBody] BoardNotificationRuleRequest request,
        [FromServices] INotificationService notificationService,
        CancellationToken token)
    {
        var userId = ResolveUserId();
        await notificationService.SetBoardRuleAsync(userId, listId, eventType, request.Groups, token);
        return Ok();
    }

    [HttpGet("lists/{listId:guid}/email-import")]
    public async Task<ActionResult<ListEmailImportConfigurationDto?>> GetEmailImportConfiguration(
        Guid listId,
        [FromServices] IListEmailImportService emailImportService,
        CancellationToken token)
    {
        var userId = ResolveUserId();
        return Ok(await emailImportService.GetConfigurationAsync(userId, listId, token));
    }

    [HttpPut("lists/{listId:guid}/email-import")]
    public async Task<ActionResult> SaveEmailImportConfiguration(
        Guid listId,
        [FromBody] ListEmailImportSaveRequest request,
        [FromServices] IListEmailImportService emailImportService,
        CancellationToken token)
    {
        var userId = ResolveUserId();
        await emailImportService.SaveConfigurationAsync(userId, listId, request, token);
        return Ok();
    }

    [HttpDelete("lists/{listId:guid}/email-import")]
    public async Task<ActionResult> DeleteEmailImportConfiguration(
        Guid listId,
        [FromServices] IListEmailImportService emailImportService,
        CancellationToken token)
    {
        var userId = ResolveUserId();
        await emailImportService.DeleteConfigurationAsync(userId, listId, token);
        return Ok();
    }

    [HttpPost("lists/{listId:guid}/email-import/test")]
    public async Task<ActionResult<EmailImportConnectionTestResult>> TestEmailImportConnection(
        Guid listId,
        [FromBody] ListEmailImportSaveRequest request,
        [FromServices] IListEmailImportService emailImportService,
        CancellationToken token)
    {
        var userId = ResolveUserId();
        return Ok(await emailImportService.TestConnectionAsync(userId, listId, request, token));
    }

    [HttpPost("lists/{listId:guid}/email-import/run")]
    public async Task<ActionResult<EmailImportRunResult>> RunEmailImport(
        Guid listId,
        [FromServices] IListEmailImportService emailImportService,
        CancellationToken token)
    {
        var userId = ResolveUserId();
        return Ok(await emailImportService.ImportListAsync(userId, listId, token));
    }

    [HttpGet("lists/{listId:guid}/forms")]
    public async Task<ActionResult<IReadOnlyList<TodoFormEntity>>> GetForms(
        Guid listId,
        [FromServices] ITodoFormService formService,
        CancellationToken token)
    {
        var userId = ResolveUserId();
        return Ok(await formService.GetFormsAsync(userId, listId, token));
    }

    [HttpGet("forms/{formId:guid}")]
    public async Task<ActionResult<TodoFormEntity>> GetFormForEdit(
        Guid formId,
        [FromServices] ITodoFormService formService,
        CancellationToken token)
    {
        var userId = ResolveUserId();
        var form = await formService.GetFormForEditAsync(userId, formId, token);
        return form is null ? NotFound() : Ok(form);
    }

    [HttpPost("lists/{listId:guid}/forms")]
    public async Task<ActionResult<TodoFormEntity>> CreateForm(
        Guid listId,
        [FromBody] CreateTodoFormRequest request,
        [FromServices] ITodoFormService formService,
        CancellationToken token)
    {
        var userId = ResolveUserId();
        return Ok(await formService.CreateFormAsync(userId, listId, request.Name, token));
    }

    [HttpPut("forms/{formId:guid}")]
    public async Task<ActionResult<TodoFormEntity>> SaveForm(
        Guid formId,
        [FromBody] SaveTodoFormRequest request,
        [FromServices] ITodoFormService formService,
        CancellationToken token)
    {
        var userId = ResolveUserId();
        request.Form.Id = formId;
        var saved = await formService.SaveFormAsync(userId, request.Form, request.PlainPassword, token);
        return saved is null ? NotFound() : Ok(saved);
    }

    [HttpDelete("forms/{formId:guid}")]
    public async Task<ActionResult> DeleteForm(
        Guid formId,
        [FromServices] ITodoFormService formService,
        CancellationToken token)
    {
        var userId = ResolveUserId();
        return await formService.DeleteFormAsync(userId, formId, token) ? Ok() : NotFound();
    }

    /* -------- DTOs -------- */

    public record CreateListFromTemplateRequest(string Name);
    public record AddCommentRequest(string Message, Guid? Id = null);
    public record GroupNameRequest(string Name, bool IsPortfolio = false, Guid? Id = null);
    public record GroupPortfolioRequest(bool IsPortfolio);
    public record GroupCollapsedRequest(bool IsCollapsed);
    public record PortfolioInviteRequest(string Email, ListRole Role);
    public record ReorderKanbanTasksRequest(string Column, IReadOnlyList<Guid> OrderedTaskIds);
    public record ReorderNavigationListsRequest(Guid? GroupId, IReadOnlyList<Guid> OrderedListIds);
    public record MoveListRequest(Guid? FromGroupId, Guid? ToGroupId, IReadOnlyList<Guid> FromOrderedIds, IReadOnlyList<Guid> ToOrderedIds);
    public record AcceptShareLinkRequest(string Token);
    public record CreateShareLinkRequest(ListRole Role, string? Comment);
    public record UpdateCommentRequest(string? Comment);
    public record InviteByEmailRequest(string Email, string DisplayName, ListRole Role);
    public record UpdateRoleRequest(ListRole Role);
    public record ShareLinkResult(bool Success, string Message, string? Link);
    public record OperationResult(bool Success, string Message);
    public record NotificationPreferenceRequest(NotificationDeliveryChannel Channel, PushNotificationContentMode? PushContentMode = null);
    public record BoardNotificationRuleRequest(NotificationRecipientGroup Groups);
    public record SetDoneColumnRequest(bool IsDone);
    public record SetWatchingRequest(bool Watching);
    public record ApprovalDecisionRequest(bool Approved);
    public record MoveTaskRequest(Guid ToListId, string? DesiredTargetColumn);
    public record LabelRequest(string Title, string? BackgroundColor, Guid? Id = null);
    public record CreateTodoFormRequest(string Name);
    public record SaveTodoFormRequest(TodoFormEntity Form, string? PlainPassword);

    [HttpGet("search")]
    public async Task<ActionResult<IReadOnlyList<SearchResultItem>>> Search(
        [FromQuery] string? q,
        [FromServices] ISearchService searchService, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(q))
            return Ok(Array.Empty<SearchResultItem>());

        var userId = ResolveUserId();
        var results = await searchService.SearchAsync(userId, q, token);
        return Ok(results);
    }

    private static void ApplySyncTokens(IEnumerable<TodoListEntity> lists)
    {
        foreach (var list in lists)
        {
            list.SyncToken = MobileSyncFingerprint.ForList(list);
            list.SyncVersion = list.ContentVersion;
            foreach (var task in list.Tasks ?? [])
            {
                task.SyncToken = MobileSyncFingerprint.ForTask(task);
                task.SyncVersion = task.ContentVersion;
            }
        }
    }

    private static bool HasSyncConflict(string? suppliedToken, string currentToken)
        => !string.IsNullOrWhiteSpace(suppliedToken)
           && !CryptographicOperations.FixedTimeEquals(
               Encoding.UTF8.GetBytes(suppliedToken),
               Encoding.UTF8.GetBytes(currentToken));

    private static string CreateWorkspaceEtag(IEnumerable<TodoListEntity> lists)
    {
        // Hash the complete response graph. Comments, attachments, watchers, labels, custom fields
        // and user-specific navigation/table preferences are intentionally part of the ETag even
        // though they are excluded from full-update conflict fingerprints.
        var ordered = lists.OrderBy(list => list.Id).ToList();
        var payload = JsonSerializer.SerializeToUtf8Bytes(ordered, WorkspaceEtagJson);
        return $"\"{Convert.ToHexString(SHA256.HashData(payload))}\"";
    }

    private string ResolveUserId()
    {
        return User.FindFirstValue(ClaimTypes.NameIdentifier)
               ?? User.FindFirstValue("sub")
               ?? User.FindFirstValue(ClaimTypes.Email)
               ?? "gast";
    }

    private string? ResolveUserEmail()
    {
        return User.FindFirstValue(ClaimTypes.Email)
               ?? User.FindFirstValue("email")
               ?? User.Identity?.Name;
    }
}
