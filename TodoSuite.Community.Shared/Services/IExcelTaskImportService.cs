using Klassenbibliothek.Data;

namespace Klassenbibliothek.Services;

public sealed record ExcelImportResult(int Count, bool FollowUpFailed);
public sealed record ExcelImportRequest(ExcelImportSheet Sheet, bool Date1904,
    IReadOnlyList<ExcelFieldMapping> Mappings, Guid ImportId);

/// <summary>Uses the same import dialog and validation rules in web and mobile clients.</summary>
public interface IExcelTaskImportService
{
    Task<IReadOnlyList<TodoTaskEntity>> ValidateExcelImportAsync(string userId, Guid listId,
        ExcelImportSheet sheet, bool date1904, IReadOnlyList<ExcelFieldMapping> mappings);
    Task<ExcelImportResult> ImportExcelAsync(string userId, Guid listId,
        ExcelImportSheet sheet, bool date1904, IReadOnlyList<ExcelFieldMapping> mappings, Guid? importId = null);
}
