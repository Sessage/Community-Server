using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

namespace TodoSuite.Server.OpenApi;

public static class SwaggerServiceCollectionExtensions
{
    public static IServiceCollection AddSessageSwagger(this IServiceCollection services)
    {
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "Sessage API",
                Version = "v1",
                Description = "REST-API für Sessage - Mobile-Sync, Authentifizierung und Admin-Verwaltung.\n\n" +
                              "Authentifizierung via JWT Bearer (POST /api/mobile/auth/login) oder Personal Access Token."
            });

            options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
            {
                Name = "Authorization",
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                In = ParameterLocation.Header,
                Description = "JWT-Token aus POST /api/mobile/auth/login. Format: Bearer {token}"
            });

            options.AddSecurityRequirement(openApiDocument => new OpenApiSecurityRequirement
            {
                {
                    new OpenApiSecuritySchemeReference("Bearer", openApiDocument),
                    []
                }
            });

            var assembly = typeof(CommunityApplication).Assembly;
            var xmlPath = Path.ChangeExtension(assembly.Location, ".xml");
            if (File.Exists(xmlPath))
                options.IncludeXmlComments(xmlPath);
        });

        return services;
    }
}
