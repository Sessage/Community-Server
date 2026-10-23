using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using System.Reflection.Emit;
using Klassenbibliothek.Data;

namespace Klassenbibliothek.Data;

/// <summary>
/// EF Core unit-of-work for identity and workspace data. Enterprise tables deliberately live
/// in the same model so installing/removing a license changes behavior without destructive
/// schema changes; the licensing administration uses a separate context and database.
/// </summary>
public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
    : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<DailyFocusPreferenceEntity> DailyFocusPreferences => Set<DailyFocusPreferenceEntity>();
    public DbSet<DailyFocusSelectionEntity> DailyFocusSelections => Set<DailyFocusSelectionEntity>();
    public DbSet<TodoListEntity> TodoLists => Set<TodoListEntity>();
    public DbSet<TodoTaskEntity> TodoTasks => Set<TodoTaskEntity>();
    public DbSet<TodoAttachmentEntity> TodoAttachments => Set<TodoAttachmentEntity>();
    public DbSet<TodoStepEntity> TodoSteps => Set<TodoStepEntity>();
    public DbSet<TodoCommentEntity> TodoComments => Set<TodoCommentEntity>();
    public DbSet<ListParticipantEntity> ListParticipants => Set<ListParticipantEntity>();

    public DbSet<ListViewPreferenceEntity> ListViewPreferences => Set<ListViewPreferenceEntity>();
    public DbSet<TodoListNavigationPreferenceEntity> TodoListNavigationPreferences => Set<TodoListNavigationPreferenceEntity>();
    public DbSet<ListInviteEntity> ListInvites => Set<ListInviteEntity>();

    public DbSet<TodoLabelEntity> TodoLabels => Set<TodoLabelEntity>();
    public DbSet<TodoTaskLabelEntity> TodoTaskLabels => Set<TodoTaskLabelEntity>();
    public DbSet<TodoCustomFieldDefinitionEntity> TodoCustomFields => Set<TodoCustomFieldDefinitionEntity>();
    public DbSet<TodoCustomFieldOptionEntity> TodoCustomFieldOptions => Set<TodoCustomFieldOptionEntity>();
    public DbSet<TodoTaskCustomFieldValueEntity> TodoTaskCustomFieldValues => Set<TodoTaskCustomFieldValueEntity>();

    public DbSet<TodoListGroupEntity> TodoListGroups => Set<TodoListGroupEntity>();
    public DbSet<TodoListGroupPreferenceEntity> TodoListGroupPreferences => Set<TodoListGroupPreferenceEntity>();
    public DbSet<PortfolioParticipantEntity> PortfolioParticipants => Set<PortfolioParticipantEntity>();
    public DbSet<PortfolioInviteEntity> PortfolioInvites => Set<PortfolioInviteEntity>();
    public DbSet<PortfolioListEntity> PortfolioLists => Set<PortfolioListEntity>();
    public DbSet<DirectoryIdentityEntity> DirectoryIdentities => Set<DirectoryIdentityEntity>();
    public DbSet<DirectoryShareGrantEntity> DirectoryShareGrants => Set<DirectoryShareGrantEntity>();
    public DbSet<DirectoryInvitationDeliveryEntity> DirectoryInvitationDeliveries => Set<DirectoryInvitationDeliveryEntity>();

    public DbSet<TodoTaskMemberEntity> TodoTaskMembers => Set<TodoTaskMemberEntity>();
    public DbSet<TodoListWatcherEntity> TodoListWatchers => Set<TodoListWatcherEntity>();
    public DbSet<TodoTaskWatcherEntity> TodoTaskWatchers => Set<TodoTaskWatcherEntity>();
    public DbSet<BoardNotificationRuleEntity> BoardNotificationRules => Set<BoardNotificationRuleEntity>();
    public DbSet<UserNotificationPreferenceEntity> UserNotificationPreferences => Set<UserNotificationPreferenceEntity>();
    public DbSet<UserNotificationEntity> UserNotifications => Set<UserNotificationEntity>();

    public DbSet<PersonalAccessTokenEntity> PersonalAccessTokens => Set<PersonalAccessTokenEntity>();
    public DbSet<MobileRefreshTokenEntity> MobileRefreshTokens => Set<MobileRefreshTokenEntity>();

    public DbSet<DashboardEntity> Dashboards => Set<DashboardEntity>();
    public DbSet<TodoFormEntity> TodoForms => Set<TodoFormEntity>();
    public DbSet<TodoFormFieldEntity> TodoFormFields => Set<TodoFormFieldEntity>();
    public DbSet<TodoFormSubmissionKeyEntity> TodoFormSubmissionKeys => Set<TodoFormSubmissionKeyEntity>();
    public DbSet<ListEmailImportConfigurationEntity> ListEmailImportConfigurations => Set<ListEmailImportConfigurationEntity>();
    public DbSet<ListEmailImportedMessageEntity> ListEmailImportedMessages => Set<ListEmailImportedMessageEntity>();
    public DbSet<TodoAutomationRuleEntity> TodoAutomationRules => Set<TodoAutomationRuleEntity>();
    public DbSet<TodoAutomationConditionEntity> TodoAutomationConditions => Set<TodoAutomationConditionEntity>();
    public DbSet<TodoAutomationActionEntity> TodoAutomationActions => Set<TodoAutomationActionEntity>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        var (tasks, lists) = ChangedAggregateIds();
        if (tasks.Count > 0) _ = TodoTasks.Where(task => tasks.Contains(task.Id)).ToList();
        if (lists.Count > 0) _ = TodoLists.Where(list => lists.Contains(list.Id)).ToList();
        AdvanceRevisions(tasks, lists);
        AdvanceDashboardRevisions();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        var (tasks, lists) = ChangedAggregateIds();
        if (tasks.Count > 0)
            _ = await TodoTasks.Where(task => tasks.Contains(task.Id)).ToListAsync(cancellationToken);
        if (lists.Count > 0)
            _ = await TodoLists.Where(list => lists.Contains(list.Id)).ToListAsync(cancellationToken);
        AdvanceRevisions(tasks, lists);
        AdvanceDashboardRevisions();
        return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private (HashSet<Guid> Tasks, HashSet<Guid> Lists) ChangedAggregateIds()
    {
        ChangeTracker.DetectChanges();
        var tasks = new HashSet<Guid>();
        var lists = new HashSet<Guid>();
        foreach (var entry in ChangeTracker.Entries().Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
        {
            if (entry.Entity is TodoTaskEntity reminderTask && entry.State == EntityState.Modified
                && entry.Property(nameof(TodoTaskEntity.ReminderAtUtc)).IsModified)
            {
                reminderTask.ReminderInboxSentAtUtc = null;
                reminderTask.ReminderEmailSentAtUtc = null;
                reminderTask.ReminderSentAtUtc = null;
            }
            // Comments, files, watchers and delivery markers have independent endpoints.
            // They must not invalidate a task draft, unlike steps, labels and membership.
            switch (entry.Entity)
            {
                case TodoStepEntity step: tasks.Add(step.TaskId); break;
                case TodoTaskMemberEntity member: tasks.Add(member.TaskId); break;
                case TodoTaskLabelEntity label: tasks.Add(label.TaskId); break;
                case TodoTaskCustomFieldValueEntity value: tasks.Add(value.TaskId); break;
                case ListParticipantEntity participant: lists.Add(participant.ListId); break;
                case TodoTaskEntity task when entry.State == EntityState.Modified
                    && entry.Properties.Any(property => property.IsModified && property.Metadata.Name is not nameof(TodoTaskEntity.ReminderSentAtUtc)
                        and not nameof(TodoTaskEntity.ReminderInboxSentAtUtc) and not nameof(TodoTaskEntity.ReminderEmailSentAtUtc)):
                    tasks.Add(task.Id); break;
                case TodoListEntity list when entry.State == EntityState.Modified: lists.Add(list.Id); break;
            }
        }
        return (tasks, lists);
    }

    private void AdvanceRevisions(HashSet<Guid> taskIds, HashSet<Guid> listIds)
    {
        // Advancing the parent and its children in one SaveChanges makes the existing EF
        // concurrency token cover every write path, including narrowly scoped services.
        foreach (var entry in ChangeTracker.Entries<TodoTaskEntity>().Where(entry => taskIds.Contains(entry.Entity.Id)))
            if (entry.State is not EntityState.Added and not EntityState.Deleted
                && entry.Entity.ContentVersion == entry.Property(task => task.ContentVersion).OriginalValue)
                entry.Entity.ContentVersion = checked(entry.Entity.ContentVersion + 1);
        foreach (var entry in ChangeTracker.Entries<TodoListEntity>().Where(entry => listIds.Contains(entry.Entity.Id)))
            if (entry.State is not EntityState.Added and not EntityState.Deleted
                && entry.Entity.ContentVersion == entry.Property(list => list.ContentVersion).OriginalValue)
                entry.Entity.ContentVersion = checked(entry.Entity.ContentVersion + 1);
    }

    private void AdvanceDashboardRevisions()
    {
        foreach (var entry in ChangeTracker.Entries<DashboardEntity>().Where(entry => entry.State == EntityState.Modified))
            if (entry.Entity.ContentVersion == entry.Property(dashboard => dashboard.ContentVersion).OriginalValue)
                entry.Entity.ContentVersion = checked(entry.Entity.ContentVersion + 1);
    }


    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<DashboardEntity>().Property(dashboard => dashboard.ContentVersion).IsConcurrencyToken();

        builder.Entity<DailyFocusPreferenceEntity>(entity =>
        {
            entity.HasKey(x => x.UserId);
            entity.Property(x => x.TimeZoneId).HasMaxLength(128);
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<DailyFocusSelectionEntity>(entity =>
        {
            entity.HasKey(x => new { x.UserId, x.Date, x.TaskId });
            entity.HasOne<DailyFocusPreferenceEntity>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<TodoTaskEntity>().WithMany().HasForeignKey(x => x.TaskId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<ApplicationUser>()
            .Property(user => user.PreferredLanguage)
            .HasMaxLength(16);

        builder.Entity<ApplicationUser>()
            .Property(user => user.PreferredTheme)
            .HasMaxLength(16);

        builder.Entity<ApplicationUser>()
            .Property(user => user.DisplayName)
            .HasMaxLength(200);

        builder.Entity<TodoListEntity>(entity =>
        {
            // ContentVersion is advanced by services/the unit of work and exposed to mobile sync. Database-side
            // generation would make deterministic conflict fingerprints and offline DTOs diverge.
            entity.Property(l => l.ContentVersion).HasDefaultValue(1L).ValueGeneratedNever().IsConcurrencyToken();
            entity.HasIndex(l => new { l.OwnerId, l.Name });
            entity.Property(l => l.Columns).HasColumnType("text[]");
            entity.Property(l => l.DoneColumns).HasColumnType("text[]");

            entity.HasMany(l => l.Tasks)
                .WithOne(t => t.List)
                .HasForeignKey(t => t.ListId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(l => l.Participants)
                .WithOne(p => p.List)
                .HasForeignKey(p => p.ListId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(l => l.Watchers)
                .WithOne(w => w.List)
                .HasForeignKey(w => w.ListId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(l => l.NotificationRules)
                .WithOne(r => r.List)
                .HasForeignKey(r => r.ListId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(l => l.Labels)
                .WithOne(x => x.List)
                .HasForeignKey(x => x.ListId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(l => l.CustomFields)
                .WithOne(x => x.List)
                .HasForeignKey(x => x.ListId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(l => l.AutomationRules)
                .WithOne(x => x.List)
                .HasForeignKey(x => x.ListId)
                .OnDelete(DeleteBehavior.Cascade);

        });

        builder.Entity<TodoListGroupEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(200);

            entity.HasIndex(x => new { x.OwnerId, x.SortOrder });
        });

        builder.Entity<TodoListGroupPreferenceEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.UserId, x.GroupId }).IsUnique();
            entity.HasOne(x => x.Group).WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<PortfolioParticipantEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.PortfolioGroupId, x.Email }).IsUnique();
            entity.HasIndex(x => new { x.PortfolioGroupId, x.UserId });
            entity.HasOne<TodoListGroupEntity>().WithMany().HasForeignKey(x => x.PortfolioGroupId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<PortfolioInviteEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.Token).IsUnique();
            entity.HasOne<TodoListGroupEntity>().WithMany().HasForeignKey(x => x.PortfolioGroupId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<PortfolioListEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.ListId).IsUnique();
            entity.HasIndex(x => new { x.PortfolioGroupId, x.SortOrder });
            entity.HasOne(x => x.PortfolioGroup).WithMany().HasForeignKey(x => x.PortfolioGroupId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.List).WithMany().HasForeignKey(x => x.ListId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<DirectoryIdentityEntity>(entity =>
        {
            entity.HasKey(x => x.UserId);
            // Ein Verzeichnisobjekt darf genau einem lokalen Konto zugeordnet sein. Ohne
            // diese Invariante waere die Anmeldung bei konkurrierenden Erstzugriffen
            // mehrdeutig und koennte Berechtigungen dem falschen Konto zuordnen.
            entity.HasIndex(x => x.PrincipalId).IsUnique();
            entity.Property(x => x.Email).HasMaxLength(512);
            entity.Property(x => x.GroupIds).HasColumnType("text[]");
            entity.HasOne<ApplicationUser>().WithOne().HasForeignKey<DirectoryIdentityEntity>(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<DirectoryShareGrantEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.ResourceType, x.ResourceId });
            entity.HasIndex(x => new { x.ResourceType, x.ResourceId, x.PrincipalType, x.PrincipalId }).IsUnique();
        });

        builder.Entity<DirectoryInvitationDeliveryEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.ResourceType, x.ResourceId, x.UserId }).IsUnique();
            entity.Property(x => x.Email).HasMaxLength(512);
            entity.Property(x => x.LastError).HasMaxLength(2000);
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ListViewPreferenceEntity>(entity =>
        {
            entity.HasIndex(p => new { p.UserId, p.ListId }).IsUnique();
            entity.Property(p => p.TableColumnOrder).HasColumnType("text[]");
            entity.Property(p => p.TableHiddenColumns).HasColumnType("text[]");

            entity.HasOne(p => p.List)
                  .WithMany()
                  .HasForeignKey(p => p.ListId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<TodoListNavigationPreferenceEntity>(entity =>
        {
            entity.HasIndex(p => new { p.UserId, p.ListId }).IsUnique();
            entity.HasIndex(p => new { p.UserId, p.NavigationGroupId, p.NavigationSortOrder });

            entity.HasOne(p => p.List)
                  .WithMany()
                  .HasForeignKey(p => p.ListId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(p => p.NavigationGroup)
                  .WithMany()
                  .HasForeignKey(p => p.NavigationGroupId)
                  .OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<ListParticipantEntity>(entity =>
        {
            entity.HasIndex(p => new { p.ListId, p.Email }).IsUnique();
            entity.HasIndex(p => new { p.ListId, p.UserId }).IsUnique();
        });

        builder.Entity<TodoListWatcherEntity>(entity =>
        {
            entity.HasIndex(w => new { w.ListId, w.UserId }).IsUnique();
        });

        builder.Entity<BoardNotificationRuleEntity>(entity =>
        {
            entity.HasIndex(r => new { r.ListId, r.EventType }).IsUnique();
        });

        builder.Entity<ListInviteEntity>(entity =>
        {
            entity.HasIndex(i => i.Token).IsUnique();

            entity.HasIndex(i => new { i.ListId, i.CreatedAtUtc });

            entity.Property(i => i.Comment).HasMaxLength(200);
            entity.Property(i => i.InviteEmail).HasMaxLength(256);

            entity.HasOne(i => i.List)
                  .WithMany()
                  .HasForeignKey(i => i.ListId)
                  .OnDelete(DeleteBehavior.Cascade);
        });


        builder.Entity<TodoTaskEntity>(entity =>
        {
            entity.Property(t => t.ContentVersion).HasDefaultValue(1L).ValueGeneratedNever().IsConcurrencyToken();
            entity.Property(t => t.Column).HasDefaultValue("Backlog");
            entity.HasIndex(t => new { t.ReminderAtUtc, t.ReminderSentAtUtc, t.Done, t.DeletedAt });
            entity.HasIndex(t => new { t.ListId, t.DeletedAt, t.Done, t.ListSortOrder });
            entity.HasIndex(t => new { t.ListId, t.DeletedAt, t.Column, t.KanbanSortOrder });
            entity.HasIndex(t => new { t.DueDate, t.DeletedAt, t.Done });

            entity.HasMany(t => t.Attachments)
                .WithOne(a => a.Task)
                .HasForeignKey(a => a.TaskId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(t => t.Steps)
                .WithOne(s => s.Task)
                .HasForeignKey(s => s.TaskId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(t => t.Comments)
                .WithOne(c => c.Task)
                .HasForeignKey(c => c.TaskId)
                .OnDelete(DeleteBehavior.Cascade);

            // Optional: explizit machen, dass beim Task-L�schen die Join-Links weg sollen
            entity.HasMany(t => t.LabelLinks)
                .WithOne(x => x.Task)
                .HasForeignKey(x => x.TaskId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(t => t.Watchers)
                .WithOne(w => w.Task)
                .HasForeignKey(w => w.TaskId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(t => t.CustomFieldValues)
                .WithOne(v => v.Task)
                .HasForeignKey(v => v.TaskId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<TodoCustomFieldDefinitionEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(160);
            entity.HasIndex(x => new { x.ListId, x.SortOrder });
            entity.HasMany(x => x.Options)
                .WithOne(o => o.Field)
                .HasForeignKey(o => o.FieldId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.SourceTaskList)
                .WithMany()
                .HasForeignKey(x => x.SourceTaskListId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<TodoCustomFieldOptionEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Value).HasMaxLength(240);
            entity.HasIndex(x => new { x.FieldId, x.SortOrder });
        });

        builder.Entity<TodoTaskCustomFieldValueEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Value).HasColumnType("text");
            entity.HasIndex(x => new { x.TaskId, x.FieldId }).IsUnique();
            entity.HasOne(x => x.Field)
                .WithMany()
                .HasForeignKey(x => x.FieldId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<TodoTaskWatcherEntity>(entity =>
        {
            entity.HasIndex(w => new { w.TaskId, w.UserId }).IsUnique();
        });

        // Join-Tabelle Task <-> Label
        builder.Entity<TodoTaskLabelEntity>(entity =>
        {
            entity.HasKey(x => new { x.TaskId, x.LabelId });

            entity.HasOne(x => x.Task)
                .WithMany(t => t.LabelLinks)
                .HasForeignKey(x => x.TaskId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(x => x.Label)
                .WithMany(l => l.TaskLinks)
                .HasForeignKey(x => x.LabelId)
                .OnDelete(DeleteBehavior.Cascade); // DAS ist f�r Label-L�schen entscheidend
        });

        builder.Entity<TodoLabelEntity>(entity =>
        {
            entity.HasIndex(x => new { x.ListId, x.Title })
                .IsUnique();

            // Optional: explizit machen, dass beim Label-L�schen die Join-Links weg sollen
            entity.HasMany(l => l.TaskLinks)
                .WithOne(x => x.Label)
                .HasForeignKey(x => x.LabelId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<TodoTaskMemberEntity>()
        .HasIndex(x => new { x.TaskId, x.UserId })
        .IsUnique();

        builder.Entity<TodoTaskMemberEntity>()
            .HasOne(x => x.Task)
            .WithMany(t => t.Members)
            .HasForeignKey(x => x.TaskId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<PersonalAccessTokenEntity>(entity =>
        {
            entity.HasIndex(t => t.TokenHash).IsUnique();
            entity.HasIndex(t => t.UserId);
            entity.Property(t => t.Name).HasMaxLength(200);
        });

        builder.Entity<MobileRefreshTokenEntity>(entity =>
        {
            entity.HasKey(token => token.Id);
            entity.HasIndex(token => token.TokenHash).IsUnique();
            entity.HasIndex(token => new { token.UserId, token.ExpiresAtUtc });
            entity.HasIndex(token => new { token.FamilyId, token.RevokedAtUtc });
            entity.Property(token => token.TokenHash).HasMaxLength(64);
            entity.Property(token => token.SecurityStamp).HasMaxLength(256);
            entity.Property(token => token.ReplacementTokenHash).HasMaxLength(64);
            entity.Property(token => token.RevokedAtUtc).IsConcurrencyToken();
            entity.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(token => token.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<UserNotificationPreferenceEntity>(entity =>
        {
            entity.HasIndex(p => p.UserId).IsUnique();
            entity.Property(p => p.PushContentMode).HasDefaultValue(PushNotificationContentMode.Anonymous);
        });

        builder.Entity<UserNotificationEntity>(entity =>
        {
            entity.HasIndex(n => new { n.UserId, n.ReadAtUtc, n.CreatedAtUtc });
            entity.Property(n => n.Title).HasMaxLength(240);
        });

        builder.Entity<DashboardEntity>(entity =>
        {
            entity.HasKey(d => d.Id);
            entity.Property(d => d.Name).HasMaxLength(200);
            entity.Property(d => d.FilterJson).HasColumnType("text").HasDefaultValue("{}");
            entity.Property(d => d.SelectedListIds).HasColumnType("uuid[]");
            entity.HasIndex(d => new { d.OwnerId, d.SortOrder });
            entity.HasIndex(d => d.PortfolioGroupId).IsUnique();
            entity.HasOne<TodoListGroupEntity>().WithMany().HasForeignKey(d => d.PortfolioGroupId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<TodoFormEntity>(entity =>
        {
            entity.HasKey(f => f.Id);
            entity.Property(f => f.Name).HasMaxLength(200);
            entity.Property(f => f.Description).HasColumnType("text");
            entity.Property(f => f.SuccessMessage).HasMaxLength(1000);
            entity.Property(f => f.Slug).HasMaxLength(80);
            entity.Property(f => f.PasswordSalt).HasMaxLength(128);
            entity.Property(f => f.PasswordHash).HasMaxLength(128);
            entity.Property(f => f.BackgroundColor).HasMaxLength(32);
            entity.Property(f => f.ButtonColor).HasMaxLength(32);
            entity.Property(f => f.CapacityReachedText).HasMaxLength(500);
            entity.HasIndex(f => f.Slug).IsUnique();
            entity.HasIndex(f => new { f.ListId, f.Name });

            entity.HasOne(f => f.List)
                .WithMany()
                .HasForeignKey(f => f.ListId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(f => f.Fields)
                .WithOne(field => field.Form)
                .HasForeignKey(field => field.FormId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<TodoFormFieldEntity>(entity =>
        {
            entity.HasKey(f => f.Id);
            entity.Property(f => f.Label).HasMaxLength(200);
            entity.Property(f => f.PublicLabel).HasMaxLength(200);
            entity.Property(f => f.HelpText).HasMaxLength(500);
            entity.Property(f => f.ValidationRulesJson).HasColumnType("text");
            entity.HasIndex(f => new { f.FormId, f.SortOrder });
        });

        builder.Entity<TodoFormSubmissionKeyEntity>(entity =>
        {
            entity.HasKey(s => s.Id);
            entity.Property(s => s.SubmissionKey).HasMaxLength(120);
            entity.Property(s => s.IpHash).HasMaxLength(128);
            entity.HasIndex(s => new { s.FormId, s.TaskId });
            entity.HasIndex(s => new { s.FormId, s.SubmissionKey }).IsUnique();
            entity.HasIndex(s => new { s.FormId, s.IpHash, s.CreatedAtUtc });
            entity.HasOne(s => s.Form)
                .WithMany()
                .HasForeignKey(s => s.FormId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ListEmailImportConfigurationEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Host).HasMaxLength(255);
            entity.Property(x => x.UserName).HasMaxLength(320);
            entity.Property(x => x.FolderName).HasMaxLength(512);
            entity.Property(x => x.TargetColumn).HasMaxLength(256);
            entity.Property(x => x.EncryptedPassword).HasColumnType("text");
            entity.Property(x => x.LastError).HasColumnType("text");
            entity.Property(x => x.IntervalMinutes).HasDefaultValue(15);
            entity.HasIndex(x => x.ListId).IsUnique();

            entity.HasOne(x => x.List)
                .WithMany()
                .HasForeignKey(x => x.ListId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ListEmailImportedMessageEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.FolderName).HasMaxLength(512);
            entity.HasIndex(x => new { x.ConfigurationId, x.FolderName, x.UidValidity, x.MessageUid }).IsUnique();
            entity.HasIndex(x => x.TaskId).IsUnique();
            entity.HasOne(x => x.Configuration)
                .WithMany()
                .HasForeignKey(x => x.ConfigurationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<TodoCommentEntity>(entity =>
        {
            entity.Property(x => x.AuthorUserId).HasMaxLength(450);
            entity.HasIndex(x => new { x.TaskId, x.AuthorUserId });
        });

        builder.Entity<TodoAutomationRuleEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(200);
            entity.HasIndex(x => new { x.ListId, x.SortOrder });
            entity.HasMany(x => x.Conditions)
                .WithOne(x => x.Rule)
                .HasForeignKey(x => x.RuleId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(x => x.Actions)
                .WithOne(x => x.Rule)
                .HasForeignKey(x => x.RuleId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<TodoAutomationConditionEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.FieldKey).HasMaxLength(100);
            entity.Property(x => x.Value).HasMaxLength(1000);
            entity.HasIndex(x => new { x.RuleId, x.SortOrder });
        });

        builder.Entity<TodoAutomationActionEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.FieldKey).HasMaxLength(100);
            entity.Property(x => x.Value).HasMaxLength(4000);
            entity.Property(x => x.ConfigurationJson).HasColumnType("text");
            entity.HasIndex(x => new { x.RuleId, x.SortOrder });
        });


    }
}
