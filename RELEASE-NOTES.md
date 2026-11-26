# CM AnrufMonitor 0.3.1

## Rückwärtssuche läuft bei einer einzelnen unklaren Antwort weiter

Version 0.3.0 konnte den gesamten Suchlauf anhalten, wenn eine einzelne Antwort von Das Örtliche keiner vollständigen Rufnummer zugeordnet werden konnte. Eine solche Antwort wurde fälschlich wie eine Zugriffssperre behandelt. Dadurch blieben die nachfolgenden Nummern unbearbeitet.

Diese Version trennt die Fälle:

- **Einzelne nicht auswertbare Antwort:** Die betreffende Nummer wird mit einem eigenen Zähler übersprungen. Anschließend sucht die App die übrigen Nummern weiter.
- **Kein falscher Nichttreffer:** Eine nicht auswertbare Antwort wird nicht als „kein Treffer“ gespeichert. Ein bereits bekannter Internetname bleibt erhalten.
- **Probleme nachvollziehen:** Die aufklappbare Problemliste nennt die Rufnummer, die Kategorie und den Grund. Die Abschlussmeldung zeigt, wie viele Nummern bearbeitet wurden und wie viele noch ausstehen.
- **Weiterhin begrenzte Anfragen:** Bei drei nicht auswertbaren Anbieterantworten ohne eine dazwischen erfolgreich ausgewertete neue Antwort hält die Suche an. Cachetreffer und vorübergehende Verbindungsfehler setzen diesen Zähler nicht zurück. Echte Zugriffssperren, erforderliche Anmeldungen und Anfragenbegrenzungen stoppen weiterhin sofort.
- **Ergebnisse wiederverwenden:** Ein späterer manueller Suchlauf verwendet gültige gespeicherte Treffer und Nichttreffer. Der Verlauf muss dafür nicht gelöscht werden.

Die Prüfung der vollständigen Rufnummer bleibt bestehen. Die App übernimmt keinen Namen aus einem fremden Eintrag und erfindet bei unklaren Antworten keine Zuordnung. Die Änderung kann den Suchlauf fortsetzen, erhöht aber nicht automatisch die Zahl öffentlich verfügbarer Telefonbucheinträge.

## Aktualisieren

Eine laufende ältere App vollständig beenden. `CM-AnrufMonitor_0.3.1_Windows-x64.zip` entpacken und die neue `CM-AnrufMonitor.exe` starten. Einstellungen, Kontakte, Verlauf und vorhandene Internetnamen bleiben im bisherigen Datenordner erhalten. Eine separate .NET-Installation ist nicht nötig. Bei aktiviertem Windows-Autostart die Einstellung aus dem neuen Programmordner erneut speichern.

Anschließend unter **Anrufverlauf** erneut **Namen für gesamte Liste suchen** wählen. Nur wenn eine tatsächliche Sperre oder Anfragenbegrenzung gemeldet wurde, zunächst warten. **Leeren** ist für die Fortsetzung nicht erforderlich.

## Prüfstand

Die automatisierten Prüfungen bilden auch den gemeldeten Ablauf nach: 157 Nummern, 8 gespeicherte Ergebnisse und eine nicht auswertbare Antwort an Position 22. Zusätzlich werden spätere erfolgreiche Treffer, der Stopp bei wiederholten unklaren Antworten, erhaltene gespeicherte Namen und echte Zugriffssperren geprüft. Der native Windows-Test kontrolliert die sichtbare Problemliste und einen nachfolgenden Treffer mit synthetischen Daten.

Die aktuelle öffentliche Anbieterwebseite wurde erneut anhand veröffentlichter Geschäftsnummern geprüft. Die konkrete Antwort zu der im Nutzerscreenshot betroffenen Rufnummer liegt nicht vor; deren individueller Inhalt wird deshalb nicht als reproduziert behauptet. Der Prüfbericht und der GitHub-Prüflauf dokumentieren den tatsächlichen Stand.

CM AnrufMonitor bleibt eine unabhängige Vorabversion ohne Herausgeberzertifikat. Ersteller: **C. Mihalik**.

[Projekt und Anleitung](https://github.com/ceromihalik-dev/CM-AnrufMonitor) · [Build und Prüfungen](https://github.com/ceromihalik-dev/CM-AnrufMonitor/actions)
