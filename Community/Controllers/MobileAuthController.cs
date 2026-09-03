using System.IdentityModel.Tokens.Jwt;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Localization;
using Microsoft.IdentityModel.Tokens;
using Klassenbibliothek.Data;
using Klassenbibliothek.Localization;
using TodoSuite.Server.Services;
using TodoSuite.Server.Auth;
using Klassenbibliothek.Services;
using Klassenbibliothek.Administration;

namespace TodoSuite.Server.Controllers;

/// <summary>
/// Implements the authentication and account-management surface consumed by native clients.
/// It issues short-lived bearer tokens, applies shared login-attempt protection, and keeps
/// account mutations inside ASP.NET Core Identity so lockout and security-stamp rules apply.
/// </summary>
[ApiController]
[Route("api/mobile/auth")]
public class MobileAuthController : ControllerBase
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly IConfiguration _configuration;
    private readonly LdapAuthService _ldapAuth;
    private readonly ActiveDirectoryOptions _adOptions;
    private readonly IStringLocalizer<SharedResource> _localizer;
    private readonly IEmailSender<ApplicationUser> _emailSender;
    private readonly AuthAttemptProtectionService _attemptProtection;
    private readonly IDirectoryIdentitySynchronizer _directoryIdentitySynchronizer;
    private readonly JwtTokenOptions _jwtOptions;
    private readonly ICentralAdministrationPolicy _centralPolicy;
    private readonly IAuditEventSink _audit;
    private readonly ILogger<MobileAuthController> _logger;
    private readonly UserAccountArtifactCleanupService _accountArtifactCleanup;
    private readonly PersonalAccessTokenService _personalAccessTokens;

    public MobileAuthController(
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        IConfiguration configuration,
        LdapAuthService ldapAuth,
        ActiveDirectoryOptions adOptions,
        IStringLocalizer<SharedResource> localizer,
        IEmailSender<ApplicationUser> emailSender,
        AuthAttemptProtectionService attemptProtection,
        IDirectoryIdentitySynchronizer directoryIdentitySynchronizer,
        JwtTokenOptions jwtOptions,
        ICentralAdministrationPolicy centralPolicy,
        IAuditEventSink audit,
        ILogger<MobileAuthController> logger,
        UserAccountArtifactCleanupService accountArtifactCleanup,
        PersonalAccessTokenService personalAccessTokens)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _configuration = configuration;
        _ldapAuth = ldapAuth;
        _adOptions = adOptions;
        _localizer = localizer;
        _emailSender = emailSender;
        _attemptProtection = attemptProtection;
        _directoryIdentitySynchronizer = directoryIdentitySynchronizer;
        _jwtOptions = jwtOptions;
        _centralPolicy = centralPolicy;
        _audit = audit;
        _logger = logger;
        _accountArtifactCleanup = accountArtifactCleanup;
        _personalAccessTokens = personalAccessTokens;
    }

    [HttpGet("config")]
    public ActionResult<ConfigResponse> Config()
    {
        return Ok(new ConfigResponse(_adOptions.Enabled, _centralPolicy.Current.AllowSelfRegistration));
    }

    [HttpPost("login")]
    [EnableRateLimiting("auth")]
    public async Task<ActionResult<LoginResponse>> Login([FromBody] LoginRequest request)
    {
        var loginName = (request.Email ?? string.Empty).Trim();
        var password = request.Password ?? string.Empty;
        // Apply the application-level subject/address throttle before looking up a user. This
        // keeps timing and error behavior independent of whether the account exists.
        var subject = NormalizeLoginSubject(loginName);
        var block = _attemptProtection.Check(HttpContext, subject);
        if (block.IsBlocked)
            return TooManyLoginAttempts(block);

        if (string.IsNullOrWhiteSpace(loginName) || string.IsNullOrEmpty(password))
        {
            _attemptProtection.RecordFailure(HttpContext, subject);
            return Unauthorized();
        }

        ApplicationUser? user;

        if (request.UseAd && _adOptions.Enabled)
        {
            // LDAP-/AD-Authentifizierung: Das Email-Feld enthält den konfigurierten Verzeichnis-Anmeldenamen.
            var adUser = await _ldapAuth.AuthenticateAsync(loginName, password);
            if (adUser is null)
            {
                _attemptProtection.RecordFailure(HttpContext, subject);
                return Unauthorized();
            }

            // Lokalen Benutzer suchen oder anlegen
            user = await _userManager.FindByEmailAsync(adUser.Email);
            if (user is null)
            {
                user = new ApplicationUser
                {
                    UserName = adUser.Email,
                    Email = adUser.Email,
                    EmailConfirmed = true
                };

                var createResult = await _userManager.CreateAsync(user);
                if (!createResult.Succeeded)
                    return StatusCode(500, new RegisterResponse(false, createResult.Errors.Select(e => e.Description).ToArray()));
            }
            await _directoryIdentitySynchronizer.SynchronizeAsync(user.Id, adUser.DirectoryIdentity, HttpContext.RequestAborted);
        }
        else
        {
            // Lokale Authentifizierung
            user = await _userManager.FindByEmailAsync(loginName);
            if (user is null)
            {
                _attemptProtection.RecordFailure(HttpContext, subject);
                return Unauthorized();
            }

            var signIn = await _signInManager.CheckPasswordSignInAsync(user, password, lockoutOnFailure: true);
            if (signIn.IsLockedOut)
                return StatusCode(StatusCodes.Status423Locked, new ErrorResponse("Konto ist voruebergehend gesperrt. Bitte spaeter erneut versuchen."));

            if (!signIn.Succeeded)
            {
                _attemptProtection.RecordFailure(HttpContext, subject);
                return Unauthorized();
            }
        }

        if (await _userManager.IsLockedOutAsync(user))
            return StatusCode(StatusCodes.Status423Locked, new ErrorResponse("Konto ist voruebergehend gesperrt. Bitte spaeter erneut versuchen."));

        var isAdmin = await _userManager.IsInRoleAsync(user, "Admin");
        if (await _userManager.GetTwoFactorEnabledAsync(user))
        {
            // The short-lived challenge grants no API access. It is accepted only by the 2FA
            // endpoint and is bound to the user's current security stamp.
            _attemptProtection.RecordSuccess(HttpContext, subject);
            return Ok(LoginResponse.TwoFactorRequired(await CreateTwoFactorChallengeTokenAsync(user)));
        }

        _attemptProtection.RecordSuccess(HttpContext, subject);
        return Ok(await GenerateJwtTokenAsync(user, loginName, isAdmin));
    }

    [HttpPost("login-2fa")]
    [EnableRateLimiting("auth")]
    public async Task<ActionResult<LoginResponse>> LoginWith2fa([FromBody] LoginWith2faRequest request)
    {
        var principal = ValidateTwoFactorChallengeToken(request.ChallengeToken);
        var userId = principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        var subject = string.IsNullOrWhiteSpace(userId) ? "2fa:unknown" : $"2fa:{userId}";
        var block = _attemptProtection.Check(HttpContext, subject);
        if (block.IsBlocked)
            return TooManyLoginAttempts(block);

        if (string.IsNullOrWhiteSpace(userId))
        {
            _attemptProtection.RecordFailure(HttpContext, subject);
            return Unauthorized();
        }

        var user = await _userManager.FindByIdAsync(userId);
        if (user is null)
        {
            _attemptProtection.RecordFailure(HttpContext, subject);
            return Unauthorized();
        }

        // Rechecking the security stamp invalidates an outstanding challenge after password,
        // recovery, or administrator-driven security changes.
        if (!await IsChallengeValidForUserAsync(principal!, user)
            || !await _userManager.GetTwoFactorEnabledAsync(user))
        {
            _attemptProtection.RecordFailure(HttpContext, subject);
            return Unauthorized();
        }

        if (await _userManager.IsLockedOutAsync(user))
            return StatusCode(StatusCodes.Status423Locked, new ErrorResponse("Konto ist voruebergehend gesperrt. Bitte spaeter erneut versuchen."));

        var code = (request.Code ?? string.Empty).Replace(" ", string.Empty).Replace("-", string.Empty);
        var valid = await _userManager.VerifyTwoFactorTokenAsync(
            user,
            _userManager.Options.Tokens.AuthenticatorTokenProvider,
            code);

        if (!valid)
        {
            var accessFailure = await _userManager.AccessFailedAsync(user);
            if (!accessFailure.Succeeded)
                _logger.LogWarning("Could not record failed mobile 2FA attempt for user {UserId}.", user.Id);
            _attemptProtection.RecordFailure(HttpContext, subject);
            if (await _userManager.IsLockedOutAsync(user))
                return StatusCode(StatusCodes.Status423Locked, new ErrorResponse("Konto ist voruebergehend gesperrt. Bitte spaeter erneut versuchen."));
            return Unauthorized(new ErrorResponse("Der Authenticator-Code ist ungültig."));
        }

        var resetFailures = await _userManager.ResetAccessFailedCountAsync(user);
        if (!resetFailures.Succeeded)
            _logger.LogWarning("Could not reset failed access count after mobile 2FA for user {UserId}.", user.Id);

        var isAdmin = await _userManager.IsInRoleAsync(user, "Admin");
        _attemptProtection.RecordSuccess(HttpContext, subject);
        return Ok(await GenerateJwtTokenAsync(user, user.Email ?? user.UserName ?? string.Empty, isAdmin));
    }

    [HttpPost("register")]
    [EnableRateLimiting("auth")]
    public async Task<ActionResult<RegisterResponse>> Register([FromBody] RegisterRequest request)
    {
        if (!_centralPolicy.Current.AllowSelfRegistration)
            return StatusCode(403, new RegisterResponse(false, [_localizer["Err_Auth_RegistrationNotAllowed"].Value]));

        if (_adOptions.Enabled)
            return StatusCode(403, new RegisterResponse(false, [_localizer["Err_Auth_RegistrationDisabledByAd"].Value]));

        var email = (request.Email ?? string.Empty).Trim();
        var password = request.Password ?? string.Empty;
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password))
            return BadRequest(new RegisterResponse(false, ["E-Mail-Adresse und Passwort müssen angegeben werden."]));

        var existingUser = await _userManager.FindByEmailAsync(email);
        if (existingUser is not null)
            return Conflict(new RegisterResponse(false, [_localizer["Err_Auth_AccountAlreadyExists"].Value]));

        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = false
        };

        var result = await _userManager.CreateAsync(user, password);
        if (result.Succeeded)
        {
            var code = await _userManager.GenerateEmailConfirmationTokenAsync(user);
            code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(code));
            var callbackUrl = QueryHelpers.AddQueryString(
                BuildPublicUrl("/Account/ConfirmEmail"),
                new Dictionary<string, string?>
                {
                    ["userId"] = user.Id,
                    ["code"] = code
                });
            try
            {
                await _emailSender.SendConfirmationLinkAsync(user, email, callbackUrl);
            }
            catch
            {
                // Registration is incomplete without a deliverable confirmation path. Remove
                // the new Identity row instead of leaving an account the user cannot activate.
                await _userManager.DeleteAsync(user);
                return StatusCode(
                    StatusCodes.Status503ServiceUnavailable,
                    new RegisterResponse(false, ["Bestätigungs-E-Mail konnte nicht versendet werden. Bitte später erneut versuchen."]));
            }
            return Ok(new RegisterResponse(true, []));
        }

        return BadRequest(new RegisterResponse(false, result.Errors.Select(error => error.Description).ToArray()));
    }

    [HttpGet("me")]
    [Authorize(Policy = "MobileApi")]
    public async Task<ActionResult<MeResponse>> Me()
    {
        var (userId, user) = await GetCurrentUserAsync();
        if (user is null || string.IsNullOrWhiteSpace(userId)) return Unauthorized();

        var isAdmin = await _userManager.IsInRoleAsync(user, "Admin");
        var hasLocalPassword = await _userManager.HasPasswordAsync(user);
        var emailConfirmed = await _userManager.IsEmailConfirmedAsync(user);
        var twoFactorEnabled = await _userManager.GetTwoFactorEnabledAsync(user);
        var recoveryCodesLeft = await _userManager.CountRecoveryCodesAsync(user);
        var passkeyCount = (await _userManager.GetPasskeysAsync(user)).Count;

        return Ok(new MeResponse(
            userId,
            user.Email ?? string.Empty,
            isAdmin,
            hasLocalPassword,
            emailConfirmed,
            twoFactorEnabled,
            recoveryCodesLeft,
            passkeyCount,
            UserLanguagePreferences.Normalize(user.PreferredLanguage)));
    }

    [HttpPost("language")]
    [Authorize(Policy = "MobileApi")]
    public async Task<IActionResult> SetLanguage([FromBody] LanguagePreferenceRequest request)
    {
        var (_, user) = await GetCurrentUserAsync();
        if (user is null) return Unauthorized();
        if (!UserLanguagePreferences.TryNormalize(request.PreferredLanguage, out var preferredLanguage))
            return BadRequest(new ErrorResponse("Die ausgewählte Sprache wird nicht unterstützt."));

        user.PreferredLanguage = preferredLanguage;
        var result = await _userManager.UpdateAsync(user);
        return result.Succeeded
            ? NoContent()
            : BadRequest(new ErrorResponse(string.Join(" ", result.Errors.Select(error => error.Description))));
    }

    [HttpPost("change-password")]
    [Authorize(Policy = "MobileApi")]
    [ProducesResponseType<LoginResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request)
    {
        var (_, user) = await GetCurrentUserAsync();
        if (user is null) return Unauthorized();

        if (!await _userManager.HasPasswordAsync(user))
            return BadRequest(new ErrorResponse("Dieses Konto verwendet kein lokales Passwort."));

        var result = await _userManager.ChangePasswordAsync(user, request.OldPassword, request.NewPassword);
        if (result.Succeeded)
        {
            var isAdmin = await _userManager.IsInRoleAsync(user, "Admin");
            return Ok(await GenerateJwtTokenAsync(user, user.Email ?? user.Id, isAdmin));
        }

        return BadRequest(new ErrorResponse(string.Join(" ", result.Errors.Select(error => error.Description))));
    }

    [HttpPost("change-email")]
    [Authorize(Policy = "MobileApi")]
    public async Task<IActionResult> ChangeEmail([FromBody] ChangeEmailRequest request)
    {
        var (userId, user) = await GetCurrentUserAsync();
        if (user is null || string.IsNullOrWhiteSpace(userId)) return Unauthorized();

        if (!await _userManager.HasPasswordAsync(user))
            return BadRequest(new ErrorResponse("E-Mail-Adresse wird ueber den Anmeldeanbieter verwaltet."));

        var newEmail = (request.NewEmail ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(newEmail))
            return BadRequest(new ErrorResponse("E-Mail-Adresse darf nicht leer sein."));
        if (!new EmailAddressAttribute().IsValid(newEmail))
            return BadRequest(new ErrorResponse("Bitte eine gültige E-Mail-Adresse angeben."));
        if (string.Equals(user.Email, newEmail, StringComparison.OrdinalIgnoreCase))
            return BadRequest(new ErrorResponse("Die neue E-Mail-Adresse entspricht der aktuellen Adresse."));

        var existing = await _userManager.FindByEmailAsync(newEmail);
        if (existing is not null && existing.Id != user.Id)
            return Conflict(new ErrorResponse("Diese E-Mail-Adresse wird bereits verwendet."));

        var code = await _userManager.GenerateChangeEmailTokenAsync(user, newEmail);
        code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(code));

        var callbackUrl = QueryHelpers.AddQueryString(
            BuildPublicUrl("/Account/ConfirmEmailChange"),
            new Dictionary<string, string?>
            {
                ["userId"] = userId,
                ["email"] = newEmail,
                ["code"] = code
            });

        try
        {
            await _emailSender.SendConfirmationLinkAsync(user, newEmail, callbackUrl);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "E-mail change confirmation for user {UserId} could not be sent.", userId);
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new ErrorResponse("Die Bestätigungs-E-Mail konnte nicht versendet werden. Bitte später erneut versuchen."));
        }
        return NoContent();
    }

    [HttpPost("send-email-confirmation")]
    [Authorize(Policy = "MobileApi")]
    public async Task<IActionResult> SendEmailConfirmation()
    {
        var (userId, user) = await GetCurrentUserAsync();
        if (user is null || string.IsNullOrWhiteSpace(userId)) return Unauthorized();

        var email = await _userManager.GetEmailAsync(user);
        if (string.IsNullOrWhiteSpace(email))
            return BadRequest(new ErrorResponse("Keine E-Mail-Adresse hinterlegt."));

        var code = await _userManager.GenerateEmailConfirmationTokenAsync(user);
        code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(code));

        var callbackUrl = QueryHelpers.AddQueryString(
            BuildPublicUrl("/Account/ConfirmEmail"),
            new Dictionary<string, string?>
            {
                ["userId"] = userId,
                ["code"] = code
            });

        try
        {
            await _emailSender.SendConfirmationLinkAsync(user, email, callbackUrl);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "E-mail confirmation for user {UserId} could not be sent.", userId);
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new ErrorResponse("Die Bestätigungs-E-Mail konnte nicht versendet werden. Bitte später erneut versuchen."));
        }
        return NoContent();
    }

    [HttpPost("forgot-password")]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequest request)
    {
        var email = (request.Email ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(email))
            return BadRequest(new ErrorResponse("E-Mail-Adresse darf nicht leer sein."));

        var user = await _userManager.FindByEmailAsync(email);
        if (user is not null && await _userManager.HasPasswordAsync(user))
        {
            var code = await _userManager.GeneratePasswordResetTokenAsync(user);
            code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(code));
            var callbackUrl = QueryHelpers.AddQueryString(
                BuildPublicUrl("/Account/ResetPassword"),
                new Dictionary<string, string?> { ["code"] = code });
            try
            {
                await _emailSender.SendPasswordResetLinkAsync(user, email, callbackUrl);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Passwort-Reset-E-Mail an Benutzer '{UserId}' konnte nicht versendet werden.", user.Id);
            }
        }

        // Always return the same result to avoid account enumeration.
        return NoContent();
    }

    [HttpGet("personal-data")]
    [Authorize(Policy = "MobileApi")]
    public async Task<IActionResult> DownloadPersonalData()
    {
        var (userId, user) = await GetCurrentUserAsync();
        if (user is null || string.IsNullOrWhiteSpace(userId)) return Unauthorized();
        if (!_centralPolicy.Current.AllowPersonalDataExport)
            return StatusCode(StatusCodes.Status403Forbidden, new ErrorResponse("Der Export persönlicher Daten wurde administrativ deaktiviert."));

        var personalData = new Dictionary<string, string>();
        var properties = typeof(ApplicationUser).GetProperties()
            .Where(property => Attribute.IsDefined(property, typeof(PersonalDataAttribute)));
        foreach (var property in properties)
            personalData[property.Name] = property.GetValue(user)?.ToString() ?? "null";

        foreach (var login in await _userManager.GetLoginsAsync(user))
            personalData[$"{login.LoginProvider} external login provider key"] = login.ProviderKey;
        personalData["Authenticator Key"] = await _userManager.GetAuthenticatorKeyAsync(user) ?? "null";
        personalData["Personal access tokens"] = JsonSerializer.Serialize(
            await _personalAccessTokens.ListAsync(userId, HttpContext.RequestAborted));

        await _audit.RecordAsync("personal-data", "data-exported", userId, cancellationToken: HttpContext.RequestAborted);
        return File(JsonSerializer.SerializeToUtf8Bytes(personalData, new JsonSerializerOptions { WriteIndented = true }),
            "application/json", "PersonalData.json");
    }

    [HttpDelete("personal-data")]
    [Authorize(Policy = "MobileApi")]
    public async Task<IActionResult> DeletePersonalData([FromBody] DeletePersonalDataRequest request)
    {
        var (userId, user) = await GetCurrentUserAsync();
        if (user is null || string.IsNullOrWhiteSpace(userId)) return Unauthorized();
        if (!_centralPolicy.Current.AllowAccountDeletion)
            return StatusCode(StatusCodes.Status403Forbidden, new ErrorResponse("Die Selbstlöschung von Konten wurde administrativ deaktiviert."));
        if (await _userManager.HasPasswordAsync(user)
            && !await _userManager.CheckPasswordAsync(user, request.Password ?? string.Empty))
            return BadRequest(new ErrorResponse("Das eingegebene Passwort ist falsch."));

        var profilePicturePath = user.ProfilePicturePath;
        // Identity deletion is authoritative. External artifacts are removed only afterwards,
        // so a failed account deletion cannot leave a live account without its associated data.
        var result = await _userManager.DeleteAsync(user);
        if (!result.Succeeded)
            return BadRequest(new ErrorResponse(string.Join(" ", result.Errors.Select(error => error.Description))));

        await _accountArtifactCleanup.CleanupAfterUserDeletionAsync(
            userId,
            profilePicturePath,
            CancellationToken.None);
        await _audit.RecordAsync("personal-data", "account-deleted", userId, cancellationToken: HttpContext.RequestAborted);
        return NoContent();
    }

    private async Task<(string? UserId, ApplicationUser? User)> GetCurrentUserAsync()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        if (string.IsNullOrWhiteSpace(userId)) return (null, null);

        var user = await _userManager.FindByIdAsync(userId);
        return (userId, user);
    }

    private async Task<LoginResponse> GenerateJwtTokenAsync(ApplicationUser user, string fallbackName, bool isAdmin)
    {
        var key = _jwtOptions.Key;
        var issuer = _jwtOptions.Issuer;
        var audience = _jwtOptions.Audience;
        var expiresMinutes = _jwtOptions.ExpiresMinutes;

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new(ClaimTypes.NameIdentifier, user.Id),
            new(ClaimTypes.Name, user.UserName ?? fallbackName),
            new(ClaimTypes.Email, user.Email ?? fallbackName),
            new(JwtTokenOptions.SecurityStampClaimType, await _userManager.GetSecurityStampAsync(user))
        };

        if (isAdmin)
            claims.Add(new Claim(ClaimTypes.Role, "Admin"));

        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key));
        var creds = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);
        var expiresAt = DateTime.UtcNow.AddMinutes(expiresMinutes);

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            expires: expiresAt,
            signingCredentials: creds);

        var rawToken = new JwtSecurityTokenHandler().WriteToken(token);
        return LoginResponse.Success(rawToken, expiresAt, user.Id, isAdmin);
    }

    private async Task<string> CreateTwoFactorChallengeTokenAsync(ApplicationUser user)
    {
        var key = _jwtOptions.Key;
        var issuer = _jwtOptions.Issuer;
        var audience = _jwtOptions.Audience;

        // The API authentication handler revalidates the embedded security stamp, allowing
        // password and account-security changes to revoke otherwise unexpired JWTs.
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new(ClaimTypes.NameIdentifier, user.Id),
            new("purpose", "mobile-2fa"),
            new(JwtTokenOptions.SecurityStampClaimType, await _userManager.GetSecurityStampAsync(user))
        };

        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key));
        var creds = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private ClaimsPrincipal? ValidateTwoFactorChallengeToken(string challengeToken)
    {
        try
        {
            var key = _jwtOptions.Key;
            var issuer = _jwtOptions.Issuer;
            var audience = _jwtOptions.Audience;

            var principal = new JwtSecurityTokenHandler().ValidateToken(challengeToken, new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateIssuerSigningKey = true,
                ValidateLifetime = true,
                ValidIssuer = issuer,
                ValidAudience = audience,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
                ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                RequireExpirationTime = true,
                RequireSignedTokens = true,
                ClockSkew = TimeSpan.FromSeconds(30)
            }, out _);

            // A normally signed access token must not be reusable as a 2FA challenge.
            return principal.FindFirstValue("purpose") == "mobile-2fa" ? principal : null;
        }
        catch
        {
            return null;
        }
    }

    private ObjectResult TooManyLoginAttempts(AuthBlockStatus block)
    {
        var retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(block.RetryAfter.TotalSeconds));
        Response.Headers.RetryAfter = retryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);

        return StatusCode(
            StatusCodes.Status429TooManyRequests,
            new ErrorResponse($"Zu viele fehlgeschlagene Anmeldeversuche. Bitte in {FormatRetryAfter(block.RetryAfter)} erneut versuchen."));
    }

    private static string NormalizeLoginSubject(string? value)
        => string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim().ToUpperInvariant();

    private static string FormatRetryAfter(TimeSpan retryAfter)
    {
        if (retryAfter.TotalHours >= 1)
            return $"{Math.Ceiling(retryAfter.TotalHours):0} Stunde(n)";

        if (retryAfter.TotalMinutes >= 1)
            return $"{Math.Ceiling(retryAfter.TotalMinutes):0} Minute(n)";

        return $"{Math.Ceiling(retryAfter.TotalSeconds):0} Sekunde(n)";
    }

    /// <summary>E-Mail-Adresse bei lokalem Login, konfigurierter LDAP-/AD-Anmeldename wenn UseAd=true.</summary>
    public record LoginRequest(string Email, string Password, bool UseAd = false);
    public record ConfigResponse(bool AdEnabled, bool AllowRegistration);
    public record LoginResponse(string? Token, DateTime? ExpiresAtUtc, string? UserId, bool IsAdmin, bool RequiresTwoFactor, string? TwoFactorChallenge)
    {
        public static LoginResponse Success(string token, DateTime expiresAtUtc, string userId, bool isAdmin)
            => new(token, expiresAtUtc, userId, isAdmin, false, null);

        public static LoginResponse TwoFactorRequired(string challenge)
            => new(null, null, null, false, true, challenge);
    }
    public record LoginWith2faRequest(string ChallengeToken, string Code);
    public record RegisterRequest(string Email, string Password);
    public record RegisterResponse(bool Succeeded, IReadOnlyList<string> Errors);
    public record MeResponse(
        string UserId,
        string Email,
        bool IsAdmin,
        bool HasLocalPassword,
        bool EmailConfirmed,
        bool TwoFactorEnabled,
        int RecoveryCodesLeft,
        int PasskeyCount,
        string? PreferredLanguage);
    public record LanguagePreferenceRequest(string? PreferredLanguage);
    public record ChangePasswordRequest(string OldPassword, string NewPassword);
    public record ChangeEmailRequest(string NewEmail);
    public record ForgotPasswordRequest(string? Email);
    public record DeletePersonalDataRequest(string? Password);
    public record ErrorResponse(string Error);

    private string BuildPublicUrl(string path)
    {
        var configuredBaseUrl = _configuration["Smtp:AppBaseUrl"]?.Trim().TrimEnd('/');
        var relativePath = path.TrimStart('/');
        var requestPath = Request.PathBase.Add(new PathString($"/{relativePath}"));
        return Uri.TryCreate(configuredBaseUrl, UriKind.Absolute, out var baseUri)
            ? new Uri(new Uri($"{baseUri.AbsoluteUri.TrimEnd('/')}/"), relativePath).AbsoluteUri
            : $"{Request.Scheme}://{Request.Host}{requestPath}";
    }

    private async Task<bool> IsChallengeValidForUserAsync(ClaimsPrincipal principal, ApplicationUser user)
    {
        var tokenStamp = principal.FindFirstValue(JwtTokenOptions.SecurityStampClaimType);
        if (string.IsNullOrWhiteSpace(tokenStamp))
            return false;

        var currentStamp = await _userManager.GetSecurityStampAsync(user);
        return string.Equals(tokenStamp, currentStamp, StringComparison.Ordinal);
    }
}
