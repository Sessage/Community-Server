using Klassenbibliothek.Data;
using Klassenbibliothek.Services;
using Klassenbibliothek.AutomationPlugins;

namespace TodoSuite.Server.Services;

/// <summary>
/// Community-edition implementation of the automation contract.
/// It preserves a stable client-facing API while reporting Enterprise-only operations as unavailable.
/// </summary>
public sealed class CommunityAutomationService : ITodoAutomationService
{
    public Task<IReadOnlyList<AutomationPluginActionDescriptor>> GetPluginActionsAsync(string userId, Guid listId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<AutomationPluginActionDescriptor>>([]);
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
