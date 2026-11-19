# Prüfung des Anbieters Das Örtliche

Die normale Testausführung arbeitet vollständig offline. Sie sendet weder Testnummern noch Anruflisten ins Internet.

```sh
dotnet run --project tests/ReverseLookupProviderChecks/ReverseLookupProviderChecks.csproj
```

Stand 19. November 2026: 70 Prüfungen erfolgreich. Die Parser- und Transportgrenzen wurden zusätzlich unabhängig geprüft.

## Öffentliche Quellen und beobachtete Seitenstruktur

- Die aktuelle [Rückwärtssuche](https://www.dasoertliche.de/rueckwaertssuche/) verwendet `GET /rueckwaertssuche/?ph=<vollständige nationale Rufnummer>`.
- Trefferlisten können auf `https://www.dasoertliche.de/?form_name=search_inv&ph=...` weiterleiten. Ein eindeutiger Treffer leitete auf `/?form_name=detail&id=<numerische Eintragskennung>` weiter. Der Client folgt höchstens zwei geprüften Weiterleitungen innerhalb desselben HTTPS-Ursprungs. Beim Suchformular muss die Rufnummer gleich bleiben; bei der Detailseite wird die vollständige Rufnummer anschließend unabhängig aus dem Eintrag geprüft. Fremde Ziele, unerwartete Parameter und andere Pfade stoppen die Suche.
- Ein beobachteter fehlender Eintrag wurde mit **HTTP 404** und `div.nonumber01` beantwortet. Deshalb bedeutet 404 allein weder „kein Treffer“ noch „gesperrt“. `NotFound` verlangt zusätzlich die ausdrückliche Meldung im Ergebnisbereich und die exakt passende Rufnummer in `input[name=ph]`. Dieselbe Formulierung im allgemeinen FAQ reicht nicht.
- Eine beobachtete Trefferliste verwendet `div.hit`, den Namenslink `a.hitlnk_name` und einzelne mit `Tel.` beschriftete Spans in `div.phoneblock`. Eine Telefonnummer in einer Anzeige, einem anderen Treffer oder einem verschachtelten Datenobjekt darf keinen Namen zuordnen.
- Die beobachtete Detailseite enthält einen `Organization`-Bereich mit `itemprop="name"` und `itemprop="telephone"`. Diese ausdrücklich maschinenlesbaren Angaben sind dort durch CSS ausgeblendet und werden trotzdem ausgewertet. Das ist etwas anderes als ein inaktives HTML-Template: Solche Template-Inhalte bleiben ausgeschlossen.
- Die veraltete jFritz-Route `/Controller?form_name=search_inv&ph=...` lieferte bei der Stichprobe **HTTP 410**. Sie wird nicht verwendet.

Die kleinen HTML-Dateien in `Fixtures` bilden diese Struktur eigenständig nach. Sie enthalten öffentliche Unternehmensangaben, keine Nutzerdaten und keine vollständigen kopierten Webseiten. Der ursprüngliche jFritz-Code und seine regulären Ausdrücke wurden nicht übernommen. Die Recherche zum Arbeitsablauf erfolgte anhand der öffentlichen Projekte [jFritz](https://github.com/jfritz-org/jfritz) und [reverseLookup](https://github.com/jfritz-org/reverseLookup).

Die [Anbieterhilfe](https://www.dasoertliche.de/rueckwaertssuche/) beschreibt die Identifikation unbekannter Anrufer. Die [Nutzungsbedingungen](https://www.dasoertliche.de/nutzungsbedingungen) beschränken die Verwendungszwecke. Eine öffentlich nutzbare Suchseite ist keine zugesicherte, dauerhaft unveränderte API. Insbesondere werden hier kein Adressverzeichnis heruntergeladen, keine Sperren umgangen und keine fremden Kontaktdaten veröffentlicht.

## Was die Prüfungen abdecken

Die Tests prüfen die vollständige Rufnummernzuordnung, getrennte Ergebnisbereiche, doppelte und widersprüchliche Namen, echte Fehlermeldungen gegenüber FAQ-Text, strukturierte JSON-LD- und Microdata-Einträge, transparente Programmkennung, erlaubte und gesperrte Weiterleitungen, 429 mit Wartezeit, 403/410, Antwortgrößen, Zeichensätze und Abbruch während HTTP-Zugriff, Antwortstream und HTML-Auswertung. Skripte werden nicht ausgeführt; Template-, Textarea- und ähnliche inaktive Inhalte werden nicht als Treffer ausgewertet.

Die Produktionsverbindung verwendet HTTPS, die normale Betriebssystem-Proxykonfiguration und `CM-AnrufMonitor/<Version>` mit Projektadresse als User-Agent. Es werden keine Browser-Cookies und keine FRITZ!Box-Anmeldedaten übertragen. Automatische Weiterleitungen sind deaktiviert; erlaubte Suchweiterleitungen werden einzeln geprüft. Der gesamte einzelne Abruf einschließlich Auswertung hat eine Frist von 15 Sekunden, die Antwort nach Dekompression höchstens 2 MiB. Die Anzahl der Anfragen und der Abstand zwischen Rufnummern gehören zum getrennt geprüften Stapeldienst.

## Konkreter Prüfstand außerhalb der Offline-Suite

Eine separat abgerufene öffentliche Seite für die FRITZ-Unternehmensnummer wurde vollständig eingelesen und korrekt als `NotFound` erkannt. Eine separat abgerufene Sparkassen-Trefferliste enthielt sowohl den Unternehmensnamen als auch Namen mit dem Zusatz „SB-Filiale“. Sie wurde korrekt als `Ambiguous` behandelt. Es wurde kein gemeinsamer Namensanfang als eindeutiger Name erfunden.

Der End-to-End-Versuch mit der öffentlich eingetragenen Firma „Juwelier Leicht, Juwelier im Hotel Adlon“ wurde mit dem tatsächlichen Produktionsanbieter, transparenter Programmkennung und normaler Betriebssystem-Proxykonfiguration ausgeführt. Die Antwort war **`Found` mit dem Namen „Juwelier Leicht, Juwelier im Hotel Adlon“**. Der Client folgte dabei der beobachteten erlaubten Detailweiterleitung und prüfte die vollständige Rufnummer anhand desselben `Organization`-Eintrags. Die vollständig separat gespeicherte Antwortseite ergab beim Offline-Abgleich ebenfalls `Found`.

Der manuelle Live-Modus ist nur für diese eine feste öffentliche Unternehmensnummer vorgesehen und wird weder vom normalen Testlauf noch von CI aufgerufen:

```sh
dotnet run --project tests/ReverseLookupProviderChecks/ReverseLookupProviderChecks.csproj -- --live-public-company
```

Bereits separat gespeicherte öffentliche Beispielseiten können ohne erneuten Internetzugriff geprüft werden:

```sh
dotnet run --project tests/ReverseLookupProviderChecks/ReverseLookupProviderChecks.csproj -- --captured /absoluter/pfad/beispiel.html +4983316090 Ambiguous
```

## Grenzen

Es werden nur vollständige deutsche Rufnummern unterstützt. Nicht veröffentlichte Nummern und Einträge mit Widerspruch gegen die Inverssuche liefern keinen Namen. Bei mehreren unterschiedlichen Namen, ungewohnter Seitenstruktur, CAPTCHA oder Zugriffssperre wird kein Name geraten. Ein einzelner verifizierter Internettreffer belegt außerdem keine Identität des tatsächlichen Anrufers, da eine übermittelte Rufnummer missbraucht werden kann. Internetnamen bleiben deshalb getrennt von eigenen Kontakten und dem FRITZ!Box-Telefonbuch.
