# CM AnrufMonitor 0.2.0

Ein Windows-Desktopfenster für Anrufe an deiner FRITZ!Box, mit kompakter Widget-Ansicht, automatischem Anruflistenabgleich und Kontaktnamen. Ersteller: **C. Mihalik**.

Die Oberfläche verwendet den gewünschten Blauverlauf von `#2773CB` nach `#104281`, weiße Schrift und gelbe Akzente. Kleine Schaltflächen für **Info** und **Unterstützen** bleiben auch in der kompakten Ansicht erreichbar.

**[Windows-Version herunterladen](https://github.com/ceromihalik-dev/CM-AnrufMonitor/releases)** · **[Builds und Prüfungen](https://github.com/ceromihalik-dev/CM-AnrufMonitor/actions)** · [Anleitung](Anleitung.html) · [Prüfbericht](PRUEFBERICHT.md)

## Was die App macht

- Zeigt eingehende und ausgehende Anrufe live an, einschließlich mehrerer gleichzeitiger Gespräche.
- Lädt die auf der FRITZ!Box gespeicherte Anrufliste nach. So erscheinen nach dem nächsten Start auch Anrufe, während der PC aus war.
- Gleicht beim Start, nach Wiederverbindung, regelmäßig alle fünf Minuten und kurz nach einem beobachteten Gesprächsende ab.
- Verbindet Live-Aufzeichnungen und Routereinträge, wenn sie eindeutig zusammengehören. Wiederholtes Abgleichen erzeugt keine neuen Kopien bereits bekannter Routereinträge.
- Ruft die Telefonbücher der FRITZ!Box ab und zeigt passende Namen im Widget und im Verlauf an.
- Bietet für unbekannte deutsche Rufnummern eine ausdrücklich angeklickte Internet-Rückwärtssuche über Das Örtliche.
- Speichert bis zu 2.000 Anrufe lokal; mit Suche, Filtern und CSV-Export.
- Bietet eigene Kontakte, XML-/CSV-Import, Infobereich, optionale Vordergrundanzeige, Deckkraft und Windows-Autostart.

Das Widget ist ein eigenes verschiebbares Windows-Fenster. Die App besitzt keine Audio- oder SIP-Telefonfunktion. Telefonieren und Annehmen erfolgt am Telefon.

## In Betrieb nehmen

Das Windows-x64-ZIP vollständig in einen eigenen Ordner entpacken und `CM-AnrufMonitor.exe` starten. Eine separate .NET-Installation ist nicht nötig. Die ausführliche Anleitung ist als `Anleitung.html` im ZIP enthalten. Die ausführbare Datei ist nicht mit einem Herausgeberzertifikat signiert.

### 1. Live-Anzeige

1. Am direkt mit der FRITZ!Box verbundenen Telefon `#96*5*` wählen, um den Callmonitor einzuschalten.
2. In der App unter **Einrichtung** die Box-Adresse eintragen, gewöhnlich `fritz.box`, alternativ die tatsächliche lokale IP-Adresse.
3. Den Live-Port **1012** verwenden und verbinden.

Die Live-Schnittstelle braucht kein Routerkennwort. Zum späteren Abschalten dient `#96*4*`; das betrifft auch andere Anrufmonitor-Programme.

### 2. Anrufliste und Namen automatisch abgleichen

1. In der FRITZ!Box-Oberfläche unter **Heimnetz → Netzwerk → Netzwerkeinstellungen**, gegebenenfalls **weitere Einstellungen**, die Option **Zugriff für Anwendungen zulassen** im Bereich **Heimnetzfreigaben** einschalten. Je nach FRITZ!OS liegt sie in einer eigenen Registerkarte.
2. Unter **System → FRITZ!Box-Benutzer** einen Benutzer mit Zugriff auf Telefonie und Anrufliste verwenden. Die Berechtigung heißt je nach Version etwa **Sprachnachrichten, Faxnachrichten, FRITZ!App Fon und Anrufliste**.
3. In der App unter **Anrufliste und Telefonbücher abgleichen** Benutzername und Kennwort eintragen. Der getrennte **TR-064-Port ist 49000**.
4. **Automatischen FRITZ!Box-Abgleich aktivieren** und **FRITZ!Box-Telefonbücher zur Namenserkennung laden** auswählen.
5. Mit **Verbindung testen** den Zugriff prüfen, dann **Zugang speichern**. **Jetzt abgleichen** lädt die Anrufliste und Telefonbücher sofort.

Den Routerzugang nur in der App eingeben. Ein neues Kennwort wird beim Speichern mit Windows-DPAPI für das aktuelle Benutzerkonto geschützt. Ein leeres Kennwortfeld behält den vorhandenen Zugang bei, sofern Host, Port und Benutzer übereinstimmen. **Kennwort löschen** entfernt ihn und schaltet den automatischen Abgleich aus.

Live-Anzeige und Listenabgleich arbeiten unabhängig: Für das Nachladen muss der TCP-Callmonitor nicht verbunden sein. Beide verwenden die eingetragene FRITZ!Box-Adresse. Die Telefonbücher werden beim Start, beim manuellen Abgleich und danach bei einem fälligen Abgleich nach sechs Stunden erneut gelesen.

## Wie die Namenserkennung funktioniert

| Quelle | Verhalten |
|---|---|
| Eigene Kontakte und manuell importierte Kontakte | Höchste Priorität; lassen sich in der App bearbeiten. |
| Automatisch geladene FRITZ!Box-Telefonbücher | Werden getrennt zwischengespeichert und der überprüften Box zugeordnet. Mehrere Nummern pro Kontakt sind möglich. |
| Name aus der FRITZ!Box-Anrufliste | Wird verwendet, wenn kein passender Kontakteintrag vorhanden ist. |
| Internet-Rückwärtssuche | Öffnet erst nach einem Klick die vollständige deutsche Rufnummer bei Das Örtliche im Standardbrowser. Ergebnisse werden nicht automatisch in die App übernommen. |

Landesvorwahl und Ortsvorwahl unter **Einrichtung** richtig setzen, in Deutschland beispielsweise `+49` und die eigene Ortsvorwahl. So kann die App verschiedene Schreibweisen derselben vollständigen Nummer zuordnen. Sie verwendet keine unsicheren Vergleiche anhand weniger Endziffern.

Die Internet-Rückwärtssuche ist für veröffentlichte deutsche Rufnummern gedacht. Ohne passenden öffentlichen Eintrag oder bei widersprochener Rückwärtssuche gibt es keinen Namen. Unterdrückte Rufnummern kann auch diese App nicht ermitteln. Ein gefundener Name ist eine Zuordnung der übermittelten Nummer, kein Identitätsnachweis des Anrufers.

## Was der Abgleich leisten kann

Der PC muss für die Live-Anzeige eingeschaltet sein. War er aus oder die App beendet, werden beim nächsten erreichbaren Abgleich die noch auf der FRITZ!Box vorhandenen Einträge übernommen. Bereits auf dem Router gelöschte oder nicht gespeicherte Anrufe lassen sich nicht nachträglich rekonstruieren. Die App fordert höchstens 2.000 Einträge an und begrenzt auch ihren lokalen Verlauf auf 2.000 Einträge.

Die FRITZ!Box liefert historische Startzeiten gewöhnlich minutengenau und Gesprächsdauern auf Minuten aufgerundet. Nur aus der Box importierte Dauern sind deshalb mit **≈** gekennzeichnet. Bereits vorhandene genaue Live-Werte bleiben erhalten.

Für die Zusammenführung werden Gerätekennung, Router-Anruf-ID, Richtung, Startminute, vollständige Rufnummern und passende Dauer ausgewertet. Wiederverwendete IDs auf anderen Tagen werden nicht blind gleichgesetzt. Bei unterdrückten Nummern oder mehreren ununterscheidbaren Anrufen kann die Zuordnung mehrdeutig sein: Solche Einträge bleiben getrennt, und der Abgleich meldet das. Eine Garantie, jeden Grenzfall ohne sichtbare Doppelung aufzulösen, wäre falsch.

**Verlauf löschen** entfernt lokale Einträge. Solange der Abgleich aktiv ist, können noch auf der FRITZ!Box vorhandene Anrufe erneut erscheinen. Die App verändert oder löscht keine Router-Anrufliste und keine Router-Telefonbücher.

## Daten und Verbindung

Einstellungen, eigene Kontakte, Routerkontakt-Cache und Verlauf liegen unter `%LOCALAPPDATA%\CM\AnrufMonitor`. Das Kennwort liegt separat in `Credentials.bin`, mit DPAPI für das aktuelle Windows-Benutzerkonto geschützt und an Routeradresse, Port und Benutzer gebunden. Anruf- und Kontaktdaten sind lokale, nicht zusätzlich verschlüsselte Dateien. Das Kopieren des Datenordners auf ein anderes Windows-Konto ersetzt keine erneute Kennworteingabe.

Die App liest die Box im eigenen Heimnetz oder über eine bestehende passende VPN-Verbindung. Der Callmonitor auf TCP 1012 ist unverschlüsselt. Diese Version nutzt für TR-064 HTTP mit Digest-Anmeldung auf 49000; Digest verschlüsselt nicht die übertragenen Anruf- und Kontaktdaten. Es ist keine TLS-Verbindung auf 49443 implementiert. Keine Internet-Portfreigabe für diese Dienste einrichten.

TR-064-Zugriffe sind auf lokale/private Adressen begrenzt. Downloadadressen aus der Box werden geprüft, Weiterleitungen und fremde Zielserver abgelehnt. XML-DTDs und externe Entitäten sind deaktiviert. Es gibt keine Telemetrie. Browser-Schaltflächen für Projekt, freiwillige Unterstützung und Rückwärtssuche öffnen erst nach einem Klick den jeweiligen externen Dienst.

## Bauen und prüfen

Benötigt wird das **.NET 10 SDK**, im Projekt auf **10.0.404** eingestellt. Die Anwendung verwendet keine zusätzlichen NuGet-Anwendungspakete.

Unter Windows in PowerShell:

```powershell
.\Build.ps1
.\artifacts\win-x64\CM-AnrufMonitor.exe --smoke-test --smoke-output .\qa-output
.\tools\Package-Windows.ps1
```

`Build.ps1` führt vier Prüfsuiten aus und erzeugt die eigenständig startbare EXE mit eingebetteter Laufzeit. Die Windows-Ausführungsrichtlinie muss die Ausführung des lokalen Skripts erlauben; das Skript ändert keine Richtlinie. Ein ARM64-Build ist mit `Build.ps1 -Runtime win-arm64` möglich. Veröffentlicht wird x64.

Die plattformunabhängigen Prüfungen lassen sich einzeln ausführen:

```powershell
dotnet run --project tests/CoreChecks/CoreChecks.csproj -c Release
dotnet run --project tests/NetworkChecks/NetworkChecks.csproj -c Release
dotnet run --project tests/HistorySyncChecks/HistorySyncChecks.csproj -c Release
dotnet run --project tests/Tr064Checks/Tr064Checks.csproj -c Release
```

Der Windows-Smoke-Test öffnet ein echtes WPF-Fenster mit eindeutig markierten Demo-Daten, prüft DPAPI und rendert beide Ansichten. Er verwendet einen isolierten temporären Datenordner, verbindet sich nicht mit einem Router und verändert keinen Autostart. `smoke-result.json` und die PNGs dokumentieren diesen begrenzten Test, keinen echten Telefonanruf.

Der GitHub-Workflow baut unter Windows, führt alle Prüfungen und den WPF-Smoke-Test aus und veröffentlicht erst danach ein als Vorabversion gekennzeichnetes ZIP mit SHA-256-Prüfsumme. Bereits vorhandene Releases einer Version werden nicht überschrieben. `BUILD-INFO.json` im ZIP nennt den verwendeten Quellcode-Commit und den tatsächlichen Prüfstand. Ein echter Praxistest an der FRITZ!Box des Anwenders bleibt erforderlich.

## Projektstruktur

| Pfad | Inhalt |
|---|---|
| `src/CM.AnrufMonitor.Core` | Live-Parser, TCP-Client, TR-064/Digest, Kontakte, Verlauf und Zusammenführung |
| `src/CM.AnrufMonitor.App` | WPF-Oberfläche, Widget, DPAPI, Timer und Windows-Integration |
| `tests/CoreChecks` | 44 Prüfungen der vorhandenen Kernfunktionen |
| `tests/NetworkChecks` | 6 Szenarien mit echten lokalen TCP-Verbindungen |
| `tests/HistorySyncChecks` | 54 Prüfungen von Abgleich, Migration und Kontakt-Cache |
| `tests/Tr064Checks` | 64 Prüfungen einschließlich lokalem Digest-HTTP-Server |
| `.github/workflows/windows-build.yml` | Windows-Prüfung, Paket und Release |

`CallTracker` wird auf einem Thread verwendet; die App führt Änderungen auf dem WPF-Dispatcher zusammen. Gleichzeitige Abrufe werden vermieden. Ein Wechsel des Zugangs verwirft Ergebnisse noch laufender alter Abrufe. Beschädigte JSON-Dateien bleiben als `.broken`-Kopie erhalten und werden sichtbar gemeldet.

## Ersteller und Unterstützung

**C. Mihalik** · [GitHub-Profil](https://github.com/ceromihalik-dev) · [Freiwillig unterstützen](https://www.paypal.com/donate/?hosted_button_id=Y39Q96VMSJWG2)

Die App zeigt diese Angaben über kleine Schaltflächen. Die Unterstützung ist freiwillig und schaltet keine Funktionen frei.

## Schnittstellenquellen

- [FRITZ!: TR-064 First Steps](https://fritz.support/resources/TR-064_First_Steps.pdf)
- [FRITZ!: X_AVM-DE_OnTel, Anrufliste und Telefonbücher](https://fritz.support/resources/TR-064_Contact_SCPD.pdf)
- [FRITZ!: Zugriff von Anwendungen einrichten](https://fritz.com/apps/knowledge-base/FRITZ-App-Smart-Home/3668_FRITZ-App-Smart-Home-einrichten/)
- [fritzconnection: Call Monitoring](https://fritzconnection.readthedocs.io/en/latest/sources/call_monitoring.html)
- [Home Assistant: FRITZ!Box Call Monitor](https://www.home-assistant.io/integrations/fritzbox_callmonitor/)
- [Das Örtliche: Voraussetzungen und Grenzen der Rückwärtssuche](https://www.dasoertliche.de/rueckwaertssuche/)
- [Microsoft: Single-file deployment](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview)

FRITZ! und FRITZ!Box sind Marken ihres jeweiligen Rechteinhabers. CM AnrufMonitor ist eine unabhängige Anwendung. Das FRITZ!-Logo wird nicht als Programmlogo verwendet. Die Hinweise zu mitgelieferten Microsoft-Komponenten stehen in `Lizenzhinweise`.
