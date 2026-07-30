namespace Klassenbibliothek.Data;

public enum DefaultListView
{
    Liste = 0,
    Kanban = 1,
    Calendar = 2,
    Tabelle = 3,
    Forms = 4
}

public enum ListRole
{
    Admin = 0,
    Member = 1,
    Observer = 2
}

public enum ListSortMode
{
    Custom = 0,       // Drag&Drop / SortOrder-Felder
    Importance = 1,
    DueDate = 2,
    Alphabetical = 3,
    CreatedAt = 4
}
