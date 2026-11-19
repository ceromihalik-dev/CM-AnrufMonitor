# Prüfbericht: CM AnrufMonitor 0.2.1

Stand: 19. November 2026. Ersteller: C. Mihalik. Die Anwendung wird als Vorabversion veröffentlicht.

## Ergebnis und Aussagekraft

**168 Verhaltensprüfungen bestanden:** 44 Kernprüfungen, 6 TCP-Szenarien, 54 Prüfungen des Verlaufsabgleichs und 64 TR-064-Prüfungen. Die WPF-Anwendung wurde mit .NET SDK 10.0.404 ohne Warnungen und Fehler für Windows kompiliert.

Die lokalen Prüfungen verwenden ausschließlich künstliche Daten und lokale HTTP-/TCP-Server. Sie belegen Parser-, Netzwerk-, Abgleich- und Speicherverhalten. **Eine echte FRITZ!Box wurde nicht angesprochen.** Insbesondere Routermodell, FRITZ!OS, Benutzerrechte und die Telefoniekonfiguration des Anwenders sind noch praktisch zu prüfen.

Der GitHub-Workflow ergänzt einen echten Windows-Start der WPF-Anwendung und eine DPAPI-Prüfung. Er erstellt ein Release-Paket nur, wenn Build, alle Prüfsuiten und dieser Smoke-Test erfolgreich sind. Der überprüfbare Lauf steht unter [GitHub Actions](https://github.com/ceromihalik-dev/CM-AnrufMonitor/actions). Für ein daraus veröffentlichtes ZIP nennen `BUILD-INFO.json` und `windows-smoke.json` den tatsächlichen Prüfstand. Diese Angaben sind kein Nachweis eines Anrufs über echte Hardware.

## Werkzeugstand

| Bestandteil | Stand |
|---|---|
| Anwendung | 0.2.1; Version zentral in `Directory.Build.props` |
| SDK | .NET 10.0.404 |
| Zielpaket | `win-x64`, WPF, eigenständige Single-File-EXE |
| Laufzeit | Im Paket enthalten; genaue Version in `BUILD-INFO.json` |
| CI | GitHub Actions, Windows Server 2025 |
| Build | Release, kein Trimming, eingebettete Debug-Informationen |
| Signatur | Keine Authenticode-Signatur |
| Testdaten | Künstliche Nummern und Ereignisse; keine Anwenderzugangsdaten |

## Durchgeführte Kern- und Netzwerkprüfungen

| Suite | Anzahl | Schwerpunkte |
|---|---:|---|
| `CoreChecks` | 44 | Ereignisparser, parallele Gespräche, unterbrochene Beobachtung, Kontakte, XML-/CSV-Import, atomare JSON-Speicherung |
| `NetworkChecks` | 6 | Echte lokale TCP-Verbindungen, fragmentierte Zeilen, mehrere Ereignisse, Stille, Wiederverbindung, Abbruch und überlange Eingaben |
| `HistorySyncChecks` | 54 | Wiederholte Abrufe, Live-/Router-Zuordnung, ID-Wiederverwendung, laufende Gespräche, Mehrdeutigkeiten, Migration und Gerätebindung |
| `Tr064Checks` | 64 | SOAP/XML, Digest-Anmeldung, HTTP-Fehler, Zeitgrenzen, Telefonbücher, URL-/Netzwerkgrenzen und XML-Sicherheit |

### Anruflistenabgleich

- Bekannte Routerdatensätze bleiben bei wiederholtem Abruf stabil und werden nicht erneut angelegt.
- Wiederverwendete Anruf-IDs an einem anderen Datum oder an einer anderen Box bleiben getrennt.
- Die Zuordnung zum Live-Verlauf braucht eine eindeutige Übereinstimmung des gesamten Kandidatenbestands. Vollständige normalisierte Nummern, Richtung, Minute und passende Dauer werden berücksichtigt.
- Laufende Gespräche behalten ihre aktive Verbindungs-ID; zugehörige historische Zeilen werden zurückgestellt.
- Auch verspätete Live-Ereignisse nach einem bereits importierten Routereintrag sind geprüft: Bei eindeutiger Zuordnung bleibt die genauere abgeschlossene Live-Aufzeichnung erhalten.
- Unterdrückte Nummern und ununterscheidbare Kandidaten werden nicht blind verschmolzen. Getrennte Einträge und ein Mehrdeutigkeitshinweis sind in diesen Fällen beabsichtigt.
- Genaue Live-Dauern werden nicht durch aufgerundete Router-Minuten ersetzt. Importierte Minutendauern sind als ungefähr gekennzeichnet.
- Einstellungen und Verlauf aus 0.1.0 bleiben lesbar. Bestehende Enum-Zahlenwerte wurden erhalten.
- Der separate Telefonbuch-Cache wird nur für die überprüfte Gerätekennung geladen. Ein Gerätewechsel zwischen Listen- und Telefonbuchabruf wird zurückgewiesen.

Ein wiederholter Abgleich mit 2.000 bereits bekannten Einträgen benötigte in einer lokalen Messung rund **33 ms**. Dabei blieben alle IDs erhalten, `Added = 0` und `Updated = 0`. Das ist eine Messbeobachtung auf dem Entwicklungshost, keine zugesicherte Laufzeit und keine fragile Testschranke.

### TR-064 und Zugangsdaten

- Ein echter lokaler HTTP-Testserver prüft die Antwort auf eine Digest-Challenge rechnerisch. Basic-Anmeldung wird abgelehnt.
- Die Box und der Telefoniedienst werden aus der Gerätebeschreibung ermittelt. Alle angekündigten Telefonbücher und mehrere Nummern pro Kontakt werden verarbeitet.
- Antwortgrößen sind begrenzt. XML-DTDs und externe Entitäten sind ausgeschaltet.
- DNS wird pro Vorgang aufgelöst; Verbindungen werden an die aufgelösten lokalen/privaten Adressen gebunden. Fremde Server, unzulässige URL-Bestandteile und HTTP-Weiterleitungen werden abgelehnt.
- Fehlerausgaben enthalten keine Kennwörter, Router-Sitzungsparameter oder zurückgegebenen Download-URLs.
- Bei einem fehlgeschlagenen Teilabruf wird kein unvollständiges Telefonbuch als vollständiger neuer Bestand ausgegeben.
- Landes- und Ortsvorwahl werden auch beim Telefonbuchabruf berücksichtigt; ein zusätzlicher Österreich-Fall ist geprüft.
- Die Einstellungen enthalten kein Kennwortfeld. Der Passwortwert in kurzlebigen Zugangsoptionen ist von JSON-Serialisierung ausgeschlossen und wird durch `ToString()` nicht ausgegeben.

HTTP-Digest ist keine Transportverschlüsselung. Diese Version liest TR-064 lokal über HTTP auf 49000; HTTPS auf 49443 ist nicht implementiert. Auch der Live-Callmonitor auf 1012 überträgt unverschlüsselt. Die Oberfläche erläutert die dafür getrennt benötigten Einstellungen.

## Was der Windows-Smoke-Test prüft

`CM-AnrufMonitor.exe --smoke-test --smoke-output <Ordner>` startet die tatsächliche WPF-Anwendung mit einem isolierten temporären Profil:

- Das Fenster lädt und verwendet den gewünschten Blauverlauf.
- Automatische Routerverbindungen, Infobereich und Autostartänderungen sind für den Test ausgeschaltet.
- Ein künstliches Kennwort wird durch Windows-DPAPI gespeichert und wieder gelesen; ein abweichender Routerzugang wird zurückgewiesen, Klartext wird nicht in der Datei gefunden.
- Der Demo-Anruf ist ausdrücklich beschriftet und gelangt nicht in den echten Verlauf.
- Die Breite jeder Reiterbeschriftung wird gegen den tatsächlich benötigten WPF-Text geprüft. Die in 0.2.0 sichtbar gewordene Beschneidung durch äußere Abstände ist in 0.2.1 korrigiert.
- Große Ansicht, Einrichtung mit den FRITZ!Box-Zugangsfeldern und kompaktes Widget werden tatsächlich gerendert und als PNG erfasst.
- Erfolg erzeugt `smoke-result.json` mit `passed: true`; Fehler führen zu einem von null verschiedenen Prozess-Exitcode.

Die Prüfung ersetzt keine interaktive Bedienung auf verschiedenen Windows-10-/11-PCs, Skalierungen oder Monitoren. Sie enthält keinen echten Routerzugang und testet weder einen Telefonanruf noch PayPal oder eine Rückwärtssuche mit persönlichen Rufnummern.

## Erster Test an der eigenen Box

1. Neue EXE starten und die Einrichtung sowie das kompakte Fenster ansehen.
2. Live-Anzeige auf Port 1012 aktivieren und einen eingehenden sowie einen ausgehenden Anruf prüfen.
3. Einen eigenen FRITZ!Box-Benutzer mit den benötigten Telefonie-/Anruflistenrechten eintragen und **Verbindung testen**.
4. **Zugang speichern** und **Jetzt abgleichen**. Einen bereits in der Box vorhandenen Anruf und einen Telefonbuchnamen vergleichen.
5. App beenden, einen weiteren Anruf ausführen und die App wieder öffnen. Der neue Routereintrag muss nach erfolgreichem Abgleich erscheinen.
6. Erneut manuell abgleichen: Bereits bekannte Routereinträge sollten nicht zusätzlich erscheinen.
7. Einen Anruf mit unterdrückter Nummer prüfen: Die App darf keinen Kontakt aus einer anderen Nummer zuordnen.

Für diese Schritte sind der tatsächliche Windows-PC und die FRITZ!Box nötig. Alle Zugangsdaten bleiben beim Anwender.
