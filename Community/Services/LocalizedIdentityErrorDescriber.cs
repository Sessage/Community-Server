using Klassenbibliothek.Localization;
using Microsoft.AspNetCore.Identity;

namespace TodoSuite.Server.Services;

/// <summary>Keeps server validation errors in the language requested by web and native clients.</summary>
public sealed class LocalizedIdentityErrorDescriber : IdentityErrorDescriber
{
    private static IdentityError Error(string code, string key, params object[] args) => new() { Code = code, Description = SharedText.Get(key, args) };
    public override IdentityError DefaultError() => Error(nameof(DefaultError), "App_RequestFailed");
    public override IdentityError ConcurrencyFailure() => Error(nameof(ConcurrencyFailure), "App_ChangeUpdated");
    public override IdentityError PasswordMismatch() => Error(nameof(PasswordMismatch), "Identity_PasswordMismatch");
    public override IdentityError InvalidToken() => Error(nameof(InvalidToken), "Identity_InvalidToken");
    public override IdentityError DuplicateEmail(string email) => Error(nameof(DuplicateEmail), "Identity_DuplicateEmail");
    public override IdentityError InvalidEmail(string? email) => Error(nameof(InvalidEmail), "Identity_InvalidEmail");
    public override IdentityError InvalidUserName(string? userName) => Error(nameof(InvalidUserName), "Identity_InvalidUser");
    public override IdentityError DuplicateUserName(string userName) => Error(nameof(DuplicateUserName), "Identity_DuplicateUser");
    public override IdentityError PasswordTooShort(int length) => Error(nameof(PasswordTooShort), "Identity_PasswordLength", length);
    public override IdentityError PasswordRequiresDigit() => Error(nameof(PasswordRequiresDigit), "App_Passwordneedsanumber");
    public override IdentityError PasswordRequiresLower() => Error(nameof(PasswordRequiresLower), "App_Passwordneedsalowercaseletter");
    public override IdentityError PasswordRequiresUpper() => Error(nameof(PasswordRequiresUpper), "App_Passwordneedsanuppercaseletter");
    public override IdentityError PasswordRequiresNonAlphanumeric() => Error(nameof(PasswordRequiresNonAlphanumeric), "App_Passwordneedsasymbol");
    public override IdentityError RecoveryCodeRedemptionFailed() => Error(nameof(RecoveryCodeRedemptionFailed), "Security_VerificationFailed");
}
