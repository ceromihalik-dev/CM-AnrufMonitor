# Prüfbericht: CM AnrufMonitor 0.3.1

Stand: 26. November 2026. Ersteller: C. Mihalik. Die Anwendung wird als Vorabversion veröffentlicht.

## Ergebnis und Aussagekraft

Alle **338 lokalen Prüfungen in sechs Prüfsuiten** wurden erfolgreich ausgeführt. Der Release-Build der WPF-Anwendung war ohne Warnungen und Fehler erfolgreich. Für 0.3.1 kommen gezielte Regressionen für einzeln nicht auswertbare Antworten, die Fortsetzung der übrigen Anrufliste und die sichtbare Problemliste hinzu. Die Ergebnisse der zusätzlichen nativen Windows-Prüfung stehen im zugehörigen GitHub-Prüflauf und in `windows-smoke.json` des Release-Pakets.

Die lokalen Prüfungen verwenden ausschließlich künstliche Daten und lokale HTTP-/TCP-Server. Sie belegen Parser-, Netzwerk-, Abgleich- und Speicherverhalten. **Eine echte FRITZ!Box wurde nicht angesprochen.** Insbesondere Routermodell, FRITZ!OS, Benutzerrechte und die Telefoniekonfiguration des Anwenders sind noch praktisch zu prüfen.

Der GitHub-Workflow ergänzt einen echten Windows-Start der WPF-Anwendung und eine DPAPI-Prüfung. Er erstellt ein Release-Paket nur, wenn Build, alle Prüfsuiten und dieser Smoke-Test erfolgreich sind. Der überprüfbare Lauf steht unter [GitHub Actions](https://github.com/ceromihalik-dev/CM-AnrufMonitor/actions). Für ein daraus veröffentlichtes ZIP nennen `BUILD-INFO.json` und `windows-smoke.json` den tatsächlichen Prüfstand. Diese Angaben sind kein Nachweis eines Anrufs über echte Hardware.

## Werkzeugstand

| Bestandteil | Stand |
|---|---|
| Anwendung | 0.3.1; Version zentral in `Directory.Build.props` |
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
| `ReverseLookupChecks` | 95 | Vollständiger Snapshot, eindeutige Nummern, Prioritäten, persistenter Cache, Alterung, Fortsetzung nach unklarer Antwort, Restmengen, Warteschlange und Stoppsignale |
| `ReverseLookupProviderChecks` | 75 | Aktuelle Webseitenstruktur, exakte Nummernzuordnung, mehrdeutige Treffer, unklare Antworten, HTTP-Fehler, begrenzte Weiterleitungen, Antwortgröße und Abbruch |

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

## Rückwärtssuche und gespeicherte Namen

- Der Lauf arbeitet mit einem ausdrücklich übergebenen Snapshot von höchstens 2.000 Anrufen. Der UI-Suchfilter wird dafür nicht verwendet.
- Vollständige Nummern werden normalisiert und dedupliziert. Eigene Nummern, interne Nebenstellen, Demodaten, unterdrückte und unvollständige Nummern sowie vom Anbieter nicht unterstützte Länder werden lokal ausgeschlossen.
- Bereits vorhandene eigene und FRITZ!Box-Namen verhindern eine unnötige Internetabfrage. Internetnamen bleiben als separate Quelle gespeichert.
- Anfragen laufen nacheinander. Im Produkt gilt ein Mindestabstand von zwei Sekunden. Abbruch ist auch während einer Anfrage oder Wartezeit möglich; bereits gespeicherte Ergebnisse bleiben erhalten.
- Positive Treffer werden 30 Tage wiederverwendet, negative sieben Tage. Ältere positive Namen bleiben sichtbar. Ein vorübergehender Fehler erhält sie, ein eindeutiger neuer Nichttreffer oder mehrere unterschiedliche Namen hebt sie auf.
- Speicherung erfolgt atomar in `reverse-lookup.json`. Beschädigte Originaldateien werden als `.broken` bewahrt. Falsche Anbieterkennungen und abweichende Ergebnisnummern werden abgewiesen.
- Der Anbieter vergleicht die gesamte Rufnummer innerhalb desselben Ergebnisobjekts. Werbung, Seitentitel und die allgemeine FAQ sind keine Teilnehmernamen. Unterschiedliche Namen zur gleichen Nummer ergeben `Ambiguous`.
- Die Anfrage verwendet einen offen erkennbaren App-User-Agent. Aktuelle Such- und Detailweiterleitungen werden nur über HTTPS zum festgelegten Anbieterhost und höchstens zwei Schritte weit verfolgt. Suchrouten müssen dieselbe Rufnummer enthalten; bei Detailrouten ist nur die beobachtete Form mit numerischer Eintragskennung zulässig. Der Ergebniseintrag muss weiterhin die vollständige angefragte Rufnummer enthalten.
- Antworten sind auf 2 MiB begrenzt; eine Anfrage einschließlich Weiterleitung hat eine Zeitgrenze von 15 Sekunden. HTTP 429, erforderliche Anmeldung und echte Sperren stoppen den Lauf sofort. Eine einzelne nicht auswertbare Antwort wird getrennt und ohne negativen Cache übersprungen. Nach drei nicht auswertbaren Anbieterantworten ohne erfolgreich ausgewertete neue Antwort dazwischen stoppt der Lauf. Cachetreffer, lokal nicht unterstützte Nummern und vorübergehende Fehler setzen den Zähler nicht zurück. Es gibt keine automatischen Wiederholungen, Proxywechsel oder Browser-Imitation.

### Korrektur des gemeldeten Abbruchs in 0.3.1

Der gemeldete Screenshot zeigt 22 bearbeitete von 157 Nummern, 21 Nichttreffer und einen Fehler. Die wörtliche Fehlermeldung stammt aus dem allgemeinen Parser-Fallback. In 0.3.0 lieferte dieser Fallback `Blocked`, auch wenn weder eine Zugriffssperre noch eine Bestätigungsseite erkannt worden war. Der Stapeldienst beendete deshalb alle folgenden Abfragen. Aus dem Screenshot lässt sich die konkrete HTML-Antwort der betroffenen Nummer nicht rekonstruieren.

Die neue Einordnung `UnrecognizedResponse` wird nicht als Nichttreffer gespeichert und überschreibt keinen vorhandenen Namen. Die zugehörige Regression verwendet 157 synthetische Nummern, 8 gültige Cacheeinträge und eine unlesbare Antwort an Position 22. Sie prüft, dass nachfolgende Nummern verarbeitet werden. Zusätzliche Fälle prüfen wiederholte unlesbare Antworten, unveränderte echte Stoppsignale, Restmengen und lokale Fehlerdetails ohne HTML-Inhalte.

### Beobachtete Anbieterantworten

Am 19. November 2026 wurde die aktuelle öffentliche Webseite mit wenigen veröffentlichten Geschäftsnummern geprüft. Der ältere `/Controller`-Suchpfad lieferte HTTP 410. Die aktuelle Rückwärtssuche verwendet bei Treffern eine HTTP-302-Weiterleitung zur Ergebnisliste beziehungsweise bei einem einzelnen Eintrag zur Detailseite. Bei einem eindeutigen Nichttreffer liefert sie eine echte HTTP-404-Ergebnisseite. Diese 404 wird nur mit passender Suchnummer und dem konkreten Nichttreffer-Element akzeptiert. Allgemeine Fehlerseiten gelten nicht als „kein Eintrag“.

Die aktuelle Ergebnisliste enthält den Namen in `hitlnk_name` und die Rufnummer innerhalb der zugehörigen `phoneblock`-Struktur. Die geprüfte Detailseite verwendet zusammengehörige `Organization`-Microdata mit vollständiger Rufnummer und Name. Solche strukturierten Angaben können per CSS ausgeblendet sein; Skripte und inaktive Vorlagen werden dagegen nicht als Einträge ausgewertet. Der Parser wurde eigenständig für die beobachteten Strukturen geschrieben; es wurden kein jFritz-Code und keine jFritz-Regulärausdrücke übernommen. Reduzierte künstliche Prüffälle bilden die Struktur nach. Die vollständige Geschäftsseitenantwort bleibt außerhalb des Repositorys.

Der tatsächliche Produktionsanbieter lieferte im Live-Test für den veröffentlichten Geschäftsanschluss von „Juwelier Leicht, Juwelier im Hotel Adlon“ den eindeutigen Status `Found` und genau diesen Namen. Eine separat erfasste Sparkassenliste mit unterschiedlichen Filialbezeichnungen wurde korrekt als `Ambiguous` erkannt; eine echte Nichttrefferseite als `NotFound`. Details und reproduzierbare, ausdrücklich aufgerufene Prüfmodi stehen unter `tests/ReverseLookupProviderChecks/README.md`.

Am 26. November 2026 wurden dieselben drei veröffentlichten Geschäftsbeispiele erneut abgerufen. Die beobachteten Strukturen für Detailtreffer, Nichttreffer und mehrere Filialtreffer waren weiterhin vorhanden. Daraus folgt keine Aussage über den unbekannten Einzelfall im Nutzerscreenshot.

Ein Live-Ergebnis belegt die Erreichbarkeit und Struktur zum Prüfzeitpunkt, keine zugesicherte Verfügbarkeit oder Trefferquote. Die automatisierten Prüfsuiten und der Windows-Test fragen keine persönlichen Rufnummern und keinen externen Dienst ab.

## Was der Windows-Smoke-Test prüft

`CM-AnrufMonitor.exe --smoke-test --smoke-output <Ordner>` startet die tatsächliche WPF-Anwendung mit einem isolierten temporären Profil:

- Das Fenster lädt und verwendet den gewünschten Blauverlauf.
- Automatische Routerverbindungen, Infobereich und Autostartänderungen sind für den Test ausgeschaltet.
- Ein künstliches Kennwort wird durch Windows-DPAPI gespeichert und wieder gelesen; ein abweichender Routerzugang wird zurückgewiesen, Klartext wird nicht in der Datei gefunden.
- Der Demo-Anruf ist ausdrücklich beschriftet und gelangt nicht in den echten Verlauf.
- Die Breite jeder Reiterbeschriftung wird gegen den tatsächlich benötigten WPF-Text geprüft. Die in 0.2.0 sichtbar gewordene Beschneidung durch äußere Abstände ist in 0.2.1 korrigiert.
- Eine ausdrücklich markierte, synthetische Anrufliste enthält drei Zeilen mit zwei verschiedenen Rufnummern. Ein lokaler Testanbieter ersetzt den Internetdienst. Der Test prüft den gesamten Verlauf trotz aktivem Filter, nur eine Anfrage für die doppelte unbekannte Nummer, Namensanzeige in beiden Zeilen, spätere Anrufe, Suche, Widget, Herkunft, Speicherung und Vorrang des eigenen Kontakts.
- Ein zusätzlicher Ablauf enthält eine nicht auswertbare Nummer vor einem späteren erfolgreichen Treffer. Er prüft getrennte Zähler, Restmenge, sichtbaren Fehlergrund, den späteren Namen und die nutzbare Problemliste, auch bei 600 Pixel Fensterhöhe.
- Große Ansicht, Verlauf mit erkannten Namen, Einrichtung mit den FRITZ!Box-Zugangsfeldern und kompaktes Widget werden tatsächlich gerendert und als PNG erfasst.
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
8. Im Verlauf **Namen für gesamte Liste suchen** starten. Einen tatsächlichen öffentlichen Treffer und alle Vorkommen seiner Nummer vergleichen. Quelle und Datum prüfen, die App neu starten und die weiterhin sichtbare Zuordnung kontrollieren.

Für diese Schritte sind der tatsächliche Windows-PC und die FRITZ!Box nötig. Alle Zugangsdaten bleiben beim Anwender.
