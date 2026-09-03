// AdminSettingsService.cs
using Microsoft.Extensions.Configuration;

namespace TodoSuite.Server.Services;

/// <summary>
/// Reads and updates installation-wide administrative settings from the database.
/// The service is the single source of truth for runtime switches such as self-registration.
/// </summary>
public class AdminSettingsService
{
    public bool AllowSelfRegistration { get; private set; }

    public AdminSettingsService(IConfiguration configuration)
    {
        AllowSelfRegistration = configuration.GetValue<bool>("AllowRegistration", defaultValue: true);
    }

    public void SetRegistrationAllowed(bool allowed)
    {
        AllowSelfRegistration = allowed;
    }
}
