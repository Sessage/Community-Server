namespace TodoSuite.Server.Services;

/// <summary>
/// Konfigurationsoptionen für die Active-Directory- oder LDAP-Authentifizierung.
/// Wird aus dem Abschnitt "ActiveDirectory" in appsettings.json gelesen.
/// </summary>
public class ActiveDirectoryOptions
{
    /// <summary>
    /// Aktiviert die AD-Authentifizierung. Wenn false, werden lokale Konten verwendet.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Verzeichnistyp. Unterstützt werden "ActiveDirectory" (Standard) und "Ldap".
    /// Der Wert steuert ausschließlich sinnvolle Standardattribute; alle Attribute
    /// und Filter können weiterhin einzeln überschrieben werden.
    /// </summary>
    public string Provider { get; set; } = "ActiveDirectory";

    /// <summary>
    /// Hostname oder IP-Adresse des LDAP/AD-Servers.
    /// </summary>
    public string Server { get; set; } = "";

    /// <summary>
    /// Port des LDAP-Servers (Standard: 389 ohne SSL, 636 mit SSL).
    /// </summary>
    public int Port { get; set; } = 389;

    /// <summary>
    /// SSL/TLS-Verschlüsselung aktivieren (LDAPS).
    /// </summary>
    public bool UseSSL { get; set; }

    /// <summary>
    /// Distinguished Name (DN) des Dienstkontos für die Suche im AD.
    /// Beispiel: "cn=svc-sessage,ou=serviceaccounts,dc=example,dc=com"
    /// </summary>
    public string BindUser { get; set; } = "";

    /// <summary>
    /// Passwort des Dienstkontos.
    /// </summary>
    public string BindPassword { get; set; } = "";

    /// <summary>
    /// Basis-DN für die Benutzersuche.
    /// Beispiel: "dc=example,dc=com"
    /// </summary>
    public string BaseDn { get; set; } = "";

    /// <summary>
    /// CN der AD-Gruppe, auf die der Login beschränkt wird.
    /// Leer lassen, um keinen Gruppen-Filter zu verwenden.
    /// Beispiel: "Sessage-Users"
    /// </summary>
    public string RequiredGroupCn { get; set; } = "";

    /// <summary>Optionaler vollständiger DN einer erforderlichen Gruppe.</summary>
    public string RequiredGroupDn { get; set; } = "";

    /// <summary>
    /// Attribut für den Anmeldenamen. Standard: sAMAccountName bei AD, uid bei LDAP.
    /// </summary>
    public string UserNameAttribute { get; set; } = "";

    /// <summary>
    /// Kommagetrennte weitere Anmeldeattribute. Standard bei AD: userPrincipalName.
    /// </summary>
    public string AdditionalUserNameAttributes { get; set; } = "";

    /// <summary>Attribut für die E-Mail-Adresse. Standard: mail.</summary>
    public string EmailAttribute { get; set; } = "";

    /// <summary>Attribut für den Anzeigenamen. Standard: displayName bei AD, cn bei LDAP.</summary>
    public string DisplayNameAttribute { get; set; } = "";

    /// <summary>
    /// Attribut für die stabile, menschenlesbare Verzeichniskennung. Standard bei AD:
    /// userPrincipalName; bei LDAP wird zunächst die E-Mail-Adresse verwendet.
    /// </summary>
    public string IdentityAttribute { get; set; } = "";

    /// <summary>Objektklasse für Benutzer. Standard: user bei AD, inetOrgPerson bei LDAP.</summary>
    public string UserObjectClass { get; set; } = "";

    /// <summary>Objektklasse für Gruppen. Standard: group bei AD, groupOfNames bei LDAP.</summary>
    public string GroupObjectClass { get; set; } = "";

    /// <summary>Attribut für Gruppennamen. Standard: cn.</summary>
    public string GroupNameAttribute { get; set; } = "";

    /// <summary>Attribut am Benutzerobjekt mit direkten Gruppen-DNs. Standard: memberOf.</summary>
    public string GroupMembershipAttribute { get; set; } = "";

    /// <summary>
    /// Optionaler Benutzer-Suchfilter. Der Platzhalter {username} wird LDAP-sicher ersetzt.
    /// Beispiel: (&amp;(objectClass=inetOrgPerson)(uid={username})).
    /// </summary>
    public string UserSearchFilter { get; set; } = "";

    /// <summary>Optionale abweichende Suchbasis für Gruppen.</summary>
    public string GroupSearchBaseDn { get; set; } = "";

    /// <summary>
    /// Optionaler Filter zur Ermittlung von Gruppenmitgliedschaften. Unterstützte
    /// Platzhalter: {userDn} und {username}. Beispiele sind member={userDn} oder
    /// memberUid={username}. Ohne Filter nutzt AD die rekursive Matching Rule und
    /// generisches LDAP das konfigurierte memberOf-Attribut.
    /// </summary>
    public string GroupMembershipSearchFilter { get; set; } = "";

    /// <summary>
    /// Optionale Domain zur Bildung einer E-Mail-Adresse, falls das E-Mail-Attribut fehlt.
    /// Bei AD wird aus Kompatibilitätsgründen andernfalls die Domain aus BaseDn verwendet.
    /// </summary>
    public string FallbackEmailDomain { get; set; } = "";

    /// <summary>Netzwerk-Timeout pro LDAP-Anfrage in Sekunden.</summary>
    public int TimeoutSeconds { get; set; } = 15;

    public bool UseStartTls { get; set; } = false;

    /// <summary>
    /// Optionaler SHA-256-Fingerabdruck des erwarteten LDAP-Serverzertifikats. Erlaubt
    /// private oder selbstsignierte Unternehmenszertifikate ausschließlich bei exakter
    /// Übereinstimmung, ohne die globale Zertifikatsprüfung des Betriebssystems zu lockern.
    /// </summary>
    public string PinnedServerCertificateSha256 { get; set; } = "";

    /// <summary>
    /// Probiert bei Verbindungsfehlern zusätzlich LDAP, StartTLS und LDAPS. Kann einen
    /// Transport-Downgrade verursachen und ist daher standardmäßig deaktiviert.
    /// </summary>
    public bool EnableAutoFallback { get; set; } = false;
}
