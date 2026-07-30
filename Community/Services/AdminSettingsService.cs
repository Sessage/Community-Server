// AdminSettingsService.cs

namespace TodoSuite.Server.Services;

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
