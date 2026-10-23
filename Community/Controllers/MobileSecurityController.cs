using System.Security.Claims;
using Klassenbibliothek.Data;
using Klassenbibliothek.Localization;
using Klassenbibliothek.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;
using QRCoder;
using TodoSuite.Server.Auth;

namespace TodoSuite.Server.Controllers;

/// <summary>Account-bound security management for native clients; secrets never enter shared caches.</summary>
[ApiController]
[Route("api/mobile/auth/security")]
[Authorize(Policy = "MobileSession")]
[EnableRateLimiting("auth")]
public sealed class MobileSecurityController(UserManager<ApplicationUser> users) : ControllerBase
{
    private Task<ApplicationUser?> UserAsync() => users.GetUserAsync(User);
    private void NoStore() => Response.Headers.CacheControl = "no-store";
    private BadRequestObjectResult Invalid() => BadRequest(new { error = SharedText.Get("Security_VerificationFailed") });

    [HttpPost("authenticator/setup")]
    public async Task<IActionResult> Setup(SecurityVerification request)
    {
        NoStore();
        var user = await UserAsync();
        if (user is null) return Unauthorized();
        if (await users.GetTwoFactorEnabledAsync(user) || !await VerifyAsync(user, request)) return Invalid();
        var key = await users.GetAuthenticatorKeyAsync(user);
        if (string.IsNullOrEmpty(key))
        {
            // Initial enrollment must keep the verified bearer session alive until the
            // user confirms a code. ResetAuthenticatorKey would revoke that session.
            var generated = users.GenerateNewAuthenticatorKey();
            if (!(await users.SetAuthenticationTokenAsync(user, "[AspNetUserStore]", "AuthenticatorKey", generated)).Succeeded) return Invalid();
            key = await users.GetAuthenticatorKeyAsync(user);
        }
        if (string.IsNullOrEmpty(key)) return Invalid();
        var uri = $"otpauth://totp/{Uri.EscapeDataString("Sessage")}:{Uri.EscapeDataString(user.Email ?? user.UserName ?? "")}?secret={Uri.EscapeDataString(key)}&issuer=Sessage&digits=6";
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(uri, QRCodeGenerator.ECCLevel.Q);
        using var png = new PngByteQRCode(data);
        return Ok(new AuthenticatorSetup(key, "data:image/png;base64," + Convert.ToBase64String(png.GetGraphic(4))));
    }

    [HttpPost("authenticator/enable")]
    public async Task<IActionResult> Enable(SecurityVerification request)
    {
        NoStore();
        var user = await UserAsync();
        if (user is null) return Unauthorized();
        if (await users.GetTwoFactorEnabledAsync(user) || !await VerifyCodeAsync(user, request.Code)) return Invalid();
        if (!(await users.SetTwoFactorEnabledAsync(user, true)).Succeeded) return Invalid();
        var codes = await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10);
        return Ok(new SecurityRecoveryCodes(codes?.ToArray() ?? []));
    }

    [HttpPost("authenticator/disable")]
    public async Task<IActionResult> Disable(SecurityVerification request)
    {
        NoStore();
        var user = await UserAsync();
        if (user is null) return Unauthorized();
        if (!await users.GetTwoFactorEnabledAsync(user) || !await VerifyCodeAsync(user, request.Code)) return Invalid();
        if (!(await users.SetTwoFactorEnabledAsync(user, false)).Succeeded) return Invalid();
        return Ok();
    }

    [HttpPost("recovery-codes")]
    public async Task<IActionResult> RecoveryCodes(SecurityVerification request)
    {
        NoStore();
        var user = await UserAsync();
        if (user is null) return Unauthorized();
        if (!await users.GetTwoFactorEnabledAsync(user) || !await VerifyCodeAsync(user, request.Code)) return Invalid();
        var codes = await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10);
        return Ok(new SecurityRecoveryCodes(codes?.ToArray() ?? []));
    }

    [HttpGet("passkeys")]
    public async Task<IActionResult> Passkeys()
    {
        NoStore();
        var user = await UserAsync();
        return user is null ? Unauthorized() : Ok((await users.GetPasskeysAsync(user))
            .Select(key => new SecurityPasskey(WebEncoders.Base64UrlEncode(key.CredentialId), key.Name)).ToArray());
    }

    [HttpPost("passkeys/{credentialId}/rename")]
    public async Task<IActionResult> Rename(string credentialId, SecurityVerification request)
    {
        NoStore();
        var user = await UserAsync();
        if (user is null) return Unauthorized();
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 200 || !await VerifyAsync(user, request)) return Invalid();
        var id = DecodeId(credentialId);
        if (id is null) return Invalid();
        var key = (await users.GetPasskeysAsync(user)).FirstOrDefault(key => key.CredentialId.SequenceEqual(id));
        if (key is null) return NotFound();
        key.Name = request.Name.Trim();
        return (await users.AddOrUpdatePasskeyAsync(user, key)).Succeeded ? Ok() : Invalid();
    }

    [HttpPost("passkeys/{credentialId}/delete")]
    public async Task<IActionResult> Delete(string credentialId, SecurityVerification request)
    {
        NoStore();
        var user = await UserAsync();
        if (user is null) return Unauthorized();
        if (!await VerifyAsync(user, request)) return Invalid();
        var id = DecodeId(credentialId);
        if (id is null) return Invalid();
        if (!(await users.GetPasskeysAsync(user)).Any(key => key.CredentialId.SequenceEqual(id))) return NotFound();
        return (await users.RemovePasskeyAsync(user, id)).Succeeded ? Ok() : Invalid();
    }

    private async Task<bool> VerifyAsync(ApplicationUser user, SecurityVerification request)
    {
        if (await users.IsLockedOutAsync(user)) return false;
        if (await users.GetTwoFactorEnabledAsync(user)) return await VerifyCodeAsync(user, request.Code);
        if (await users.HasPasswordAsync(user))
        {
            var valid = request.Password is { Length: > 0 and <= 1024 } && await users.CheckPasswordAsync(user, request.Password);
            if (valid) await users.ResetAccessFailedCountAsync(user); else await users.AccessFailedAsync(user);
            return valid;
        }
        // Refreshing a token must never count as a new interactive authentication.
        return MobileAuthenticationTime.IsRecent(User, DateTimeOffset.UtcNow);
    }

    private async Task<bool> VerifyCodeAsync(ApplicationUser user, string? code)
    {
        if (await users.IsLockedOutAsync(user)) return false;
        var normalized = (code ?? "").Replace(" ", "").Replace("-", "");
        var valid = normalized.Length == 6 && normalized.All(char.IsAsciiDigit)
            && await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, normalized);
        if (valid) await users.ResetAccessFailedCountAsync(user); else await users.AccessFailedAsync(user);
        return valid;
    }

    private static byte[]? DecodeId(string value)
    {
        if (value.Length > 2048) return null;
        try { return WebEncoders.Base64UrlDecode(value); } catch (FormatException) { return null; }
    }
}
