# Aetherfluss Nexus 0.12.5 – Connect UI (Entwicklungsstand)

**Noch nicht als Dalamud-Release freigegeben.** Der Client wurde auf dem Branch `main` überarbeitet; Windows-Build, Dalamud-Test und Backend-Abnahme stehen aus.

## Umgesetzt im Client
- Eigenständige Connect-Tabs: **Nachrichten**, **Kontakte**, **Community & Termine**, **Konto & Sicherheit**.
- Nachrichtenansicht: Gesprächspartner direkt auswählen, Posteingang anzeigen, bestehende `messages`-API verwenden; lesbarer 300px-Verlauf mit Sender und Zeitfeld `createdAt`, soweit vom Server geliefert.
- Konto-Tab: Profil aktualisieren, abmelden, **ein** Feld für das neue Passwort; bisheriges Passwort nur als optionale serverseitige Sicherheitsprüfung.
- Kontolöschung im Konto-Tab **nur erklärt**, noch keine auslösbare Aktion. Eine echte Löschung setzt einen geprüften serverseitigen Endpunkt voraus.

## Nicht umgesetzt / Abhängigkeiten
- Produktive Kontolöschung inklusive Nachrichten, Kontakte, Tokens, Raid-Daten und Backups.
- Passwort-Recovery nach Verlust der Sitzung; dies erfordert eine sichere Recovery-Methode.
- Gegebenenfalls serverseitige Unterstützung für Passwortänderung ohne bisheriges Passwort bei frisch re-authentifizierten Sitzungen.

Das öffentliche Repository enthält **keine** produktive `aetherfluss-api/connect.php` und kein vollständiges aktuelles DB-Schema. Keine alten Backend-Stände ohne Abgleich auf dem Produktivserver einsetzen. Benötigt: aktuelle `connect.php` sowie `SHOW CREATE TABLE`-Ausgaben der `aether_connect_*`-Tabellen **ohne Nutzerdatensätze oder Geheimnisse**.

## Release-Hinweis
Die Projektversion wurde auf `0.12.5.0` gesetzt. Nach einem erfolgreichen Build und der Backend-Abnahme einen neuen Release-Tag `v0.12.5-beta` verwenden; bestehende Tags nicht überschreiben.
