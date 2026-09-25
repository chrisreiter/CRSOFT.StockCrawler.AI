# Der Hausmeister — was nach welcher Frist entbehrlich ist

**CRSOFT.StockCrawler**, Stand 25.09.2026. Oberfläche: *System → Hausmeister*.
Läuft täglich am Ende des Tageslaufs; Einstellungen in `appsettings.json`
unter `Housekeeping`.

---

## Warum es ihn braucht

Die Datenbank wächst an Stellen, die niemand liest. Gemessen am 25.09.2026,
nach einem Monat Betrieb, vor dem ersten Lauf:

| Tabelle | Zeilen | MB | Befund |
| --- | ---: | ---: | --- |
| `forecast_track` | 9.336.570 | 956 | ein Rückrechnungslauf ab 2001 |
| `forecast` | 1.285.137 | 802 | davon 42.659 dauerhaft unbewertbar |
| `crossing` | 7.207.370 | 431 | zwei Jahre |
| `price_bar` | 3.998.324 | 386 | **geschützt** |
| `forecast_component` | 6.425.685 | 317 | nach der Bewertung ohne Zweck |
| `curve_link` + `curve_event` | 3.157.921 | 332 | davon **acht überholte Läufe** |

Die Kurvendiskussion liest ausschliesslich `MAX(run_id)` je Intervall und
Glättungsart. Acht ältere Läufe mit zusammen 1,84 Millionen Zeilen liest
nichts mehr. Das ist kein Randfall, sondern das Muster: Was einmal gerechnet
und ersetzt wurde, bleibt liegen.

---

## Die Regel hinter den Regeln

**Eine Zeile darf weg, wenn nachweislich keine Ansicht und keine Messung sie
mehr liest — nicht, wenn sie alt ist.** Deshalb steht zu jeder Regel in der
Oberfläche, *wer* die Zeilen las und *was verloren geht*. Der Bestand ist in
diesem Projekt die Beweislage; wer ihn kürzt, kürzt sie mit.

**Was keine Einstellung anfassen kann** — diese Tabellen kommen in keiner
Regel vor:

`price_bar` · `asset` · `invest_buchung`, `invest_konto`,
`invest_kontobewegung` · `app_user`, `app_session`, `app_state` ·
`pillar_weight`, `pillar_skill` · `model_weight` (dort steckt alles, was die
erste Säule gelernt hat) · **jede Prognose, deren Zielzeitpunkt noch
aussteht**, unabhängig von ihrem Alter.

---

## Die elf Regeln

| Schlüssel | Tabelle | Frist | Was verloren geht |
| --- | --- | ---: | --- |
| `kurvenlaeufe` | `curve_event`, `curve_link` | 14 T | der Vergleich mit einem älteren Lauf; der jüngste je Intervall **und** Glättungsart bleibt immer |
| `rueckrechnung` | `forecast_track` | 1825 T | der gezeichnete Verlauf reicht weniger weit zurück; neu rechnen geht jederzeit |
| `prognose_komponenten` | `forecast_component` | 30 T | die Aufschlüsselung, welches Teilmodell eine alte Prognose trug |
| `unbewertbar` | `forecast` | 30 T | nichts Messbares — diese Prognosen können nie bewertet werden |
| `kreuzungen` | `crossing` | 730 T | ältere Kreuzungen zählen nicht mehr in die Bewährung; entstehen bei einer Neuberechnung neu |
| `bewertete_prognosen` | `forecast` (+ Bewertungen) | 365 T | die Lernkurve reicht weniger weit zurück; das Gelernte steckt in `model_weight` |
| `autopilot_protokoll` | `autopilot_lauf`, `autopilot_entscheidung` | 365 T | die Begründung einzelner Entscheidungen; die Buchungen bleiben |
| `laufprotokoll` | `ingest_run` | 90 T | die Geschichte der Abrufe |
| `reasoning_protokoll` | `reasoning_log` | 90 T | nicht gemerkte Antworten; **gemerkte bleiben** |
| `nachrichten` | `knowledge_source`, `knowledge_chunk` | 365 T | ältere Meldungen aus Suche und Journal, **samt Qdrant-Vektoren** |
| `eigenes_protokoll` | `housekeeping_*` | 365 T | ältere Aufräumläufe |

`0` schaltet eine Regel ab — sie erscheint dann als *abgeschaltet*, was sich
von *nichts gefunden* unterscheidet.

### Zu drei Fristen im Einzelnen

**`rueckrechnung` = 5 Jahre.** Der Walk-Forward wurde nachträglich über
bereits bekannte Kurse gerechnet und belegt deshalb keine Prognosegüte (siehe
CLAUDE.md); er ist aus jeder Auswertung ausgeschlossen und dient allein der
Anzeige eines durchgehenden Verlaufs. Fünf Jahre decken jede Kursansicht ab,
und es war mit 956 MB der grösste Posten des Bestands.

**`bewertete_prognosen` = 1 Jahr.** Diese Zeilen tragen die Lernkurve — den
einzigen Nachweis, ob die Prognose besser wird. Ein Jahr deckt jede Aussage
ab, die diese Ansicht trifft. Kürzer wäre eine Kürzung der Beweislage.

**`nachrichten` = 1 Jahr, mit Qdrant.** Ein Artikel besteht aus einer Zeile
in `knowledge_source`, seinen Abschnitten in `knowledge_chunk` und seinen
Vektoren in Qdrant. Wer nur die Datenbank aufräumt, hinterlässt verwaiste
Vektoren — die erzeugen keinen Fehler, sondern *weniger Treffer*. Deshalb
geht die Regel über `IKnowledgeService.DeleteAsync`, das beide Seiten räumt.

---

## Probelauf, zweistufige Freigabe, Blöcke

**Der Probelauf zählt mit denselben Bedingungen, mit denen später gelöscht
wird** — nicht mit ähnlichen. Er ist die Voreinstellung des Endpunkts: Wer
`probe` nicht angibt, bekommt gezählt.

Eine Eigenheit, die in der Oberfläche auch dort steht: **Überschneiden sich
zwei Regeln, zählt der Probelauf dieselbe Zeile zweimal.** Gemessen am
25.09.: Probelauf 8.385.078, tatsächlich gelöscht 8.256.953 — die Differenz
sind Komponenten, die sowohl unter `prognose_komponenten` als auch unter
`unbewertbar` fielen. Gelöscht wird jede Zeile einmal.

**Zweistufige Freigabe statt `confirm()`.** Der Knopf „Jetzt aufräumen" stand
zuerst allein da und fragte über `confirm()` nach. Am 25.09.2026 lief
daraufhin ein Löschlauf, den niemand bewusst ausgelöst hatte: Ein `confirm()`
beantwortet sich in Automatisierungen, Browser-Erweiterungen und
Prüfwerkzeugen von selbst. Für eine Funktion, die Millionen Zeilen löscht,
ist das zu wenig. Jetzt gilt:

1. Der Knopf ist **gesperrt**, bis in dieser Sitzung ein Probelauf lief — man
   muss gesehen haben, was betroffen wäre.
2. Danach zeigt er die Zahl: *„Jetzt aufräumen (1.143 Zeilen)"*.
3. Der erste Klick stellt ihn scharf: *„Wirklich 1.143 Zeilen löschen?"*, rot.
   Der zweite Klick binnen zehn Sekunden löscht; sonst fällt er zurück.
4. Findet der Probelauf nichts, steht dort *„Nichts aufzuräumen"*, gesperrt.

**Blockweise und mit Zeitbudget.** Ein DELETE über sechs Millionen Zeilen
sperrt die Tabelle und bläht das Transaktionsprotokoll; währenddessen steht
der stündliche Kursabruf. Gelöscht wird in Blöcken zu 25.000 Zeilen
(`SqlDialekt.LoescheBlock` — `DELETE TOP (n)` gegen
`DELETE … WHERE pk IN (SELECT pk … LIMIT n)`). Läuft das Budget von 20
Minuten ab, bricht der Lauf nach der laufenden Regel ab und merkt es im
Protokoll; der nächste Tag macht weiter.

**Eine Regel, die scheitert, reisst die übrigen nicht mit** — sie betreffen
verschiedene Tabellen. Der Fehler steht in ihrer Zeile.

---

## Der erste Lauf, gemessen

25.09.2026, 8.256.953 Zeilen in 233 Sekunden:

| Regel | Zeilen | Dauer |
| --- | ---: | ---: |
| `rueckrechnung` | 4.825.646 | 156,2 s |
| `kurvenlaeufe` | 1.843.740 | 61,5 s |
| `prognose_komponenten` | 1.557.865 | 11,1 s |
| `unbewertbar` | 29.610 | 0,8 s |
| `nachrichten` | 92 | 2,8 s |
| die übrigen sechs | 0 | < 1 s |

Wirkung auf den Bestand:

| Tabelle | vorher | nachher |
| --- | ---: | ---: |
| `forecast_track` | 9,3 M / 956 MB | 4,5 M / **490 MB** |
| `forecast_component` | 6,4 M / 317 MB | 4,9 M / **240 MB** |
| `curve_link` | 2,1 M / 230 MB | 934 k / **102 MB** |
| `curve_event` | 1,0 M / 102 MB | 380 k / **37 MB** |

Nachgeprüft: die jüngsten Kurvenläufe (9 zentriert, 7 kausal) vollständig mit
243.312 + 617.179 und 136.365 + 317.325 Zeilen, 668.737 offene Prognosen,
`price_bar` 3.998.324, `asset` 702, `invest_buchung` 28, `model_weight`
22.795 — alle unverändert.

**Hinweis zum Platz:** SQL Server gibt gelöschte Seiten nicht sofort an das
Dateisystem zurück; `forecast` zeigt weiterhin 802 MB bei 30.000 Zeilen
weniger. Der Platz ist wiederverwendbar, nicht zurückgegeben. Wer ihn
zurückhaben will, baut die Indizes neu — das ist Sache des Betreibers, nicht
des Hausmeisters.

---

## Aufrufe

| | |
| --- | --- |
| `GET /api/housekeeping/` | Stand: letzte Läufe, Tabellengrößen, Einstellungen |
| `POST /api/housekeeping/lauf` | **Probelauf** (Voreinstellung `probe=true`) |
| `POST /api/housekeeping/lauf?probe=false` | löscht |

Protokolliert wird jeder Lauf in `housekeeping_lauf` und jede Regel in
`housekeeping_regel` — auch die mit null Treffern: Eine Regel, die nichts
findet, ist ein Ergebnis und unterscheidet sich von einer, die nicht lief.

Migration: `infra/sql/046_housekeeping.sql` und
`infra/pgsql/015_housekeeping.sql`.
