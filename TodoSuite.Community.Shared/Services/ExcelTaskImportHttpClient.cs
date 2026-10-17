using Klassenbibliothek.Localization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Klassenbibliothek.Data;

namespace Klassenbibliothek.Services;

/// <summary>Online-only import transport. Import confirmation is never placed in the offline queue.</summary>
public sealed class ExcelTaskImportHttpClient(HttpClient http, Func<string> serverUrl, Func<bool> isOnline)
    : IExcelTaskImportService, IDisposable
{
    private string? _validatedServer;
    private string? _validatedUser;
    public async Task<IReadOnlyList<TodoTaskEntity>> ValidateExcelImportAsync(string userId, Guid listId,
        ExcelImportSheet sheet, bool date1904, IReadOnlyList<ExcelFieldMapping> mappings)
    {
        _validatedServer = null;
        _validatedUser = null;
        var server = serverUrl().TrimEnd('/');
        var tasks = await Send<List<TodoTaskEntity>>(server, listId, "validate", new(sheet, date1904, mappings, Guid.NewGuid()));
        _validatedServer = server;
        _validatedUser = userId;
        return tasks;
    }

    public Task<ExcelImportResult> ImportExcelAsync(string userId, Guid listId,
        ExcelImportSheet sheet, bool date1904, IReadOnlyList<ExcelFieldMapping> mappings, Guid? importId = null)
    {
        var server = serverUrl().TrimEnd('/');
        if (server != _validatedServer || userId != _validatedUser)
            throw new IOException(SharedText.Get("ExcelImport_ProfileChanged"));
        return Send<ExcelImportResult>(server, listId, "", new(sheet, date1904, mappings, importId ?? Guid.NewGuid()));
    }

    private async Task<T> Send<T>(string server, Guid listId, string suffix, ExcelImportRequest request)
    {
        if (!isOnline()) throw new IOException(SharedText.Get("ExcelImport_Offline"));
        // Capture this operation's origin once; never send a caller-supplied user identity.
        var url = server + $"/api/mobile/lists/{listId}/excel-import" + (suffix.Length > 0 ? "/" + suffix : "");
        using var messageRequest = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(request)
        };
        messageRequest.Headers.AcceptLanguage.ParseAdd(System.Globalization.CultureInfo.CurrentUICulture.Name is { Length: > 0 } language ? language : "de");
        using var response = await http.SendAsync(messageRequest);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new IOException(SharedText.Get("ExcelImport_ServerUpgrade"));
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                throw new UnauthorizedAccessException(SharedText.Get("ExcelImport_Forbidden"));
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict)
            {
                try
                {
                    var error = await response.Content.ReadFromJsonAsync<JsonElement>();
                    if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("error", out var message) && message.ValueKind == JsonValueKind.String)
                        throw new ArgumentException(message.GetString());
                }
                catch (JsonException) { }
            }
            throw new IOException(SharedText.Get("ExcelImport_HttpError", (int)response.StatusCode));
        }
        return await response.Content.ReadFromJsonAsync<T>() ?? throw new IOException(SharedText.Get("ExcelImport_InvalidResponse"));
    }

    public void Dispose() => http.Dispose();
}
