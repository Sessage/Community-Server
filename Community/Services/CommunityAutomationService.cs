using Klassenbibliothek.Data;
using Klassenbibliothek.Services;

namespace TodoSuite.Server.Services;

/// <summary>No-op event sink used by Community so core task operations stay independent of Enterprise automation.</summary>
public sealed class CommunityAutomationService : ITodoAutomationService
{
    public Task<IReadOnlyList<TodoAutomationRuleEntity>> GetRulesAsync(string userId, Guid listId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<TodoAutomationRuleEntity>>([]);
    public Task<TodoAutomationRuleEntity> SaveRuleAsync(string userId, Guid listId, TodoAutomationRuleEntity rule, CancellationToken cancellationToken = default)
        => Task.FromException<TodoAutomationRuleEntity>(Unavailable());
    public Task<bool> DeleteRuleAsync(string userId, Guid listId, Guid ruleId, CancellationToken cancellationToken = default)
        => Task.FromException<bool>(Unavailable());
    public Task SetRuleEnabledAsync(string userId, Guid listId, Guid ruleId, bool enabled, CancellationToken cancellationToken = default)
        => Task.FromException(Unavailable());
    public Task ExecuteAsync(TodoAutomationContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    private static InvalidOperationException Unavailable() => new("Automatisierungen sind ein Enterprise-Modul.");
}
