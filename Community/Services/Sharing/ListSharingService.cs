using System.Net;
using System.Net.Mail;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Klassenbibliothek.Data;
using Klassenbibliothek.Services;
using QRCoder;

namespace TodoSuite.Server.Services.Sharing;

public class ListSharingService : IListSharingService
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;
    private readonly SmtpOptions _smtp;
    private readonly IConfiguration _cfg;

    public ListSharingService(
        IDbContextFactory<ApplicationDbContext> dbFactory,
        IOptions<SmtpOptions> smtpOptions,
        IConfiguration cfg)
    {
        _dbFactory = dbFactory;
        _smtp = smtpOptions.Value;
        _cfg = cfg;
    }

    public async Task<(bool Success, string Message, string? Link)> CreateShareLinkAsync(
        string requestingUserId, Guid listId, ListRole role, string? comment)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var list = await db.TodoLists
            .Include(l => l.Participants)
            .FirstOrDefaultAsync(l => l.Id == listId);

        if (list is null)
            return (false, "Liste nicht gefunden.", null);

        var requestKey = NormalizeKey(requestingUserId);
        var (reqEmailFromIdentity, _) = await GetUserProfileAsync(db, requestingUserId);

        if (!CanAdmin(requestKey, reqEmailFromIdentity, list))
            return (false, "Keine Berechtigung: Nur Admin darf Share-Links erstellen.", null);

        var token = Guid.NewGuid().ToString("N");
        var trimmedComment = (comment ?? "").Trim();
        if (trimmedComment.Length > 200) trimmedComment = trimmedComment[..200];

        db.ListInvites.Add(new ListInviteEntity
        {
            Id = Guid.NewGuid(),
            ListId = listId,
            Token = token,
            Role = role,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = requestingUserId,
            ExpiresAtUtc = DateTime.UtcNow.AddDays(30),
            Revoked = false,

            Type = ListInviteType.ShareLink,
            Comment = string.IsNullOrWhiteSpace(trimmedComment) ? null : trimmedComment,
            SingleUse = false,
            UsedAtUtc = null,
            InviteEmail = null
        });

        await db.SaveChangesAsync();

        var link = BuildShareUrl(listId, token);
        return (true, "Share-Link erstellt.", link);
    }

    public async Task<InviteResult> InviteByEmailAsync(string requestingUserId, Guid listId, string email, string displayName, ListRole role)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        // AsNoTracking: verhindert, dass EF die TodoListEntity als "Modified" markiert
        // und beim SaveChanges ein fehlgeschlagenes UPDATE erzeugt (DbUpdateConcurrencyException).
        var list = await db.TodoLists
            .AsNoTracking()
            .Include(l => l.Participants)
            .FirstOrDefaultAsync(l => l.Id == listId);

        if (list is null)
            return new InviteResult(false, "Liste nicht gefunden.");

        var requestKey = NormalizeKey(requestingUserId);
        var (reqEmailFromIdentity, _) = await GetUserProfileAsync(db, requestingUserId);

        if (!CanAdmin(requestKey, reqEmailFromIdentity, list))
            return new InviteResult(false, "Keine Berechtigung: Nur Admin darf einladen.");

        var targetEmail = (email ?? "").Trim();
        if (string.IsNullOrWhiteSpace(targetEmail) || !targetEmail.Contains('@'))
            return new InviteResult(false, $"Einladung konnte nicht gesendet werden: '{email}' ist keine gültige E-Mail-Adresse.");

        var token = Guid.NewGuid().ToString("N");

        db.ListInvites.Add(new ListInviteEntity
        {
            Id = Guid.NewGuid(),
            ListId = listId,
            Token = token,
            Role = role,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = requestingUserId,
            ExpiresAtUtc = DateTime.UtcNow.AddDays(14),
            Revoked = false,

            Type = ListInviteType.EmailInvite,
            Comment = null,
            InviteEmail = targetEmail,
            SingleUse = true,
            UsedAtUtc = null
        });

        // Participant vormerken/aktualisieren + Rolle setzen
        var existingParticipantId = list.Participants.FirstOrDefault(p => EqualsEmail(p.Email, targetEmail))?.Id;
        var existing = existingParticipantId is Guid trackedId
            ? await db.ListParticipants.FirstOrDefaultAsync(p => p.Id == trackedId)
            : null;
        if (existing is null)
        {
            // Direkt über DbSet einfügen statt über die Navigation der (untracked) Liste
            db.ListParticipants.Add(new ListParticipantEntity
            {
                Id = Guid.NewGuid(),
                ListId = listId,
                Email = targetEmail,
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? targetEmail : displayName.Trim(),
                InvitationPending = true,
                DirectInvitationPending = true,
                Role = role,
                DirectRole = role
            });
        }
        else
        {
            existing.DisplayName = string.IsNullOrWhiteSpace(displayName) ? existing.DisplayName : displayName.Trim();
            existing.Email = targetEmail;
            PortfolioAccessCoordinator.SetDirectAccess(existing, role, invitationPending: true);
        }

        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException ex)
        {
            return new InviteResult(false, $"Datenbankfehler beim Speichern der Einladung. Details: {ex.InnerException?.Message ?? ex.Message}");
        }

        var link = BuildShareUrl(listId, token);

        try
        {
            var lang = GetDefaultLanguage();
            var qrBase64 = GenerateQrCodeBase64(link);
            var roleLabel = TranslateRole(role, lang);
            var body = BuildInviteHtmlMail(list.Name, link, roleLabel, qrBase64, lang);

            var subject = lang == "en"
                ? $"Invitation to '{list.Name}'"
                : $"Einladung zu '{list.Name}'";

            await SendHtmlMailAsync(
                to: targetEmail,
                subject: subject,
                htmlBody: body
            );

            return new InviteResult(true, "Einladung wurde versendet.");
        }
        catch (Exception ex)
        {
            return new InviteResult(false, $"Einladung konnte nicht per E-Mail versendet werden. Details: {ex.Message}");
        }
    }

    public async Task<(bool Success, string Message)> AcceptShareLinkAsync(string acceptingUserId, Guid listId, string token)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var t = (token ?? "").Trim();
        if (string.IsNullOrWhiteSpace(t))
            return (false, "Einladungslink ist ungültig (Token fehlt).");

        var list = await db.TodoLists
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == listId);

        if (list is null)
            return (false, "Liste nicht gefunden.");

        var invite = await db.ListInvites
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.ListId == listId && x.Token == t);

        if (invite is null)
            return (false, "Einladungslink ist ungültig.");

        if (invite.Revoked)
            return (false, "Einladungslink wurde widerrufen.");

        if (invite.ExpiresAtUtc is not null && invite.ExpiresAtUtc.Value < DateTime.UtcNow)
            return (false, "Einladungslink ist abgelaufen.");

        // ✅ Typ/SingleUse korrekt behandeln
        if (invite.Type == ListInviteType.EmailInvite && invite.SingleUse && invite.UsedAtUtc is not null)
            return (false, "Einladungslink wurde bereits verwendet.");

        var (email, displayName) = await GetUserProfileAsync(db, acceptingUserId);
        var idEmail = (email ?? "").Trim();
        var disp = !string.IsNullOrWhiteSpace(displayName)
            ? displayName!.Trim()
            : (string.IsNullOrWhiteSpace(idEmail) ? acceptingUserId : idEmail);

        // UsedAtUtc setzen:
        // - ShareLink: nur Statistik (einmalig setzen wenn null)
        // - EmailInvite: nutzen wir als "consumed"-Marker
        _ = await db.ListInvites
            .Where(x => x.Id == invite.Id && x.UsedAtUtc == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.UsedAtUtc, DateTime.UtcNow));

        // E-Mail Invite: wenn SingleUse -> nach Annahme widerrufen (oder löschen)
        // ShareLink: bleibt bestehen
        if (invite.Type == ListInviteType.EmailInvite && invite.SingleUse)
        {
            _ = await db.ListInvites
                .Where(x => x.Id == invite.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Revoked, true));
        }

        // Participant Upsert ohne Concurrency
        var participant = await db.ListParticipants
            .Where(p => p.ListId == listId)
            .Where(p =>
                (!string.IsNullOrWhiteSpace(p.UserId) && p.UserId == acceptingUserId)
                || (!string.IsNullOrWhiteSpace(idEmail) && p.Email == idEmail))
            .FirstOrDefaultAsync();

        if (participant is not null)
        {
            participant.UserId = acceptingUserId;
            participant.Email = idEmail;
            participant.DisplayName = disp;
            PortfolioAccessCoordinator.SetDirectAccess(participant, invite.Role, invitationPending: false);
            await db.SaveChangesAsync();
            return (true, "Liste wurde hinzugefügt.");
        }

        db.ListParticipants.Add(new ListParticipantEntity
        {
            Id = Guid.NewGuid(),
            ListId = listId,
            UserId = acceptingUserId,
            Email = idEmail,
            DisplayName = disp,
            InvitationPending = false,
            DirectInvitationPending = false,
            Role = invite.Role,
            DirectRole = invite.Role
        });

        try
        {
            await db.SaveChangesAsync();
            return (true, "Liste wurde hinzugefügt.");
        }
        catch (DbUpdateException ex)
        {
            return (false, $"Annahme des Share-Links ist fehlgeschlagen. Details: {ex.Message}");
        }
    }

    public async Task<IReadOnlyList<ShareLinkInfo>> GetShareLinksAsync(string requestingUserId, Guid listId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var list = await db.TodoLists
            .Include(l => l.Participants)
            .FirstOrDefaultAsync(l => l.Id == listId);

        if (list is null)
            return Array.Empty<ShareLinkInfo>();

        var requestKey = NormalizeKey(requestingUserId);
        var (reqEmailFromIdentity, _) = await GetUserProfileAsync(db, requestingUserId);

        if (!CanAdmin(requestKey, reqEmailFromIdentity, list))
            return Array.Empty<ShareLinkInfo>();

        var baseUrl = GetBaseUrl();

        var items = await db.ListInvites
            .AsNoTracking()
            .Where(x => x.ListId == listId && x.Type == ListInviteType.ShareLink)
            .OrderByDescending(x => x.CreatedAtUtc)
            .Select(x => new ShareLinkInfo(
                x.Id,
                x.ListId,
                x.Token,
                $"{baseUrl}/share/list/{x.ListId}?token={x.Token}",
                x.Role,
                x.Comment,
                x.Revoked,
                x.CreatedAtUtc,
                x.ExpiresAtUtc,
                null))
            .ToListAsync();

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (string.IsNullOrWhiteSpace(item.Link))
                continue;

            var qrBase64 = GenerateQrCodeBase64(item.Link);
            items[i] = item with { QrCodeDataUrl = $"data:image/png;base64,{qrBase64}" };
        }

        return items;
    }

    public async Task<(bool Success, string Message)> UpdateShareLinkCommentAsync(string requestingUserId, Guid listId, Guid inviteId, string? comment)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var list = await db.TodoLists
            .Include(l => l.Participants)
            .FirstOrDefaultAsync(l => l.Id == listId);

        if (list is null)
            return (false, "Liste nicht gefunden.");

        var requestKey = NormalizeKey(requestingUserId);
        var (reqEmailFromIdentity, _) = await GetUserProfileAsync(db, requestingUserId);

        if (!CanAdmin(requestKey, reqEmailFromIdentity, list))
            return (false, "Keine Berechtigung: Nur Admin darf Share-Links ändern.");

        var invite = await db.ListInvites.FirstOrDefaultAsync(x =>
            x.Id == inviteId && x.ListId == listId && x.Type == ListInviteType.ShareLink);

        if (invite is null)
            return (false, "Share-Link nicht gefunden.");

        var c = (comment ?? "").Trim();
        if (c.Length > 200) c = c[..200];
        invite.Comment = string.IsNullOrWhiteSpace(c) ? null : c;

        await db.SaveChangesAsync();
        return (true, "Kommentar gespeichert.");
    }

    public async Task<(bool Success, string Message)> RevokeShareLinkAsync(string requestingUserId, Guid listId, Guid inviteId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var list = await db.TodoLists
            .Include(l => l.Participants)
            .FirstOrDefaultAsync(l => l.Id == listId);

        if (list is null)
            return (false, "Liste nicht gefunden.");

        var requestKey = NormalizeKey(requestingUserId);
        var (reqEmailFromIdentity, _) = await GetUserProfileAsync(db, requestingUserId);

        if (!CanAdmin(requestKey, reqEmailFromIdentity, list))
            return (false, "Keine Berechtigung: Nur Admin darf Share-Links löschen.");

        var invite = await db.ListInvites.FirstOrDefaultAsync(x =>
            x.Id == inviteId && x.ListId == listId && x.Type == ListInviteType.ShareLink);

        if (invite is null)
            return (false, "Share-Link nicht gefunden.");

        // ✅ Statt Hard-Delete: widerrufen (besser fürs Audit & vermeidet Race Conditions)
        invite.Revoked = true;

        await db.SaveChangesAsync();
        return (true, "Share-Link wurde widerrufen.");
    }

    public async Task<(bool Success, string Message)> UpdateParticipantRoleAsync(string requestingUserId, Guid listId, Guid participantId, ListRole role)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var list = await db.TodoLists
            .Include(l => l.Participants)
            .FirstOrDefaultAsync(l => l.Id == listId);

        if (list is null)
            return (false, "Liste nicht gefunden.");

        var requestKey = NormalizeKey(requestingUserId);
        var (reqEmailFromIdentity, _) = await GetUserProfileAsync(db, requestingUserId);

        if (!CanAdmin(requestKey, reqEmailFromIdentity, list))
            return (false, "Keine Berechtigung: Nur Admin darf Rollen ändern.");

        // Owner nicht veränderbar (OwnerId matcht Participant.UserId)
        var ownerId = (list.OwnerId ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(ownerId))
        {
            var isOwnerTarget = list.Participants.Any(p =>
                p.Id == participantId &&
                !string.IsNullOrWhiteSpace(p.UserId) &&
                string.Equals(p.UserId.Trim(), ownerId, StringComparison.OrdinalIgnoreCase));

            if (isOwnerTarget)
                return (false, "Die Rolle des Owners kann nicht geändert werden.");
        }

        var participant = list.Participants.FirstOrDefault(p => p.Id == participantId);
        if (participant is null) return (false, "Teilnehmer nicht gefunden.");
        PortfolioAccessCoordinator.NormalizeLegacyAccess(participant);
        if (participant.DirectRole is null && (participant.PortfolioRole is not null || participant.DirectoryRole is not null))
            return (false, "Diese Rolle wird geerbt und muss an ihrer Freigabequelle geändert werden.");
        PortfolioAccessCoordinator.SetDirectAccess(participant, role, participant.DirectInvitationPending);
        await db.SaveChangesAsync();

        return (true, "Rolle wurde aktualisiert.");
    }

    public async Task<(bool Success, string Message)> RemovePendingInvitationAsync(string requestingUserId, Guid listId, Guid participantId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var list = await db.TodoLists
            .Include(l => l.Participants)
            .FirstOrDefaultAsync(l => l.Id == listId);

        if (list is null)
            return (false, "Liste nicht gefunden.");

        var requestKey = NormalizeKey(requestingUserId);
        var (reqEmailFromIdentity, _) = await GetUserProfileAsync(db, requestingUserId);

        if (!CanAdmin(requestKey, reqEmailFromIdentity, list))
            return (false, "Keine Berechtigung: Nur Admin darf Einladungen zurückziehen.");

        var participant = list.Participants.FirstOrDefault(p => p.Id == participantId);
        if (participant is null)
            return (false, "Teilnehmer nicht gefunden.");

        PortfolioAccessCoordinator.NormalizeLegacyAccess(participant);
        if (!participant.DirectInvitationPending)
            return (false, "Dieser Teilnehmer hat die Einladung bereits angenommen und kann hier nicht entfernt werden.");

        // Ausstehende Einladungs-Token für diese E-Mail widerrufen
        if (!string.IsNullOrWhiteSpace(participant.Email))
        {
            await db.ListInvites
                .Where(x => x.ListId == listId && x.InviteEmail == participant.Email && !x.Revoked)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Revoked, true));
        }

        participant.DirectRole = null;
        participant.DirectInvitationPending = false;
        if (participant.PortfolioRole is null && participant.DirectoryRole is null)
            db.ListParticipants.Remove(participant);
        else
            participant.RecalculateEffectiveAccess();
        await db.SaveChangesAsync();

        return (true, "Einladung wurde zurückgezogen.");
    }

    /* ============================
     * Helpers
     * ============================ */

    private string BuildShareUrl(Guid listId, string token)
        => $"{GetBaseUrl()}/share/list/{listId}?token={token}";

    private string GetBaseUrl()
    {
        var baseUrl = (_smtp.AppBaseUrl ?? "").TrimEnd('/');
        return string.IsNullOrWhiteSpace(baseUrl) ? "https://localhost:7000" : baseUrl;
    }

    private string GetDefaultLanguage()
    {
        var lang = (_cfg["DefaultLanguage"] ?? "").Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(lang) ? "de" : lang;
    }

    private static string TranslateRole(ListRole role, string lang) => lang switch
    {
        "en" => role switch
        {
            ListRole.Observer => "Observer",
            ListRole.Member   => "Member",
            ListRole.Admin    => "Admin",
            _                 => role.ToString()
        },
        _ => role switch    // default: de
        {
            ListRole.Observer => "Beobachter",
            ListRole.Member   => "Mitglied",
            ListRole.Admin    => "Admin",
            _                 => role.ToString()
        }
    };

    private static bool CanAdmin(string requestKey, string? identityEmail, TodoListEntity list)
    {
        var req = NormalizeKey(requestKey);
        var idEmail = (identityEmail ?? "").Trim();

        if (string.Equals((list.OwnerId ?? "").Trim(), req, StringComparison.OrdinalIgnoreCase))
            return true;

        if (list.Participants is null || list.Participants.Count == 0)
            return false;

        return list.Participants.Any(p =>
            !p.InvitationPending
            && p.Role == ListRole.Admin
            && (
                (!string.IsNullOrWhiteSpace(p.UserId) &&
                 string.Equals(p.UserId.Trim(), req, StringComparison.OrdinalIgnoreCase))
                || EqualsEmail(p.Email, req)
                || (!string.IsNullOrWhiteSpace(idEmail) && EqualsEmail(p.Email, idEmail))
            )
        );
    }

    private static string NormalizeKey(string? v) => (v ?? "").Trim();

    private async Task SendHtmlMailAsync(string to, string subject, string htmlBody)
    {
        if (string.IsNullOrWhiteSpace(_smtp.FromAddress))
            throw new InvalidOperationException("E-Mail-Versand fehlgeschlagen: Absender ist nicht konfiguriert (Smtp:FromAddress).");

        using var msg = new MailMessage
        {
            From = new MailAddress(_smtp.FromAddress, _smtp.FromName),
            Subject = subject,
            Body = htmlBody,
            IsBodyHtml = true
        };
        msg.To.Add(to);

        await SmtpMailTransport.SendAsync(_smtp, msg);
    }

    private static string GenerateQrCodeBase64(string content)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.Q);
        using var code = new PngByteQRCode(data);
        var pngBytes = code.GetGraphic(10);
        return Convert.ToBase64String(pngBytes);
    }

    private static string BuildInviteHtmlMail(string listName, string link, string roleLabel, string qrBase64, string lang = "de")
    {
        var safeListName = WebUtility.HtmlEncode(listName);
        var safeLink = WebUtility.HtmlEncode(link);
        var safeRole = WebUtility.HtmlEncode(roleLabel);

        string heading, body, btnText, fallbackHint, qrHeading, qrBody, footer;

        if (lang == "en")
        {
            heading    = $"Invitation to &ldquo;{safeListName}&rdquo;";
            body       = $"You have been invited to join the list <strong>{safeListName}</strong> (Role: {safeRole}).";
            btnText    = "Accept invitation";
            fallbackHint = "If the button doesn't work, copy this link into your browser:";
            qrHeading  = "Scan in the app";
            qrBody     = "Open the Sessage app, tap the QR-scanner icon next to the logo and scan the code below:";
            footer     = "If you did not expect this invitation, you can safely ignore this email.";
        }
        else
        {
            heading    = $"Einladung zu &bdquo;{safeListName}&ldquo;";
            body       = $"Du wurdest eingeladen, an der Liste <strong>{safeListName}</strong> teilzunehmen (Rolle: {safeRole}).";
            btnText    = "Einladung annehmen";
            fallbackHint = "Falls der Button nicht funktioniert, kopiere diesen Link in deinen Browser:";
            qrHeading  = "In der App scannen";
            qrBody     = "Öffne die Sessage-App, tippe auf das QR-Scanner-Symbol neben dem Logo und scanne den Code unten:";
            footer     = "Falls du diese Einladung nicht erwartet hast, kannst du diese E-Mail ignorieren.";
        }

        return $@"<!doctype html>
<html lang=""{lang}"">
<head>
  <meta charset=""utf-8"" />
</head>
<body style=""font-family:Segoe UI, Arial, sans-serif; background:#f8fafc; padding:24px;"">
  <div style=""max-width:560px; margin:0 auto; background:#ffffff; border:1px solid #e2e8f0; border-radius:16px; padding:20px;"">
    <h2 style=""margin:0 0 12px 0; font-size:18px; color:#0f172a;"">{heading}</h2>
    <p style=""margin:0 0 16px 0; color:#334155; font-size:14px;"">
      {body}
    </p>

    <p style=""margin:0 0 18px 0;"">
      <a href=""{safeLink}""
         style=""display:inline-block; background:#2563eb; color:#ffffff; text-decoration:none; padding:10px 14px; border-radius:12px; font-weight:600;"">
        {btnText}
      </a>
    </p>

    <p style=""margin:0 0 8px 0; color:#64748b; font-size:12px;"">
      {fallbackHint}
    </p>
    <p style=""margin:0 0 20px 0; font-size:12px; color:#0f172a; word-break:break-all;"">
      {safeLink}
    </p>

    <hr style=""border:none; border-top:1px solid #e2e8f0; margin:0 0 20px 0;"" />

    <p style=""margin:0 0 10px 0; color:#334155; font-size:13px; font-weight:600;"">
      {qrHeading}
    </p>
    <p style=""margin:0 0 12px 0; color:#64748b; font-size:12px;"">
      {qrBody}
    </p>
    <div style=""text-align:center; margin:0 0 20px 0;"">
      <img src=""data:image/png;base64,{qrBase64}""
           alt=""QR-Code""
           width=""200"" height=""200""
           style=""border:4px solid #e2e8f0; border-radius:12px;"" />
    </div>

    <p style=""margin:0; color:#64748b; font-size:12px;"">
      {footer}
    </p>
  </div>
</body>
</html>";
    }

    private static async Task<(string? Email, string? DisplayName)> GetUserProfileAsync(ApplicationDbContext db, string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return (null, null);

        var u = await db.Users
            .AsNoTracking()
            .Where(x => x.Id == userId)
            .Select(x => new { x.Email, x.UserName, x.DisplayName })
            .FirstOrDefaultAsync();

        if (u is null) return (null, null);

        var email = (u.Email ?? "").Trim();
        var display = (u.DisplayName ?? u.UserName ?? "").Trim();

        return (string.IsNullOrWhiteSpace(email) ? null : email,
                string.IsNullOrWhiteSpace(display) ? null : display);
    }

    private static bool EqualsEmail(string? a, string? b)
        => string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
}
