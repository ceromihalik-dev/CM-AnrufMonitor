# CM AnrufMonitor 0.2.1

## Korrektur in 0.2.1

Die Reiter der großen Ansicht werden vollständig angezeigt. Der Windows-Prüflauf erfasst zusätzlich den Einstellungsbereich mit den neuen Zugangsfeldern.

## Neu seit 0.1

- **Anrufe nachladen:** Die App gleicht die FRITZ!Box-Anrufliste beim Start, bei Wiederverbindung, alle fünf Minuten und kurz nach einem beobachteten Gesprächsende ab. So werden auch Anrufe aus Zeiten mit ausgeschaltetem PC sichtbar, soweit sie noch auf der Box gespeichert sind.
- **Namen automatisch anzeigen:** FRITZ!Box-Telefonbücher werden abgerufen und lokal zwischengespeichert. Eigene Kontakte haben Vorrang. Ein Name aus der Router-Anrufliste dient als weitere Quelle.
- **Rückwärtssuche im Internet:** Eine kleine Schaltfläche öffnet die ausgewählte deutsche Nummer bei Das Örtliche im Browser. Die App sendet ohne diesen Klick keine Nummern an den Auskunftsdienst und übernimmt keine Web-Ergebnisse automatisch.
- **Zugang geschützt speichern:** Das FRITZ!Box-Kennwort wird mit Windows-DPAPI für das aktuelle Benutzerkonto geschützt. Separater Verbindungstest, manueller Abgleich und Kennwortlöschen sind enthalten.
- **Dezente Projektangaben:** Kleine Schaltflächen „Info“ und „Unterstützen“ in großer und kompakter Ansicht; Ersteller C. Mihalik, GitHub-Projekt und freiwillige PayPal-Unterstützung.
- **Nachvollziehbare Veröffentlichung:** Quellcode, Windows-Build, vier Prüfsuiten, WPF-/DPAPI-Smoke-Test, SHA-256-Prüfsumme und Build-Informationen auf GitHub.

## Installation und Update

`CM-AnrufMonitor_0.2.1_Windows-x64.zip` vollständig entpacken, eine eventuell laufende alte Version beenden und `CM-AnrufMonitor.exe` aus dem neuen Ordner starten. Die .NET-Laufzeit ist enthalten. Bei aktiviertem Windows-Autostart die Einstellung aus dem neuen Programmordner erneut speichern, damit der Startpfad stimmt.

Einstellungen, Kontakte und Verlauf aus 0.1.0 bleiben im bisherigen Benutzerordner lesbar. Der neue automatische Abgleich ist zunächst ausgeschaltet und wird erst mit dem eigenen FRITZ!Box-Zugang eingerichtet. In `Anleitung.html` stehen die Schritte für die Live-Anzeige auf Port 1012 und den getrennten TR-064-Abgleich auf Port 49000.

## Grenzen dieser Vorabversion

Die App kann keine auf der Box gelöschten Anrufe nachholen. Historische Zeiten und Dauern sind teilweise nur minutengenau; importierte Dauern tragen „≈“. Mehrdeutige Zuordnungen, etwa bei unterdrückten Nummern, bleiben als getrennte Einträge erhalten. Die Rückwärtssuche findet nur passende veröffentlichte deutsche Telefonbucheinträge. Es gibt keine Telefonie-/Audiofunktion.

Der Windows-Smoke-Test verwendet künstliche Daten. Ein Test an der tatsächlichen FRITZ!Box des Anwenders steht noch aus. Die EXE ist nicht mit einem Herausgeberzertifikat signiert. Der mitgelieferte Prüfbericht und `BUILD-INFO.json` beschreiben den Stand; die PNGs im Release zeigen die laufende Windows-Oberfläche mit markiertem Demo-Anruf.

[Projekt und Anleitung](https://github.com/ceromihalik-dev/CM-AnrufMonitor) · [Build und Prüfungen](https://github.com/ceromihalik-dev/CM-AnrufMonitor/actions)
