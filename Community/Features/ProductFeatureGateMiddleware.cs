using Klassenbibliothek.Features;

namespace TodoSuite.Server.Features;

/// <summary>Server-side enforcement; UI capability checks are only presentation.</summary>
public sealed class ProductFeatureGateMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IProductFeatureCatalog features)
    {
        var feature = ResolveRequiredFeature(context.Request.Path);
        if (feature is not null && !features.IsEnabled(feature))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            if (context.Request.Path.StartsWithSegments("/api"))
                await context.Response.WriteAsJsonAsync(new { error = "feature_not_available", feature });
            else
                await context.Response.WriteAsync("Diese Funktion ist in der aktuellen Edition oder Lizenz nicht verfügbar.");
            return;
        }
        await next(context);
    }

    private static string? ResolveRequiredFeature(PathString requestPath)
    {
        var path = requestPath.Value ?? string.Empty;
        if (path.StartsWith("/forms", StringComparison.OrdinalIgnoreCase) || path.Contains("/forms", StringComparison.OrdinalIgnoreCase))
            return ProductFeatureIds.Forms;
        if (path.StartsWith("/dashboards", StringComparison.OrdinalIgnoreCase) || path.Contains("/dashboards", StringComparison.OrdinalIgnoreCase))
            return ProductFeatureIds.Dashboards;
        if (path.StartsWith("/portfolio/", StringComparison.OrdinalIgnoreCase) || path.Contains("/portfolios/", StringComparison.OrdinalIgnoreCase)
            || (path.Contains("/groups/", StringComparison.OrdinalIgnoreCase) && path.EndsWith("/portfolio", StringComparison.OrdinalIgnoreCase)))
            return ProductFeatureIds.Portfolios;
        if (path.Contains("/automations", StringComparison.OrdinalIgnoreCase))
            return ProductFeatureIds.Automation;
        if (path.Contains("/email-import", StringComparison.OrdinalIgnoreCase))
            return ProductFeatureIds.EmailImport;
        if (path.Contains("/custom-fields", StringComparison.OrdinalIgnoreCase))
            return ProductFeatureIds.Forms;
        return null;
    }
}
