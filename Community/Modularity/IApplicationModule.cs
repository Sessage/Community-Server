using System.Reflection;
using Microsoft.AspNetCore.Builder;

namespace TodoSuite.Community.Modularity;

/// <summary>
/// Stable extension contract used by product hosts to compose optional server modules.
/// It deliberately lives in the server project so mobile clients do not inherit the
/// ASP.NET Core shared framework through the shared UI assembly.
/// </summary>
public interface IApplicationModule
{
    Assembly Assembly { get; }
    void ConfigureServices(WebApplicationBuilder builder);
    void MapEndpoints(WebApplication app);
}
