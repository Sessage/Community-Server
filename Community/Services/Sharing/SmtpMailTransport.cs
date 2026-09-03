using System.ComponentModel;
using System.Net;
using System.Net.Mail;
using System.Net.Sockets;
using System.Security.Authentication;

namespace TodoSuite.Server.Services.Sharing;

/// <summary>Builds and sends SMTP messages while enforcing the configured TLS and authentication policy.</summary>
internal static class SmtpMailTransport
{
    public static async Task SendAsync(SmtpOptions options, MailMessage message)
    {
        ValidateOptions(options);

        if (UsesImplicitSslPort(options))
        {
            try
            {
                await SendWithClientAsync(options, message, 587);
                return;
            }
            catch (Exception fallbackEx)
            {
                var implicitSslException = new NotSupportedException(
                    "Port 465 erwartet in der Regel implizites SSL/TLS; System.Net.Mail unterstützt nur STARTTLS.");
                throw CreateDiagnosticException(options, fallbackEx, implicitSslException);
            }
        }

        try
        {
            await SendWithClientAsync(options, message, options.Port);
        }
        catch (Exception ex) when (ShouldTryStartTlsFallback(options, ex))
        {
            try
            {
                await SendWithClientAsync(options, message, 587);
            }
            catch (Exception fallbackEx)
            {
                throw CreateDiagnosticException(options, fallbackEx, ex);
            }
        }
        catch (Exception ex) when (IsSmtpTransportException(ex))
        {
            throw CreateDiagnosticException(options, ex);
        }
    }

    private static async Task SendWithClientAsync(SmtpOptions options, MailMessage message, int port)
    {
        using var client = new SmtpClient(options.Host.Trim(), port)
        {
            EnableSsl = options.EnableSsl,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            UseDefaultCredentials = false,
            Credentials = string.IsNullOrWhiteSpace(options.User)
                ? CredentialCache.DefaultNetworkCredentials
                : new NetworkCredential(options.User, options.Password),
            Timeout = Math.Clamp(options.TimeoutMilliseconds, 5000, 120000)
        };

        await client.SendMailAsync(message);
    }

    private static void ValidateOptions(SmtpOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Host))
            throw new InvalidOperationException("E-Mail-Versand fehlgeschlagen: SMTP Host ist nicht konfiguriert (Smtp:Host).");

        if (options.Port <= 0 || options.Port > 65535)
            throw new InvalidOperationException($"E-Mail-Versand fehlgeschlagen: SMTP Port '{options.Port}' ist ungültig.");

        if (string.IsNullOrWhiteSpace(options.FromAddress))
            throw new InvalidOperationException("E-Mail-Versand fehlgeschlagen: Absender ist nicht konfiguriert (Smtp:FromAddress).");
    }

    private static bool ShouldTryStartTlsFallback(SmtpOptions options, Exception ex)
        => UsesImplicitSslPort(options)
           && IsSmtpTransportException(ex);

    private static bool UsesImplicitSslPort(SmtpOptions options)
        => options.EnableSsl && options.Port == 465;

    private static bool IsSmtpTransportException(Exception ex)
        => ex is SmtpException
           or SocketException
           or IOException
           or AuthenticationException
           or InvalidOperationException
           or Win32Exception
           || ex.InnerException is not null && IsSmtpTransportException(ex.InnerException);

    private static InvalidOperationException CreateDiagnosticException(
        SmtpOptions options,
        Exception ex,
        Exception? firstAttemptException = null)
    {
        var details = BuildDiagnosticMessage(options, ex, firstAttemptException);
        return new InvalidOperationException(details, ex);
    }

    private static string BuildDiagnosticMessage(SmtpOptions options, Exception ex, Exception? firstAttemptException)
    {
        var host = options.Host.Trim();
        var authHint = string.IsNullOrWhiteSpace(options.User)
            ? "Es ist kein SMTP-Benutzer konfiguriert; der Server muss anonyme Anmeldung erlauben."
            : "Prüfe Benutzername und Passwort. Bei netcup ist der Benutzername normalerweise die vollständige E-Mail-Adresse.";

        var providerHint = options.EnableSsl && options.Port == 465
            ? "Hinweis: Port 465 erwartet häufig SSL/TLS direkt beim Verbindungsaufbau. System.Net.Mail nutzt mit EnableSsl jedoch STARTTLS; die App hat deshalb zusätzlich Port 587 mit STARTTLS versucht."
            : options.EnableSsl && options.Port == 587
                ? "Hinweis: Port 587 ist für SMTP Submission mit STARTTLS passend."
                : "Prüfe, ob Host, Port und Verschlüsselung zum SMTP-Server passen.";

        var status = FindSmtpException(ex)?.StatusCode;
        var statusText = status is null ? "" : $" SMTP-Status: {status}.";
        var firstAttemptText = firstAttemptException is null
            ? ""
            : $" Ursprünglicher Fehler auf Port {options.Port}: {RootMessage(firstAttemptException)}.";

        return
            $"E-Mail-Versand über SMTP fehlgeschlagen ({host}:{options.Port}, SSL={options.EnableSsl}). " +
            $"{providerHint} {authHint}{statusText} Technische Ursache: {RootMessage(ex)}.{firstAttemptText}";
    }

    private static SmtpException? FindSmtpException(Exception ex)
    {
        while (true)
        {
            if (ex is SmtpException smtp)
                return smtp;

            if (ex.InnerException is null)
                return null;

            ex = ex.InnerException;
        }
    }

    private static string RootMessage(Exception ex)
    {
        while (ex.InnerException is not null)
            ex = ex.InnerException;

        return string.IsNullOrWhiteSpace(ex.Message)
            ? ex.GetType().Name
            : ex.Message;
    }
}
