namespace Klassenbibliothek.Services;

public sealed record SecurityVerification(string? Code = null, string? Password = null, string? Name = null);
public sealed record AuthenticatorSetup(string Key, string QrImage);
public sealed record SecurityRecoveryCodes(IReadOnlyList<string> Codes);
public sealed record SecurityPasskey(string CredentialId, string? Name);
