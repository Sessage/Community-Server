using Microsoft.AspNetCore.Identity;

namespace TodoSuite.Server.Services;

public static class IdentityBootstrapGuard
{
    public static void EnsureSucceeded(IdentityResult result, string operation)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Succeeded) return;

        var details = string.Join(", ", result.Errors.Select(error =>
            string.IsNullOrWhiteSpace(error.Code)
                ? error.Description
                : $"{error.Code}: {error.Description}"));
        throw new InvalidOperationException($"{operation}. {details}");
    }
}
