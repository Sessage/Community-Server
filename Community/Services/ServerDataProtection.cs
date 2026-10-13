using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace TodoSuite.Server.Services;

/// <summary>Preserves existing key stores and prevents ephemeral keys on hosts without a profile.</summary>
public static class ServerDataProtection
{
    public static void Configure(WebApplicationBuilder builder)
    {
        if (!string.IsNullOrWhiteSpace(builder.Configuration["DataProtection:KeyPath"]))
        {
            Configure(builder.Services, builder.Configuration, builder.Environment.ContentRootPath, defaultStoreAvailable: true);
            return;
        }
        var defaultStoreAvailable = FileSystemXmlRepository.DefaultKeyStorageDirectory is not null
            || (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEBSITE_INSTANCE_ID"))
                && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HOME")));
        if (!defaultStoreAvailable && OperatingSystem.IsWindows())
            defaultStoreAvailable = RegistryXmlRepository.DefaultRegistryKey is not null;

        Configure(builder.Services, builder.Configuration, builder.Environment.ContentRootPath, defaultStoreAvailable);
    }

    internal static void Configure(IServiceCollection services, IConfiguration configuration,
        string contentRoot, bool defaultStoreAvailable)
    {
        var configuredPath = configuration["DataProtection:KeyPath"];
        var certificateThumbprint = configuration["DataProtection:CertificateThumbprint"];
        var keyPath = ResolveKeyPath(configuredPath, contentRoot, defaultStoreAvailable);
        if (keyPath is null)
        {
            if (!string.IsNullOrWhiteSpace(certificateThumbprint))
                throw new InvalidOperationException("DataProtection:CertificateThumbprint erfordert DataProtection:KeyPath.");
            return; // Keep the framework's existing profile/registry/container store and discriminator.
        }

        // A missing ACL must fail startup instead of silently losing every cookie on recycle.
        Directory.CreateDirectory(keyPath);
        using (var probe = new FileStream(Path.Combine(keyPath, $".sessage-write-check-{Guid.NewGuid():N}.tmp"),
                   FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
        var applicationName = configuration["DataProtection:ApplicationName"];
        var protection = services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(keyPath))
            .SetApplicationName(string.IsNullOrWhiteSpace(applicationName) ? "Sessage" : applicationName);

        if (!string.IsNullOrWhiteSpace(certificateThumbprint))
            protection.ProtectKeysWithCertificate(certificateThumbprint);
        else if (OperatingSystem.IsWindows())
            // Machine scope works for IIS pools without a loaded user profile. File ACLs
            // restrict which local identities can read this machine-bound key ring.
            protection.ProtectKeysWithDpapi(protectToLocalMachine: true);
    }

    internal static string? ResolveKeyPath(string? configuredPath, string contentRoot, bool defaultStoreAvailable)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
            return defaultStoreAvailable ? null : Path.Combine(contentRoot, "App_Data", "dataprotection-keys");
        return Path.GetFullPath(configuredPath, contentRoot);
    }
}
