namespace Klassenbibliothek.Features;

/// <summary>Identifies the product host; concrete behavior is always determined by capabilities.</summary>
public enum ProductEdition
{
    Community = 0,
    Enterprise = 1
}

/// <summary>
/// Stable feature identifiers exchanged by server, Mobile and license documents. Values are
/// persistence/API contracts and therefore must not be renamed as part of a UI wording change.
/// </summary>
public static class ProductFeatureIds
{
    public const string Core = "community.core";
    public const string Ldap = "community.identity.ldap";
    public const string Portfolios = "enterprise.portfolios";
    public const string Dashboards = "enterprise.dashboards";
    public const string Forms = "enterprise.forms";
    public const string Automation = "enterprise.automation";
    public const string EmailImport = "enterprise.email-import";
    public const string CentralAdministration = "enterprise.central-administration";
    public const string IdentityGovernance = "enterprise.identity-governance";
    public const string PushNotifications = "enterprise.push-notifications";

    public static readonly IReadOnlySet<string> Community = new HashSet<string>(StringComparer.Ordinal)
    {
        Core,
        Ldap
    };

    public static readonly IReadOnlySet<string> Enterprise = new HashSet<string>(StringComparer.Ordinal)
    {
        Portfolios,
        Dashboards,
        Forms,
        Automation,
        EmailImport,
        CentralAdministration,
        IdentityGovernance,
        PushNotifications
    };
}

/// <summary>Runtime capability response used by clients to adapt to a particular installation.</summary>
public sealed record ProductCapabilitiesDocument(
    ProductEdition Edition,
    IReadOnlyCollection<string> Capabilities,
    IReadOnlyDictionary<string, long> Limits,
    string? LicenseStatus = null,
    DateTime? LicenseExpiresAtUtc = null);

/// <summary>
/// Server-side source of truth for feature availability. UI checks improve usability, while
/// service decorators and endpoints remain responsible for enforcement.
/// </summary>
public interface IProductFeatureCatalog
{
    ProductEdition Edition { get; }
    bool IsEnabled(string featureId);
    ProductCapabilitiesDocument GetCapabilities();
}

/// <summary>Fixed capability catalog for the independently deployable Community product.</summary>
public sealed class CommunityProductFeatureCatalog : IProductFeatureCatalog
{
    public ProductEdition Edition => ProductEdition.Community;

    public bool IsEnabled(string featureId) => ProductFeatureIds.Community.Contains(featureId);

    public ProductCapabilitiesDocument GetCapabilities()
        => new(Edition, ProductFeatureIds.Community.OrderBy(x => x).ToArray(),
            new Dictionary<string, long>(StringComparer.Ordinal), "community");
}
