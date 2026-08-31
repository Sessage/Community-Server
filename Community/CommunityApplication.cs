using Klassenbibliothek.Data;
using Klassenbibliothek.Hubs;
using Klassenbibliothek.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.IdentityModel.Tokens;
using TodoSuite.Server.OpenApi;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using TodoSuite.Server.Auth;
using TodoSuite.Server.Components;
using TodoSuite.Server.Components.Account;
using TodoSuite.Server.Services;
using TodoSuite.Server.Services.Sharing;
using TodoSuite.Community.Modularity;
using Klassenbibliothek.Features;
using Klassenbibliothek.Localization;
using TodoSuite.Server.Features;
using System.Globalization;

namespace TodoSuite.Server;

/// <summary>
/// Composition root for both server products. It owns middleware ordering and Community
/// defaults, then lets optional modules replace services and map endpoints without creating
/// a Community-to-Enterprise dependency.
/// </summary>
public static class CommunityApplication
{
    public static async Task<WebApplication> BuildAsync(
        string[] args,
        IEnumerable<IApplicationModule>? applicationModules = null)
    {
        // Materialize once: modules participate in both service registration and endpoint
        // mapping, and an iterator with side effects must not produce two different sets.
        var modules = applicationModules?.ToArray() ?? [];
        const long MaxMobileAttachmentRequestBytes = 51L * 1024 * 1024;
        
        var publishedContentRoot = AppContext.BaseDirectory;
        var developmentProjectRoot = Path.GetFullPath(Path.Combine(publishedContentRoot, "..", "..", ".."));
        var isDevelopmentBuildOutput = File.Exists(Path.Combine(developmentProjectRoot, "TodoSuite.Community.csproj"))
                                       || File.Exists(Path.Combine(developmentProjectRoot, "TodoSuite.Enterprise.Server.csproj"));
        var contentRoot = isDevelopmentBuildOutput
            ? developmentProjectRoot
            : File.Exists(Path.Combine(publishedContentRoot, "appsettings.json"))
                ? publishedContentRoot
                : Directory.GetCurrentDirectory();
        
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = contentRoot
        });

        // Enables clean start/stop semantics when a self-contained Windows package is
        // registered with the Service Control Manager. It is a no-op for console/Linux runs.
        builder.Host.UseWindowsService(options =>
            options.ServiceName = builder.Configuration["Service:Name"] ?? "Sessage");

        var dataProtectionKeyPath = builder.Configuration["DataProtection:KeyPath"];
        if (!string.IsNullOrWhiteSpace(dataProtectionKeyPath))
        {
            var resolvedKeyPath = Path.IsPathRooted(dataProtectionKeyPath)
                ? dataProtectionKeyPath
                : Path.GetFullPath(dataProtectionKeyPath, contentRoot);
            Directory.CreateDirectory(resolvedKeyPath);
            builder.Services.AddDataProtection()
                .PersistKeysToFileSystem(new DirectoryInfo(resolvedKeyPath))
                .SetApplicationName("Sessage");
        }

        // The default Windows EventLog provider may not be writable for a regular
        // desktop user. A warning would then throw and abort the complete host
        // startup. Interactive starts already have console/debug logging; keep the
        // EventLog provider available for non-interactive Windows service hosts.
        if (OperatingSystem.IsWindows() && Environment.UserInteractive)
            builder.Logging.AddFilter<Microsoft.Extensions.Logging.EventLog.EventLogLoggerProvider>(
                null,
                LogLevel.None);
        
        // --- Localization ---
        builder.Services.AddLocalization(options => options.ResourcesPath = "");
        builder.Services.Configure<RequestLocalizationOptions>(options =>
        {
            var supportedCultures = UserLanguagePreferences.SupportedCultures.ToArray();
            options.SetDefaultCulture("de")
                   .AddSupportedCultures(supportedCultures)
                   .AddSupportedUICultures(supportedCultures);
            options.ApplyCurrentCultureToResponseHeaders = true;
        });
        
        builder.Services.AddRazorComponents().AddInteractiveServerComponents();
        builder.Services.AddFluentUIComponents();
        builder.Services.AddSignalR();
        // A dependency-free liveness endpoint is consumed by Docker Compose and monitoring.
        // Database readiness is established during startup because migrations run before the
        // request pipeline becomes available.
        builder.Services.AddHealthChecks();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = MaxMobileAttachmentRequestBytes;
        });
        builder.Services.AddControllers()
            .AddJsonOptions(options =>
            {
                options.JsonSerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
            });
        builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
        {
            options.MultipartBodyLengthLimit = MaxMobileAttachmentRequestBytes;
        });
        
        if (builder.Environment.IsDevelopment())
        {
            builder.Services.AddSessageSwagger();
        }
        
        builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options =>
        {
            options.SerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
        });
        
        builder.Services.AddScoped<ITodoListService, TodoListService>();
        builder.Services.AddSingleton<IProductFeatureCatalog, CommunityProductFeatureCatalog>();
        builder.Services.AddScoped<NavigationRefreshNotifier>();
        builder.Services.AddScoped<ITodoTaskService, TodoTaskService>();
        builder.Services.AddScoped<ITodoAutomationService, CommunityAutomationService>();
        builder.Services.AddScoped<ITodoFormService, CommunityFormService>();
        builder.Services.AddScoped<IListEmailImportService, CommunityEmailImportService>();
        builder.Services.AddScoped<IDashboardService, CommunityDashboardService>();
        builder.Services.AddScoped<IPortfolioSharingService, CommunityPortfolioSharingService>();
        builder.Services.AddScoped<IDirectorySharingService, CommunityDirectorySharingService>();
        builder.Services.AddScoped<IDirectoryIdentitySynchronizer, NoOpDirectoryIdentitySynchronizer>();
        builder.Services.AddScoped<ITodoListPreferencesService, TodoListPreferencesService>();
        builder.Services.AddScoped<PersonalAccessTokenService>();
        builder.Services.AddScoped<UserAccountArtifactCleanupService>();
        builder.Services.AddScoped<ITodoColumnService, TodoColumnService>();
        builder.Services.AddScoped<ITodoAttachmentService, TodoAttachmentService>();
        builder.Services.AddScoped<ITodoCommentService, TodoCommentService>();
        builder.Services.AddScoped<ITodoStepService, TodoStepService>();
        builder.Services.AddScoped<ITodoLabelService, TodoLabelService>();
        builder.Services.AddScoped<ITodoCustomFieldService, CommunityCustomFieldService>();
        builder.Services.AddScoped<ITodoTableColumnOrderService, TodoTableColumnOrderService>();
        builder.Services.AddSingleton<AdminSettingsService>();
        builder.Services.AddSingleton<Klassenbibliothek.Administration.ICentralAdministrationPolicy, CommunityCentralAdministrationPolicy>();
        builder.Services.AddSingleton<Klassenbibliothek.Administration.IAuditEventSink, NoOpAuditEventSink>();
        builder.Services.AddSingleton<UserDirectoryService>();
        
        var adOptions = builder.Configuration.GetSection("ActiveDirectory").Get<ActiveDirectoryOptions>() ?? new ActiveDirectoryOptions();
        builder.Services.AddSingleton(adOptions);
        builder.Services.AddScoped<LdapAuthService>();
        
        builder.Services.AddCascadingAuthenticationState();
        builder.Services.AddScoped<IdentityRedirectManager>();
        builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();
        builder.Services.AddHostedService<ReminderDispatcherService>();
        builder.Services.AddScoped<ITodoTrashService, TodoTrashService>();
        builder.Services.AddScoped<ISearchService, SearchService>();
        builder.Services.AddHostedService<TrashCleanupService>();
        builder.Services.AddScoped<ITaskMemberService, TaskMemberService>();
        
        builder.Services.Configure<SmtpOptions>(builder.Configuration.GetSection("Smtp"));
        var smtpOptions = builder.Configuration.GetSection("Smtp").Get<SmtpOptions>() ?? new SmtpOptions();
        if (!builder.Environment.IsDevelopment()
            && (!string.IsNullOrWhiteSpace(smtpOptions.Host) || builder.Configuration.GetValue("AllowRegistration", false))
            && (!Uri.TryCreate(smtpOptions.AppBaseUrl, UriKind.Absolute, out var publicBaseUri)
                || (publicBaseUri.Scheme != Uri.UriSchemeHttps && publicBaseUri.Scheme != Uri.UriSchemeHttp)))
        {
            throw new InvalidOperationException(
                "Smtp:AppBaseUrl muss bei aktiviertem SMTP oder Selbstregistrierung als absolute HTTP-/HTTPS-Adresse gesetzt sein.");
        }
        builder.Services.AddScoped<IListSharingService, ListSharingService>();
        builder.Services.AddScoped<INotificationService, NotificationService>();
        builder.Services.AddSingleton<IPushNotificationDispatcher, NoOpPushNotificationDispatcher>();
        builder.Services.AddSingleton<AuthAttemptProtectionService>();
        builder.Services.Configure<ClientCompatibilityOptions>(builder.Configuration.GetSection("ClientCompatibility"));
        builder.Services.AddSingleton<ClientCompatibilityService>();
        
        var jwtKey = GetConfigurationValue(
            builder.Configuration,
            "Jwt:Key",
            "JWT_KEY",
            "JwtKey") ?? "SessageMobileDevelopmentKey-ChangeInProduction";
        var jwtIssuer = GetConfigurationValue(
            builder.Configuration,
            "Jwt:Issuer",
            "JWT_ISSUER",
            "JwtIssuer") ?? "Sessage.Server";
        var jwtAudience = GetConfigurationValue(
            builder.Configuration,
            "Jwt:Audience",
            "JWT_AUDIENCE",
            "JwtAudience") ?? "Sessage.App";
        var configuredJwtLifetime = GetConfigurationValue(
            builder.Configuration,
            "Jwt:ExpiresMinutes",
            "JWT_EXPIRES_MINUTES",
            "JwtExpiresMinutes");
        var jwtExpiresMinutes = JwtTokenOptions.DefaultExpiresMinutes;
        if (configuredJwtLifetime is not null
            && (!int.TryParse(configuredJwtLifetime, out jwtExpiresMinutes)
                || jwtExpiresMinutes < 1
                || jwtExpiresMinutes > JwtTokenOptions.MaxExpiresMinutes))
        {
            throw new InvalidOperationException(
                $"Jwt:ExpiresMinutes muss zwischen 1 und {JwtTokenOptions.MaxExpiresMinutes} liegen.");
        }
        const string developmentJwtKey = "SessageMobileDevelopmentKey-ChangeInProduction";

        if (string.IsNullOrWhiteSpace(jwtKey) || Encoding.UTF8.GetByteCount(jwtKey) < 32)
        {
            throw new InvalidOperationException("Jwt:Key muss mindestens 32 Bytes lang sein.");
        }

        if (!builder.Environment.IsDevelopment() && jwtKey == developmentJwtKey)
        {
            throw new InvalidOperationException(
                "Jwt:Key muss in Production auf einen individuellen geheimen Wert gesetzt sein. " +
                "Unter Linux/systemd als Umgebungsvariable bitte Jwt__Key oder JWT_KEY verwenden.");
        }

        builder.Services.AddSingleton(new JwtTokenOptions
        {
            Key = jwtKey,
            Issuer = jwtIssuer,
            Audience = jwtAudience,
            ExpiresMinutes = jwtExpiresMinutes
        });
        
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy("auth", httpContext =>
            {
                var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                var path = httpContext.Request.Path.Value ?? string.Empty;
                return RateLimitPartition.GetFixedWindowLimiter(
                    $"{ip}:{path}",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 10,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                        AutoReplenishment = true
                    });
            });
        });
        
        builder.Services
            .AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = IdentityConstants.ApplicationScheme;
                options.DefaultChallengeScheme = IdentityConstants.ApplicationScheme;
                options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
            })
            .AddJwtBearer("MobileBearer", options =>
            {
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        var accessToken = context.Request.Query["access_token"];
                        var path = context.HttpContext.Request.Path;
        
                        if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs/todo"))
                        {
                            context.Token = accessToken;
                        }

                        return Task.CompletedTask;
                    },
                    OnTokenValidated = async context =>
                    {
                        var userId = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
                        var tokenStamp = context.Principal?.FindFirstValue(JwtTokenOptions.SecurityStampClaimType);
                        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(tokenStamp))
                        {
                            context.Fail("The mobile token does not contain a valid user binding.");
                            return;
                        }

                        var userManager = context.HttpContext.RequestServices.GetRequiredService<UserManager<ApplicationUser>>();
                        var user = await userManager.FindByIdAsync(userId);
                        if (user is null || await userManager.IsLockedOutAsync(user))
                        {
                            context.Fail("The mobile token user is unavailable or locked.");
                            return;
                        }

                        var currentStamp = await userManager.GetSecurityStampAsync(user);
                        if (!string.Equals(tokenStamp, currentStamp, StringComparison.Ordinal))
                        {
                            context.Fail("The mobile token has been revoked.");
                            return;
                        }

                        var tokenIsAdmin = context.Principal?.IsInRole("Admin") == true;
                        var userIsAdmin = await userManager.IsInRoleAsync(user, "Admin");
                        if (tokenIsAdmin != userIsAdmin)
                            context.Fail("The mobile token roles are no longer current.");
                    }
                };
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateIssuerSigningKey = true,
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    RequireSignedTokens = true,
                    ValidIssuer = jwtIssuer,
                    ValidAudience = jwtAudience,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    ClockSkew = TimeSpan.FromMinutes(1)
                };
            })
            .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, PersonalAccessTokenAuthHandler>(
                PersonalAccessTokenAuthHandler.SchemeName, _ => { })
            .AddIdentityCookies(); // <- danach
        
        
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy("MobileApi", policy =>
            {
                policy.AddAuthenticationSchemes("MobileBearer", IdentityConstants.ApplicationScheme, PersonalAccessTokenAuthHandler.SchemeName);
                policy.RequireAuthenticatedUser();
            });
            options.AddPolicy("MobileApiAdmin", policy =>
            {
                policy.AddAuthenticationSchemes("MobileBearer", IdentityConstants.ApplicationScheme, PersonalAccessTokenAuthHandler.SchemeName);
                policy.RequireAuthenticatedUser();
                policy.RequireRole("Admin");
            });
        });
        
        var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            if (!builder.Environment.IsDevelopment())
                throw new InvalidOperationException("Connection string 'DefaultConnection' must be configured in Production.");

            connectionString = "Host=localhost;Port=5432;Database=sessage;Username=postgres;Password=secret";
        }
        void ConfigureDbContext(DbContextOptionsBuilder options) => options.UseNpgsql(connectionString);
        
        builder.Services.AddHttpClient();
        builder.Services.AddScoped(sp =>
        {
            var nav = sp.GetRequiredService<NavigationManager>();
            return new HttpClient { BaseAddress = new Uri(nav.BaseUri) };
        });
        
        builder.Services.AddDbContextFactory<ApplicationDbContext>(ConfigureDbContext);
        builder.Services.AddDatabaseDeveloperPageExceptionFilter();
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
        
        builder.Services.AddIdentityCore<ApplicationUser>(options =>
            {
                options.SignIn.RequireConfirmedAccount = true;
                options.User.RequireUniqueEmail = true;
                options.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
                options.Lockout.AllowedForNewUsers = true;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            })
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<ApplicationDbContext>()
            .AddSignInManager()
            .AddDefaultTokenProviders();
        
        builder.Services.AddScoped<IEmailSender, SmtpEmailSender>();
        builder.Services.AddScoped<Microsoft.AspNetCore.Identity.IEmailSender<ApplicationUser>, SmtpEmailSender>();
        builder.Services.AddScoped<ITodoNavigationService, TodoNavigationService>();
        builder.Services.AddScoped<ITodoCurrentUserService, ServerTodoCurrentUserService>();
        builder.Services.AddScoped<ITodoHubConnectionFactory, ServerTodoHubConnectionFactory>();
        builder.Services.AddScoped<FloatingLayerService>();
        builder.Services.AddHttpContextAccessor();
        
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders =
                ForwardedHeaders.XForwardedFor |
                ForwardedHeaders.XForwardedProto |
                ForwardedHeaders.XForwardedHost;
        
            var forwardedHeadersSection = builder.Configuration.GetSection("ForwardedHeaders");
            var knownProxies = forwardedHeadersSection.GetSection("KnownProxies").Get<string[]>() ?? [];
            var knownNetworks = forwardedHeadersSection.GetSection("KnownNetworks").Get<string[]>() ?? [];
            var trustAllProxies = forwardedHeadersSection.GetValue("TrustAllProxies", false);
        
            if (trustAllProxies || knownProxies.Length > 0 || knownNetworks.Length > 0)
            {
                options.KnownIPNetworks.Clear();
                options.KnownProxies.Clear();
            }
        
            foreach (var proxy in knownProxies)
            {
                if (IPAddress.TryParse(proxy, out var address))
                    options.KnownProxies.Add(address);
            }
        
            foreach (var network in knownNetworks)
            {
                if (TryParseIpNetwork(network, out var ipNetwork))
                    options.KnownIPNetworks.Add(ipNetwork);
            }
        });
        
        foreach (var module in modules)
            module.ConfigureServices(builder);

        var app = builder.Build();
        
        var forwardedHeadersRuntimeSection = app.Configuration.GetSection("ForwardedHeaders");
        var forwardedKnownProxies = forwardedHeadersRuntimeSection.GetSection("KnownProxies").Get<string[]>() ?? [];
        var forwardedKnownNetworks = forwardedHeadersRuntimeSection.GetSection("KnownNetworks").Get<string[]>() ?? [];
        var forwardedTrustAll = forwardedHeadersRuntimeSection.GetValue("TrustAllProxies", false);
        if (forwardedTrustAll)
        {
            app.Logger.LogWarning(
                "ForwardedHeaders:TrustAllProxies ist aktiviert. Das ist nur sicher, wenn die App ausschliesslich hinter einem vertrauenswuerdigen Reverse Proxy erreichbar ist.");
        }
        
        app.UseForwardedHeaders();
        
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.Database.Migrate();
        
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        
            const string adminRole = "Admin";
            if (!await roleManager.RoleExistsAsync(adminRole))
                IdentityBootstrapGuard.EnsureSucceeded(
                    await roleManager.CreateAsync(new IdentityRole(adminRole)),
                    "Admin-Rolle konnte nicht erstellt werden");
        
            var adminEmail = GetConfigurationValue(app.Configuration, "InitialAdmin:Email", "INITIAL_ADMIN_EMAIL") ?? "admin@sessage.local";
            var existingAdmin = await userManager.FindByEmailAsync(adminEmail);
            if (existingAdmin is null)
            {
                var configuredPassword = GetConfigurationValue(app.Configuration, "InitialAdmin:Password", "INITIAL_ADMIN_PASSWORD");
                var writePasswordFile = app.Configuration.GetValue("InitialAdmin:WritePasswordFile", app.Environment.IsDevelopment());
                if (!app.Environment.IsDevelopment()
                    && string.IsNullOrWhiteSpace(configuredPassword)
                    && !writePasswordFile)
                {
                    throw new InvalidOperationException(
                        "Es existiert kein Admin-Benutzer. Setze InitialAdmin:Password bzw. INITIAL_ADMIN_PASSWORD " +
                        "oder aktiviere InitialAdmin:WritePasswordFile bewusst für das Bootstrap-Passwort.");
                }
        
                var password = string.IsNullOrWhiteSpace(configuredPassword)
                    ? GenerateStrongPassword(48)
                    : configuredPassword;
                var adminUser = new ApplicationUser
                {
                    UserName = adminEmail,
                    Email = adminEmail,
                    EmailConfirmed = true
                };
        
                var createResult = await userManager.CreateAsync(adminUser, password);
                IdentityBootstrapGuard.EnsureSucceeded(
                    createResult,
                    "Initialer Admin-Benutzer konnte nicht erstellt werden");
                existingAdmin = adminUser;

                if (writePasswordFile && string.IsNullOrWhiteSpace(configuredPassword))
                {
                    var passwordFilePath = Path.Combine(app.Environment.ContentRootPath, "bitte_loeschen.txt");
                    await File.WriteAllTextAsync(passwordFilePath,
                        $"Initiales Admin-Passwort\n" +
                        $"========================\n" +
                        $"E-Mail:   {adminEmail}\n" +
                        $"Passwort: {password}\n\n" +
                        $"!!! BITTE DIESE DATEI NACH DEM ERSTEN LOGIN LOESCHEN !!!\n" +
                        $"Erstellt am: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n");
                }
            }

            if (!await userManager.IsInRoleAsync(existingAdmin, adminRole))
            {
                IdentityBootstrapGuard.EnsureSucceeded(
                    await userManager.AddToRoleAsync(existingAdmin, adminRole),
                    "Initialer Admin-Benutzer konnte der Admin-Rolle nicht zugewiesen werden");
            }
        }
        
        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI(options =>
            {
                options.SwaggerEndpoint("/swagger/v1/swagger.json", "Sessage API v1");
                options.RoutePrefix = "swagger";
            });
            app.UseMigrationsEndPoint();
        }
        else
        {
            app.UseExceptionHandler("/Error", createScopeForErrors: true);
            app.UseHsts();
        }

        // A form rendered before a deployment can still carry an antiforgery token from
        // the previous Data Protection key ring. Refresh that browser state once instead
        // of showing the generic production error page. The middleware only handles the
        // precise validation exception; APIs and unrelated failures remain untouched.
        app.UseMiddleware<StaleAntiforgeryCookieRecoveryMiddleware>();
        
        app.UseRequestLocalization();
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/api/mobile")
                && !context.Request.Path.StartsWithSegments("/api/mobile/client-compatibility"))
            {
                var compatibility = context.RequestServices.GetRequiredService<ClientCompatibilityService>();
                var version = context.Request.Headers["X-Sessage-App-Version"].FirstOrDefault();
                var result = compatibility.Check(version);
        
                context.Response.Headers["X-Sessage-Latest-Version"] = result.LatestVersion ?? "";
                context.Response.Headers["X-Sessage-Min-Supported-Version"] = result.MinSupportedVersion ?? "";
                context.Response.Headers["X-Sessage-Update-Url"] = result.UpdateUrl ?? "";
                if (result.UpdateAvailable)
                    context.Response.Headers["X-Sessage-Update-Available"] = "true";
        
                if (result.UpdateRequired)
                {
                    context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsJsonAsync(result);
                    return;
                }
            }
        
            await next();
        });
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/api/mobile"))
                context.Features.Get<IStatusCodePagesFeature>()?.Enabled = false;
        
            try
            {
                await next();
            }
            catch (BadHttpRequestException ex) when (context.Request.Path.StartsWithSegments("/api/mobile"))
            {
                if (!context.Response.HasStarted)
                {
                    app.Logger.LogWarning(ex, "Mobile request could not be read.");
                    context.Response.Clear();
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await context.Response.WriteAsync("Mobile-Anfrage konnte nicht gelesen werden.");
                }
            }
            catch (InvalidDataException ex) when (context.Request.Path.StartsWithSegments("/api/mobile"))
            {
                if (!context.Response.HasStarted)
                {
                    app.Logger.LogWarning(ex, "Mobile upload contains invalid data.");
                    context.Response.Clear();
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await context.Response.WriteAsync("Mobile-Upload konnte nicht gelesen werden.");
                }
            }
        });
        app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
        app.UseHttpsRedirection();
        app.UseAntiforgery();
        app.UseRateLimiter();
        app.UseAuthentication();
        app.Use(async (context, next) =>
        {
            if (context.User.Identity?.IsAuthenticated == true
                && HttpMethods.IsGet(context.Request.Method)
                && context.Request.GetTypedHeaders().Accept?.Any(mediaType =>
                    string.Equals(mediaType.MediaType.Value, "text/html", StringComparison.OrdinalIgnoreCase)) == true)
            {
                var userId = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                if (!string.IsNullOrWhiteSpace(userId))
                {
                    var userManager = context.RequestServices.GetRequiredService<UserManager<ApplicationUser>>();
                    var user = await userManager.FindByIdAsync(userId);
                    var preferredLanguage = UserLanguagePreferences.Normalize(user?.PreferredLanguage);
                    var effectiveLanguage = preferredLanguage ?? ResolveBrowserLanguage(context);
                    var culture = CultureInfo.GetCultureInfo(effectiveLanguage);
                    CultureInfo.CurrentCulture = culture;
                    CultureInfo.CurrentUICulture = culture;

                    if (preferredLanguage is not null)
                    {
                        context.Response.Cookies.Append(
                            CookieRequestCultureProvider.DefaultCookieName,
                            CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)),
                            LanguageCookieOptions(context));
                    }
                    else if (context.Request.Cookies.ContainsKey(CookieRequestCultureProvider.DefaultCookieName))
                    {
                        context.Response.Cookies.Delete(
                            CookieRequestCultureProvider.DefaultCookieName,
                            new CookieOptions { Path = "/" });
                    }
                }
            }

            await next();
        });
        app.UseAuthorization();
        app.Use(async (context, next) =>
        {
            var patIdentity = context.User.Identities.FirstOrDefault(identity =>
                identity.IsAuthenticated && identity.AuthenticationType == PersonalAccessTokenAuthHandler.SchemeName);
            var hasOtherIdentity = context.User.Identities.Any(identity =>
                identity.IsAuthenticated && identity.AuthenticationType != PersonalAccessTokenAuthHandler.SchemeName);
            var isWriteRequest = !HttpMethods.IsGet(context.Request.Method)
                                 && !HttpMethods.IsHead(context.Request.Method)
                                 && !HttpMethods.IsOptions(context.Request.Method);

            if (patIdentity is not null
                && !hasOtherIdentity
                && isWriteRequest
                && context.Request.Path.StartsWithSegments("/api")
                && !patIdentity.HasClaim("pat:write", "true"))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsync("Dieser Zugriffstoken besitzt nur Leserechte.");
                return;
            }

            await next();
        });
        app.UseMiddleware<ProductFeatureGateMiddleware>();
        
        app.Use(async (context, next) =>
        {
            try
            {
                await next();
            }
            catch (UnauthorizedAccessException ex) when (context.Request.Path.StartsWithSegments("/api/mobile"))
            {
                app.Logger.LogWarning(ex, "Unauthorized mobile API access.");
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsync("Zugriff verweigert.");
            }
            catch (ArgumentException ex) when (context.Request.Path.StartsWithSegments("/api/mobile"))
            {
                app.Logger.LogWarning(ex, "Invalid mobile API request.");
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("Die Anfrage ist ungültig.");
            }
            catch (InvalidOperationException ex) when (context.Request.Path.StartsWithSegments("/api/mobile"))
            {
                app.Logger.LogWarning(ex, "Mobile API operation failed.");
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("Die Aktion konnte nicht ausgefuehrt werden.");
            }
        });
        
        app.MapStaticAssets();
        var componentEndpoints = app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
        if (modules.Length > 0)
            componentEndpoints.AddAdditionalAssemblies(modules.Select(x => x.Assembly).Distinct().ToArray());
        app.MapControllers();
        app.MapHealthChecks("/healthz");
        app.MapHub<TodoHubEndpoint>("/hubs/todo");
        app.MapAdditionalIdentityEndpoints();
        foreach (var module in modules)
            module.MapEndpoints(app);
        static string? GetConfigurationValue(IConfiguration configuration, params string[] keys)
        {
            foreach (var key in keys)
            {
                var value = configuration[key];
                if (!string.IsNullOrWhiteSpace(value))
                    return value.Trim();
            }
        
            return null;
        }
        
        static string GenerateStrongPassword(int length = 48)
        {
            const string upper = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
            const string lower = "abcdefghijklmnopqrstuvwxyz";
            const string digits = "0123456789";
            const string special = "!@#$%^&*()_+-=[]{}|;<>?";
            const string all = upper + lower + digits + special;
        
            using var rng = System.Security.Cryptography.RandomNumberGenerator.Create();
            var randomBytes = new byte[length + 4];
            rng.GetBytes(randomBytes);
        
            var pwd = new List<char>
            {
                upper[randomBytes[0] % upper.Length],
                lower[randomBytes[1] % lower.Length],
                digits[randomBytes[2] % digits.Length],
                special[randomBytes[3] % special.Length],
            };
        
            for (var i = 4; i < length; i++)
                pwd.Add(all[randomBytes[i] % all.Length]);
        
            // Fisher-Yates shuffle using additional random bytes
            var shuffleBytes = new byte[pwd.Count];
            rng.GetBytes(shuffleBytes);
            for (var i = pwd.Count - 1; i > 0; i--)
            {
                var j = shuffleBytes[i] % (i + 1);
                (pwd[i], pwd[j]) = (pwd[j], pwd[i]);
            }
        
            return new string([.. pwd]);
        }
        
        static bool TryParseIpNetwork(string? value, out System.Net.IPNetwork network)
        {
            network = default!;
            if (string.IsNullOrWhiteSpace(value))
                return false;
        
            return System.Net.IPNetwork.TryParse(value.Trim(), out network);
        }
        
        
        
        // GET endpoint for "forget browser" - cannot be done via WebSocket in InteractiveServer,
        // so we use a plain HTTP GET that writes the cookie and redirects back.
        app.MapGet("/Account/Manage/ForgetBrowserAction", async (
            SignInManager<ApplicationUser> signInManager,
            UserManager<ApplicationUser> userManager,
            HttpContext httpContext) =>
        {
            var user = await userManager.GetUserAsync(httpContext.User);
            if (user is not null)
                await signInManager.ForgetTwoFactorClientAsync();
        
            return Results.Redirect("/Account/Manage/TwoFactorAuthentication");
        }).RequireAuthorization();

        app.MapGet("/Account/ApplyLanguage", (
            string? culture,
            string? returnUrl,
            HttpContext context) =>
        {
            if (!UserLanguagePreferences.TryNormalize(culture, out var preferredLanguage))
                return Results.BadRequest();
            if (preferredLanguage is null)
            {
                context.Response.Cookies.Delete(
                    CookieRequestCultureProvider.DefaultCookieName,
                    new CookieOptions { Path = "/" });
            }
            else
            {
                var selectedCulture = CultureInfo.GetCultureInfo(preferredLanguage);
                context.Response.Cookies.Append(
                    CookieRequestCultureProvider.DefaultCookieName,
                    CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(selectedCulture)),
                    LanguageCookieOptions(context));
            }

            var safeReturnUrl = !string.IsNullOrWhiteSpace(returnUrl)
                                && returnUrl.StartsWith("/", StringComparison.Ordinal)
                                && !returnUrl.StartsWith("//", StringComparison.Ordinal)
                ? returnUrl
                : "/konto";
            return Results.LocalRedirect(safeReturnUrl);
        }).RequireAuthorization();
        
        return app;
        
        
    }

    private static CookieOptions LanguageCookieOptions(HttpContext context) => new()
    {
        Expires = DateTimeOffset.UtcNow.AddYears(1),
        HttpOnly = false,
        IsEssential = true,
        Path = "/",
        SameSite = SameSiteMode.Lax,
        Secure = context.Request.IsHttps
    };

    private static string ResolveBrowserLanguage(HttpContext context)
    {
        var acceptedLanguages = context.Request.GetTypedHeaders().AcceptLanguage?
            .OrderByDescending(value => value.Quality ?? 1)
            .Select(value => value.Value.Value);
        foreach (var acceptedLanguage in acceptedLanguages ?? [])
        {
            if (string.IsNullOrWhiteSpace(acceptedLanguage))
                continue;
            var normalized = UserLanguagePreferences.Normalize(acceptedLanguage);
            if (normalized is not null)
                return normalized;

            try
            {
                normalized = UserLanguagePreferences.Normalize(CultureInfo.GetCultureInfo(acceptedLanguage).TwoLetterISOLanguageName);
                if (normalized is not null)
                    return normalized;
            }
            catch (CultureNotFoundException)
            {
                // Ignore malformed browser language entries and try the next one.
            }
        }

        return "de";
    }
}
