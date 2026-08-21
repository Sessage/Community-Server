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

    public DbSet<TodoTaskMemberEntity> TodoTaskMembers => Set<TodoTaskMemberEntity>();
    public DbSet<TodoListWatcherEntity> TodoListWatchers => Set<TodoListWatcherEntity>();
    public DbSet<TodoTaskWatcherEntity> TodoTaskWatchers => Set<TodoTaskWatcherEntity>();
    public DbSet<BoardNotificationRuleEntity> BoardNotificationRules => Set<BoardNotificationRuleEntity>();
    public DbSet<UserNotificationPreferenceEntity> UserNotificationPreferences => Set<UserNotificationPreferenceEntity>();
    public DbSet<UserNotificationEntity> UserNotifications => Set<UserNotificationEntity>();

    public DbSet<PersonalAccessTokenEntity> PersonalAccessTokens => Set<PersonalAccessTokenEntity>();

    public DbSet<DashboardEntity> Dashboards => Set<DashboardEntity>();
    public DbSet<TodoFormEntity> TodoForms => Set<TodoFormEntity>();
    public DbSet<TodoFormFieldEntity> TodoFormFields => Set<TodoFormFieldEntity>();
    public DbSet<TodoFormSubmissionKeyEntity> TodoFormSubmissionKeys => Set<TodoFormSubmissionKeyEntity>();
    public DbSet<ListEmailImportConfigurationEntity> ListEmailImportConfigurations => Set<ListEmailImportConfigurationEntity>();
    public DbSet<TodoAutomationRuleEntity> TodoAutomationRules => Set<TodoAutomationRuleEntity>();
    public DbSet<TodoAutomationConditionEntity> TodoAutomationConditions => Set<TodoAutomationConditionEntity>();
    public DbSet<TodoAutomationActionEntity> TodoAutomationActions => Set<TodoAutomationActionEntity>();


    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<ApplicationUser>()
            .Property(user => user.PreferredLanguage)
            .HasMaxLength(16);

        builder.Entity<ApplicationUser>()
            .Property(user => user.DisplayName)
            .HasMaxLength(200);

        builder.Entity<TodoListEntity>(entity =>
        {
            // ContentVersion is advanced by services and exposed to mobile sync. Database-side
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
            entity.HasIndex(x => x.PrincipalId);
            entity.Property(x => x.GroupIds).HasColumnType("text[]");
            entity.HasOne<ApplicationUser>().WithOne().HasForeignKey<DirectoryIdentityEntity>(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<DirectoryShareGrantEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.ResourceType, x.ResourceId });
            entity.HasIndex(x => new { x.ResourceType, x.ResourceId, x.PrincipalType, x.PrincipalId }).IsUnique();
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
