# Aetherfluss Nexus — Dalamud Custom Repository

> **Aktueller Stand (15.11.2026):** Die veröffentlichte Beta ist **0.12.4**. Der Entwicklungsbranch `main` enthält **0.12.5.0** mit den neuen Connect-Tabs, besserem Nachrichtenverlauf und separater Kontoverwaltung. Der [Windows-CI-Build für 0.12.5](https://github.com/DarkDino2010/-therfluss-Nexus/actions/runs/43734258680) war erfolgreich und enthält das Test-Artefakt `Aetherfluss-Nexus-v0.12.5-CI`. **Kontolöschung und Passwort-Wiederherstellung auf dem Produktivserver sind noch nicht implementiert.**


**Version:** 0.12.4.0 (Beta-Vorbereitung)  
**Technischer Plugin-Name:** `AetherflussGuide` (absichtlich unverändert, um Updates zu ermöglichen)  
**Sichtbarer Name:** `Aetherfluss Nexus`

Dieses Repository enthält ausschließlich den **Dalamud-Client**. Server-PHP, Datenbank- und Administrationsdateien sind **nicht enthalten**.

## Veröffentlichung (GitHub)

1. Das bestehende öffentliche GitHub-Repository [`DarkDino2010/-therfluss-Nexus`](https://github.com/DarkDino2010/-therfluss-Nexus) verwenden.
2. Die Projektdateien befinden sich bereits auf `main`; neue Änderungen gezielt committen.
3. In GitHub unter **Settings → Actions → General → Workflow permissions** `Read and write permissions` für `GITHUB_TOKEN` aktivieren, sofern noch deaktiviert.
4. Unter **Actions → Publish Aetherfluss Nexus beta → Run workflow** den neuen Tag `v0.12.4-beta` starten.
5. Erst wenn die Action erfolgreich ist, liegt in `repo.json` ein echter Eintrag vor. Der Workflow erzeugt `latest.zip` und ein GitHub-Prerelease.
6. Repository-URL an die Testerin geben: `https://raw.githubusercontent.com/DarkDino2010/-therfluss-Nexus/main/repo.json` (`OWNER` ersetzen).
7. In XIVLauncher/Dalamud `/xlsettings` → Experimental → Custom Plugin Repositories die URL hinzufügen, danach `/xlplugins` → Aetherfluss Nexus.

**Hinweis:** `repo.json` startet absichtlich mit `[]`. Das verhindert die Veröffentlichung eines kaputten Links, bevor GitHub Actions einen Build erzeugt. Kein Release gilt als erfolgreich, bevor sowohl die Action als auch eine Dalamud-Testinstallation bestanden sind.

## Updates

In `AetherflussGuide/AetherflussGuide.csproj` den `<Version>`-Wert erhöhen (vier Komponenten). Änderungen pushen. GitHub Actions erneut mit **neuem** Tag starten, z. B. `v0.12.5-beta`. Nicht denselben Tag überschreiben. Dalamud erkennt die höhere `AssemblyVersion`.

## Lokal kompilieren

Windows + .NET 10 SDK: `dotnet build AetherflussGuide/AetherflussGuide.csproj -c Release -p:Platform=x64`. Das eigentliche Release-ZIP wird vom Dalamud-SDK/Packager in der Release-Ausgabe erzeugt. Falls es nicht erzeugt wird, **bricht** die Release-Pipeline bewusst ab.

## Sicherheitsprüfung vor Public

- Alle Dateien auf Zugangsdaten/Token und persönliche Daten prüfen (keine Konfigurationsdateien committen).
- Die Connect-API muss auch ohne Plugin-UI serverseitig Authentifizierung und Rechte prüfen.
- Veröffentlichung des Clients bedeutet nicht, dass der Dienst zur öffentlichen Registrierung freigeschaltet wird.
- Netzwerkzugriffe: Datenübertragung an `der-aetherfluss.de` und optionales Laden von YouTube-Vorschaubildern offenlegen.
- Drittanbieter-Plugins können gegen die Nutzungsbedingungen von FFXIV verstoßen.
- Vor Veröffentlichung auf zweitem PC testen, ohne Entwickler-Pfade und lokale Abhängigkeiten.

## Rollback

Bei Problemen `repo.json` auf die letzte funktionierende Version zurücksetzen und committen; kein altes Release löschen, wenn Clients noch darauf verweisen. Mit `[]` wird nur die Auflistung geleert — bestehende Installationen werden dadurch nicht automatisch repariert oder entfernt.

## Status

Quellcode aus dem letzten V0.12.4-Entwicklungsstand übernommen. Die GitHub-Action und die Release-Installation sind **noch nicht in GitHub/Windows ausgeführt worden**. Die im Projekt eingetragene Dalamud-SDK-Version ist `15.0.0`; bei einer inkompatiblen Dalamud-Version muss das SDK aktualisiert und neu gebaut werden.
