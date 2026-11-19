# CM AnrufMonitor 0.3.0

## Namen für die gesamte Anrufliste

Unter **Anrufverlauf → Namen für gesamte Liste suchen** lassen sich unbekannte deutsche Rufnummern des gesamten lokal gespeicherten Verlaufs bei Das Örtliche nachschlagen. Der aktuelle Suchtext oder Richtungsfilter begrenzt diesen Lauf nicht.

- **Treffer direkt im Verlauf:** Eindeutige Namen erscheinen bei allen bisherigen und späteren Anrufen derselben vollständigen Nummer sowie im Widget, in der Suche und im CSV-Export.
- **Einmal pro Nummer:** Unterschiedliche Schreibweisen und mehrere Anrufe derselben Nummer werden zusammengefasst. Bekannte Kontakte, eigene, unterdrückte und interne Nummern werden übersprungen.
- **Vorhandene Namen behalten Vorrang:** Eigene Kontakte, geprüfte FRITZ!Box-Telefonbücher und Namen aus der Router-Anrufliste stehen vor Internettreffern.
- **Lokal gespeichert:** Quelle und Prüfdatum sind sichtbar. Positive Treffer werden 30 Tage wiederverwendet, eindeutige Nichttreffer sieben Tage. Ältere Namen bleiben erkennbar und werden beim nächsten manuellen Suchlauf erneut geprüft.
- **Fortschritt und Abbruch:** Ergebnisse erscheinen während des Laufs. Abbrechen erhält bereits gespeicherte Treffer. Einzelne Nummern lassen sich über **Name suchen und übernehmen** im Kontextmenü abfragen.
- **Kein API-Schlüssel:** Der eingebaute Anbieter verwendet die öffentliche Webseite von Das Örtliche. Es besteht keine offizielle API-Anbindung; Änderungen an der Webseite können Anpassungen der App erfordern.

Die Internet-Suche startet nur auf Klick. Sie übermittelt die unbekannten Rufnummern einzeln, keine Anrufzeiten, Gesprächsdauern, Kontaktliste oder Routerkennwörter. Bei Sperren, verlangter Anmeldung oder nicht erkennbarer Antwortstruktur hält sie an. Ohne eindeutige Zuordnung wird kein neuer Name gesetzt.

Der automatische FRITZ!Box-Anruflistenabgleich und die Telefonbücher bleiben enthalten. So werden auch Anrufe nachgeladen, während der PC ausgeschaltet war, soweit die Box sie noch gespeichert hat. Die blaue Oberfläche mit gelben Akzenten und die kleinen Info-/Unterstützen-Schaltflächen bleiben erhalten. Ersteller: **C. Mihalik**.

## Installation oder Update

`CM-AnrufMonitor_0.3.0_Windows-x64.zip` vollständig entpacken, eine laufende ältere Version beenden und `CM-AnrufMonitor.exe` aus dem neuen Ordner starten. Die .NET-Laufzeit ist enthalten. Bestehende Einstellungen, Kontakte und der Verlauf bleiben im Datenordner erhalten. Bei aktiviertem Windows-Autostart die Einstellung aus dem neuen Ordner erneut speichern.

Zugangsdaten ausschließlich in der App eingeben. Die Einrichtung des lokalen FRITZ!Box-Abgleichs ist in `Anleitung.html` beschrieben. Für die Internet-Suche unter **Einrichtung** die Landesvorwahl und bei Bedarf die eigene Ortsvorwahl prüfen.

## Prüfstand und Grenzen

Sechs Prüfsuiten prüfen Netzwerk, Routerabgleich, Speicher, Rückwärtssuche und Antwortparser. Ein nativer Windows-Smoke-Test prüft die tatsächliche WPF-Oberfläche, DPAPI und die Anzeige erkannter Namen mit einem synthetischen Anbieter. Die PNGs im Release zeigen ausdrücklich markierte Testdaten. Der Prüfbericht beschreibt die getrennte Prüfung der aktuellen Anbieterwebseite.

Ein Test an der tatsächlichen FRITZ!Box des Anwenders steht noch aus. Die Rückwärtssuche findet nur passende veröffentlichte deutsche Telefonbucheinträge. Angezeigte Namen bestätigen nicht die Identität des tatsächlichen Anrufers. Die EXE ist nicht mit einem Herausgeberzertifikat signiert. Es handelt sich um eine Vorabversion.

[Projekt und Anleitung](https://github.com/ceromihalik-dev/CM-AnrufMonitor) · [Build und Prüfungen](https://github.com/ceromihalik-dev/CM-AnrufMonitor/actions)
