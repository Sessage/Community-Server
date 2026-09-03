namespace TodoSuite.Server.Services.Sharing;

/// <summary>SMTP transport, authentication, sender, and TLS configuration.</summary>
public class SmtpOptions
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public bool EnableSsl { get; set; } = true;
    public bool UseSsl
    {
        get => EnableSsl;
        set => EnableSsl = value;
    }
    public int TimeoutMilliseconds { get; set; } = 30000;

    public string User { get; set; } = "";
    public string Password { get; set; } = "";

    public string FromAddress { get; set; } = "";
    public string FromName { get; set; } = "Sessage";

    /// <summary>Basis-URL der App (z.B. https://todo.example.com) – für Links in E-Mails.</summary>
    public string AppBaseUrl { get; set; } = "";
}
