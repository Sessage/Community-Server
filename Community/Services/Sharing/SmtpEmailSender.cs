using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Mail;
using Klassenbibliothek.Data;

namespace TodoSuite.Server.Services.Sharing;

/// <summary>
/// Adapts ASP.NET Core Identity email operations to the installation's SMTP transport.
/// Authentication links are passed as already constructed URLs and encoded by the email template.
/// </summary>
public class SmtpEmailSender : IEmailSender, IEmailSender<ApplicationUser>
{
    private readonly SmtpOptions _opt;

    public SmtpEmailSender(IOptions<SmtpOptions> options)
    {
        _opt = options.Value ?? new SmtpOptions();
    }

    // UI-IEmailSender
    public async Task SendEmailAsync(string email, string subject, string htmlMessage)
    {
        if (string.IsNullOrWhiteSpace(email))
            throw new ArgumentException("E-Mail-Versand fehlgeschlagen: Empfänger ist leer.", nameof(email));

        if (string.IsNullOrWhiteSpace(_opt.FromAddress))
            throw new InvalidOperationException("E-Mail-Versand fehlgeschlagen: Absender ist nicht konfiguriert (Smtp:FromAddress).");

        using var msg = new MailMessage
        {
            From = new MailAddress(_opt.FromAddress, _opt.FromName),
            Subject = subject ?? "",
            Body = htmlMessage ?? "",
            IsBodyHtml = true,
        };

        msg.To.Add(email.Trim());

        await SmtpMailTransport.SendAsync(_opt, msg);
    }

    // Identity-IEmailSender<ApplicationUser>
    public Task SendConfirmationLinkAsync(ApplicationUser user, string email, string confirmationLink)
    {
        if (string.IsNullOrWhiteSpace(confirmationLink))
            throw new ArgumentException("E-Mail-Versand fehlgeschlagen: Bestätigungslink ist leer.", nameof(confirmationLink));

        var subject = "E-Mail-Adresse bestätigen";
        var body = BuildHtmlMail(
            headline: "Bitte bestätige deine E-Mail-Adresse",
            intro: "Klicke auf den folgenden Button, um deine E-Mail-Adresse zu bestätigen:",
            buttonText: "E-Mail bestätigen",
            buttonUrl: UseConfiguredPublicOrigin(confirmationLink),
            footerNote: "Falls du dich nicht registriert hast, kannst du diese E-Mail ignorieren."
        );

        return SendEmailAsync(email, subject, body);
    }

    public Task SendPasswordResetLinkAsync(ApplicationUser user, string email, string resetLink)
    {
        if (string.IsNullOrWhiteSpace(resetLink))
            throw new ArgumentException("E-Mail-Versand fehlgeschlagen: Reset-Link ist leer.", nameof(resetLink));

        var subject = "Passwort zurücksetzen";
        var body = BuildHtmlMail(
            headline: "Passwort zurücksetzen",
            intro: "Klicke auf den folgenden Button, um dein Passwort zurückzusetzen:",
            buttonText: "Passwort zurücksetzen",
            buttonUrl: UseConfiguredPublicOrigin(resetLink),
            footerNote: "Wenn du kein Zurücksetzen angefordert hast, kannst du diese E-Mail ignorieren."
        );

        return SendEmailAsync(email, subject, body);
    }

    public Task SendPasswordResetCodeAsync(ApplicationUser user, string email, string resetCode)
    {
        if (string.IsNullOrWhiteSpace(resetCode))
            throw new ArgumentException("E-Mail-Versand fehlgeschlagen: Reset-Code ist leer.", nameof(resetCode));

        var subject = "Passwort-Reset Code";
        var body = $@"
<!doctype html>
<html lang=""de"">
<head>
  <meta charset=""utf-8"" />
</head>
<body style=""font-family:Segoe UI, Arial, sans-serif; background:#f8fafc; padding:24px;"">
  <div style=""max-width:560px; margin:0 auto; background:#ffffff; border:1px solid #e2e8f0; border-radius:16px; padding:20px;"">
    <h2 style=""margin:0 0 12px 0; font-size:18px; color:#0f172a;"">Passwort-Reset Code</h2>
    <p style=""margin:0 0 12px 0; color:#334155; font-size:14px;"">
      Verwende folgenden Code, um dein Passwort zurückzusetzen:
    </p>
    <div style=""font-size:20px; letter-spacing:2px; font-weight:700; padding:12px 16px; background:#f1f5f9; border-radius:12px; display:inline-block; color:#0f172a;"">
      {WebUtility.HtmlEncode(resetCode)}
    </div>
    <p style=""margin:16px 0 0 0; color:#64748b; font-size:12px;"">
      Wenn du das nicht angefordert hast, kannst du diese E-Mail ignorieren.
    </p>
  </div>
</body>
</html>";

        return SendEmailAsync(email, subject, body);
    }

    private static string BuildHtmlMail(string headline, string intro, string buttonText, string buttonUrl, string footerNote)
    {
        // Minimal hübsches HTML, ohne externe Assets
        return $@"
<!doctype html>
<html lang=""de"">
<head>
  <meta charset=""utf-8"" />
</head>
<body style=""font-family:Segoe UI, Arial, sans-serif; background:#f8fafc; padding:24px;"">
  <div style=""max-width:560px; margin:0 auto; background:#ffffff; border:1px solid #e2e8f0; border-radius:16px; padding:20px;"">
    <h2 style=""margin:0 0 12px 0; font-size:18px; color:#0f172a;"">{WebUtility.HtmlEncode(headline)}</h2>
    <p style=""margin:0 0 16px 0; color:#334155; font-size:14px;"">
      {WebUtility.HtmlEncode(intro)}
    </p>

    <p style=""margin:0 0 18px 0;"">
      <a href=""{WebUtility.HtmlEncode(buttonUrl)}""
         style=""display:inline-block; background:#2563eb; color:#ffffff; text-decoration:none; padding:10px 14px; border-radius:12px; font-weight:600;"">
        {WebUtility.HtmlEncode(buttonText)}
      </a>
    </p>

    <p style=""margin:0 0 12px 0; color:#64748b; font-size:12px;"">
      Falls der Button nicht funktioniert, kopiere diesen Link in deinen Browser:
    </p>
    <p style=""margin:0 0 16px 0; font-size:12px; color:#0f172a; word-break:break-all;"">
      {WebUtility.HtmlEncode(buttonUrl)}
    </p>

    <p style=""margin:16px 0 0 0; color:#64748b; font-size:12px;"">
      {WebUtility.HtmlEncode(footerNote)}
    </p>
  </div>
</body>
</html>";

    }

    private string UseConfiguredPublicOrigin(string actionUrl)
    {
        if (!Uri.TryCreate(_opt.AppBaseUrl?.Trim(), UriKind.Absolute, out var configuredBase)
            || !Uri.TryCreate(actionUrl, UriKind.Absolute, out var original))
        {
            return actionUrl;
        }

        return new UriBuilder(original)
        {
            Scheme = configuredBase.Scheme,
            Host = configuredBase.Host,
            Port = configuredBase.IsDefaultPort ? -1 : configuredBase.Port
        }.Uri.AbsoluteUri;
    }
}
