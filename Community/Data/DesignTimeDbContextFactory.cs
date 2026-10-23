using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Klassenbibliothek.Data;

/// <summary>Schema tooling never starts the application or reads customer secrets.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    private static readonly IServiceProvider IdentityServices = CreateIdentityServices();
    private static IServiceProvider CreateIdentityServices()
    {
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
        var services = new ServiceCollection();
        services.AddIdentityCore<ApplicationUser>(options => options.Stores.SchemaVersion = IdentitySchemaVersions.Version3);
        return services.BuildServiceProvider();
    }

    public ApplicationDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<ApplicationDbContext>().UseApplicationServiceProvider(IdentityServices).UseNpgsql(
            Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
            ?? "Host=127.0.0.1;Database=sessage_schema;Username=sessage_schema").Options);
}
