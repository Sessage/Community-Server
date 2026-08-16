using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Klassenbibliothek.Data;
using Klassenbibliothek.Services;
using Klassenbibliothek.Hubs;

namespace TodoSuite.Server.Services;

/// <summary>
/// Stellt gemeinsame Abhängigkeiten und Hilfsfunktionen für die Workspace-Services bereit.
/// </summary>
public abstract class TodoWorkspaceServiceBase
{
    /// <summary>
    /// Initialisiert die gemeinsamen Abhängigkeiten der Workspace-Services.
    /// </summary>
    protected TodoWorkspaceServiceBase(
        IDbContextFactory<ApplicationDbContext> dbContextFactory,
        IHubContext<TodoHubEndpoint> hubContext,
        IWebHostEnvironment env,
        ITaskMemberService taskMemberService)
    {
        DbContextFactory = dbContextFactory;
        HubContext = hubContext;
        Env = env;
        TaskMemberService = taskMemberService;
    }

    /// <summary>
    /// Liefert die DbContextFactory zur Erzeugung von ApplicationDbContext Instanzen.
    /// </summary>
    protected IDbContextFactory<ApplicationDbContext> DbContextFactory { get; }

    /// <summary>
    /// Zugriff auf den SignalR Hub für Echtzeit-Benachrichtigungen.
    /// </summary>
    protected IHubContext<TodoHubEndpoint> HubContext { get; }

    /// <summary>
    /// Zugriff auf die Hosting-Umgebung für Dateisystempfade.
    /// </summary>
    protected IWebHostEnvironment Env { get; }

    /// <summary>
    /// Service zur Verwaltung von Aufgaben-Mitgliedern.
    /// </summary>
    protected ITaskMemberService TaskMemberService { get; }

    /// <summary>
    /// Benachrichtigt Clients über geänderte Listen-Daten.
    /// </summary>
    protected Task NotifyListUpdatedAsync(Guid listId, CancellationToken ct = default)
        => HubContext.Clients
            .Group(TodoHub.ListGroup(listId))
            .SendAsync(TodoHub.ListsUpdated, listId, cancellationToken: ct);

    /// <summary>
    /// Benachrichtigt alle Teilnehmer einer Liste über Änderungen an der Navigationsliste,
    /// damit deren NavMenu die Ansicht aktualisiert.
    /// </summary>
    protected Task NotifyParticipantsListsUpdatedAsync(TodoListEntity list, CancellationToken ct = default)
    {
        var userIds = (list.Participants ?? new List<ListParticipantEntity>())
            .Select(p => p.UserId)
            .Append(list.OwnerId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (userIds.Count == 0)
            return Task.CompletedTask;

        return Task.WhenAll(userIds.Select(uid =>
            HubContext.Clients
                .Group(TodoHub.UserGroup(uid!))
                .SendAsync(TodoHub.ListsUpdated, cancellationToken: ct)));
    }

    /// <summary>
    /// Benachrichtigt Clients über Änderungen an Kommentaren/Anhängen einer Aufgabe.
    /// </summary>
    protected Task NotifyTaskUpdatesAsync(Guid listId, Guid taskId, CancellationToken ct = default)
        => Task.WhenAll(
            HubContext.Clients
                .Group(TodoHub.ListGroup(listId))
                .SendAsync(TodoHub.TaskCommentsUpdated, listId, taskId, cancellationToken: ct),
            HubContext.Clients
                .Group(TodoHub.ListGroup(listId))
                .SendAsync(TodoHub.TaskAttachmentsUpdated, listId, taskId, cancellationToken: ct)
        );

    /// <summary>
    /// Prüft, ob der Benutzer eine Liste lesen darf.
    /// </summary>
    protected static bool CanRead(string userId, TodoListEntity list)
        => EqualsUserKey(list.OwnerId, userId)
           || list.Participants.Any(p => !p.InvitationPending && (EqualsUserKey(p.Email, userId) || EqualsUserKey(p.UserId, userId)));

    /// <summary>
    /// Prüft, ob der Benutzer in der Liste schreiben darf.
    /// Beobachter (Observer) dürfen nichts ändern.
    /// </summary>
    protected static bool CanWrite(string userId, TodoListEntity list)
    {
        if (EqualsUserKey(list.OwnerId, userId)) return true;

        var p = list.Participants.FirstOrDefault(x => !x.InvitationPending && (EqualsUserKey(x.Email, userId) || EqualsUserKey(x.UserId, userId)));
        if (p is null) return false;

        return p.Role != ListRole.Observer;
    }

    /// <summary>
    /// Prüft, ob der Benutzer Admin-Rechte in der Liste besitzt.
    /// </summary>
    protected static bool CanAdmin(string userId, TodoListEntity list)
    {
        if (EqualsUserKey(list.OwnerId, userId))
            return true;

        if (list.Participants?.Any(p => !p.InvitationPending && (EqualsUserKey(p.UserId, userId) || EqualsUserKey(p.Email, userId)) && p.Role == ListRole.Admin) == true)
            return true;

        return false;
    }

    /// <summary>
    /// Lädt das Nutzerprofil für die Owner-Participant-Synchronisierung.
    /// </summary>
    protected static async Task<(string? Email, string? DisplayName)> GetUserProfileAsync(
        ApplicationDbContext db,
        string userId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return (null, null);

        var u = await db.Users
            .AsNoTracking()
            .Where(x => x.Id == userId)
            .Select(x => new { x.Email, x.UserName, x.DisplayName })
            .FirstOrDefaultAsync(ct);

        if (u is null) return (null, null);

        var email = (u.Email ?? "").Trim();
        var name = (u.DisplayName ?? u.UserName ?? "").Trim();

        return (string.IsNullOrWhiteSpace(email) ? null : email,
                string.IsNullOrWhiteSpace(name) ? null : name);
    }

    /// <summary>
    /// Vergleicht E-Mail-Adressen robust (Trim + IgnoreCase).
    /// </summary>
    protected static bool EqualsEmail(string? a, string? b)
        => string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Vergleicht User-Keys (UserId oder E-Mail) robust (Trim + IgnoreCase).
    /// </summary>
    protected static bool EqualsUserKey(string? a, string? b)
        => string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Stellt sicher, dass der Owner als Admin-Participant geführt wird.
    /// </summary>
    protected static async Task EnsureOwnerParticipantAdminAsync(
        ApplicationDbContext db,
        TodoListEntity list,
        string ownerUserId,
        CancellationToken ct)
    {
        list.Participants ??= new List<ListParticipantEntity>();

        var (email, displayName) = await GetUserProfileAsync(db, ownerUserId, ct);

        var ownerParticipant = list.Participants.FirstOrDefault(p =>
            string.Equals(p.UserId, ownerUserId, StringComparison.OrdinalIgnoreCase)
            || (email is not null && EqualsEmail(p.Email, email)));

        if (ownerParticipant is null)
        {
            var newOwnerParticipant = new ListParticipantEntity
            {
                Id = Guid.NewGuid(),
                ListId = list.Id,
                UserId = ownerUserId,
                Email = email ?? "",
                DisplayName = !string.IsNullOrWhiteSpace(displayName) ? displayName : (email ?? ownerUserId),
                InvitationPending = false,
                DirectInvitationPending = false,
                Role = ListRole.Admin,
                DirectRole = ListRole.Admin
            };
            list.Participants.Add(newOwnerParticipant);
            db.ListParticipants.Add(newOwnerParticipant);
        }
        else
        {
            ownerParticipant.UserId = ownerUserId;
            if (!string.IsNullOrWhiteSpace(email)) ownerParticipant.Email = email;
            if (!string.IsNullOrWhiteSpace(displayName)) ownerParticipant.DisplayName = displayName;

            PortfolioAccessCoordinator.SetDirectAccess(ownerParticipant, ListRole.Admin, invitationPending: false);
            ownerParticipant.ListId = list.Id;
        }
    }

    /// <summary>
    /// Liefert das Upload-Verzeichnis für Anhänge.
    /// </summary>
    protected string UploadRoot
    {
        get
        {
            var path = Path.Combine(Env.ContentRootPath, "uploads");
            Directory.CreateDirectory(path);
            return path;
        }
    }

    /// <summary>
    /// Bereinigt Dateinamen für sichere Speicherung.
    /// </summary>
    protected static string SafeFileName(string fileName)
    {
        fileName = (fileName ?? "").Trim();
        foreach (var c in Path.GetInvalidFileNameChars())
            fileName = fileName.Replace(c, '_');
        return string.IsNullOrWhiteSpace(fileName) ? "datei" : fileName;
    }
}
