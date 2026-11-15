# Ätherfluss Connect – Konto & Datenschutz: API-Vertrag (geplant)

**Status: Backend noch NICHT implementiert.** Dieses Dokument beschreibt die Voraussetzungen für die gewünschte Account-Oberfläche. Das derzeitige GitHub-Repository enthält nur den Dalamud-Client, nicht die produktive `connect.php` und nicht das aktuelle MySQL-Schema. Keine ungetestete Löschschaltfläche freischalten.

## Zielbild
- Connect mit Tabs **Nachrichten**, **Kontakte**, **Community & Termine** und künftig **Konto & Sicherheit**.
- Unterhaltung auswählen, gesamten vom Server gelieferten Verlauf mit Sender, Datum und Uhrzeit ansehen.
- Konto löschen ohne separaten zweiten Bestätigungsdialog. Der Benutzer muss zuvor sein Connect-Passwort **einmal** eingeben und den unmissverständlichen Button „Konto dauerhaft löschen“ drücken. Nicht automatisch beim Verlassen der Seite.
- Passwort festlegen/ändern mit **einem** neuen Passwortfeld; **keine doppelte Neueingabe**. Die Berechtigung muss dennoch serverseitig sicher nachgewiesen sein.
- Ein echtes Passwort-Reset nach Sitzungsverlust darf **nicht** allein durch Eingabe eines beliebigen neuen Passworts möglich werden. Hierfür wird ein authentifizierter Recovery-Kanal oder ein zeitlich begrenzter Einmalcode benötigt.

## API-Anforderungen
1. `POST ?action=delete_account` mit gültigem Sitzungstoken UND erneut eingegebenem Connect-Passwort. Zugriff nur auf eigenen Account; niemals ein frei wählbares `userId` akzeptieren.
2. Mit DB-Transaktion abhängige Datensätze per FK-/Relationeninventar entfernen, darunter Login-Tokens, Profile, Kontaktmetadaten, Freundschaften, Blocks, Präsenzdaten, Einladungen, Terminbezüge und Nachrichten, soweit personenbezogen. Entscheidungen für geteilte Nachrichten und Termine ausdrücklich festlegen; keine orphaned rows.
3. Erfolg erst melden, wenn Servertransaktion committed ist. Danach lokales verschlüsseltes Token und gecachte PNs/Kontakte löschen. Bei Fehler keine Erfolgsmeldung und keinen lokalen Zustand irreversibel entfernen.
4. Für neue Passwörter `POST ?action=set_password`: Bestehende Sitzung nur akzeptieren, wenn sie hinreichend frisch oder zusätzlich re-authentifiziert ist. Passworthash serverseitig mit geeignetem Password-Hashing-Algorithmus speichern; alle anderen Sitzungen invalidieren.
5. Rate-Limits, CSRF-/Token-Schutz nach Auth-Modell, Audit ohne Klartextpasswörter, Datenschutzinformationen, Löschkonzept für Backups und Fehlerfälle.
6. Alte Endpunkte nicht entfernen, bis alle Client-Versionen migriert und getestet sind. Unterstützte Migration versionieren.

## Warum noch keine fertige Konto-Löschung?
Der Client alleine kann niemals garantieren, dass Daten in MySQL gelöscht wurden. Die produktive API und Tabellenbeziehungen müssen zuerst aus der **aktuellen Serverversion** geprüft werden. Die Kontolöschung darf nicht durch einen nur kosmetischen Button dargestellt werden.
