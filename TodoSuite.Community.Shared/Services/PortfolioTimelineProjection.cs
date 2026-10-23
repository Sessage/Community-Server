using Klassenbibliothek.Data;

namespace Klassenbibliothek.Services;

/// <summary>Builds read-only timeline rows from all active tasks in each portfolio list.</summary>
public static class PortfolioTimelineProjection
{
    public static TodoListEntity Build(Guid portfolioId, string name,
        IEnumerable<TodoListEntity> lists, IEnumerable<Guid> portfolioListIds)
    {
        var selected = portfolioListIds.ToHashSet();
        return new TodoListEntity
        {
            Id = portfolioId,
            Name = name,
            Tasks = lists.Where(list => selected.Contains(list.Id) && list.DeletedAt is null && !list.IsTemplate)
                .OrderBy(list => list.NavigationSortOrder)
                .ThenBy(list => list.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select((list, index) =>
                {
                    var tasks = list.Tasks.Where(task => task.DeletedAt is null).ToList();
                    return new TodoTaskEntity
                    {
                        Id = list.Id,
                        ListId = list.Id,
                        Title = list.Name,
                        StartDate = tasks.Select(task => task.StartDate).Min(),
                        DueDate = tasks.Select(task => task.DueDate).Max(),
                        CardColor = list.BackgroundColor,
                        ListSortOrder = index
                    };
                }).ToList()
        };
    }
}
