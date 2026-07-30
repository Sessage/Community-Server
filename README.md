# Sessage Community Server

Dieses Repository enthält den frei verfügbaren Community-Kern des Sessage-Servers. Es wird automatisch und einseitig aus dem zentralen Sessage-Monorepo veröffentlicht.

## Projekte

| Pfad | Aufgabe |
| --- | --- |
| `Community/TodoSuite.Community.csproj` | Lauffähiger ASP.NET-Core-/Blazor-Server |
| `TodoSuite.Community.Shared/TodoSuite.Community.Shared.csproj` | Gemeinsame Domänen-, UI- und Serverbausteine |
| `Community-Server.slnx` | Eigenständige Solution für Visual Studio und `dotnet` |

## Lokaler Start

Voraussetzungen sind das .NET 10 SDK, PostgreSQL und optional Node.js für einen erneuten Tailwind-Build.

```powershell
dotnet restore .\Community-Server.slnx
dotnet run --project .\Community\TodoSuite.Community.csproj
```

Die Datenbankverbindung und weitere Einstellungen werden über `Community/appsettings.json`, Umgebungsvariablen oder eine lokale, nicht eingecheckte `appsettings.Development.json` konfiguriert. Ausstehende Entity-Framework-Migrationen werden beim Start angewendet.

> **Wichtig:** Die eingecheckten Werte für Datenbank, SMTP, JWT und Active Directory sind ausschließlich lokale Beispielwerte. Vor einem erreichbaren oder produktiven Start müssen Kennwörter und insbesondere `Jwt__Key` über eine geschützte Konfiguration oder Umgebungsvariablen ersetzt werden. Geheimnisse dürfen nicht in `appsettings.json` committed werden.

## Änderungen beitragen

Dieses Repository ist ein Veröffentlichungsspiegel. Direkte Commits können beim nächsten Abgleich überschrieben werden. Änderungen am Community-Kern werden im zentralen Sessage-Repository entwickelt und von dort nach erfolgreicher Prüfung hierher übertragen.

## Lizenz

Der Community-Kern steht unter der [European Union Public Licence 1.2](LICENSE.md) (EUPL-1.2). Hinweise und Lizenzen eingebundener Drittkomponenten bleiben davon unberührt.

