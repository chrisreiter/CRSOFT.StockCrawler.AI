# Neuzugänge — was neu an den Markt kommt

Eigener Reiter **Neuzugänge**. Erstnotizen und neue Börsenlistings, bevor sie
handelbar sind.

---

## Warum die Seite überhaupt nötig ist

In CLAUDE.md steht seit Monaten:

> `first_seen_utc` ist kein Erstnotiz-Datum. Das Universum ist die Rangliste
> nach Marktkapitalisierung, ein Wert taucht also erst auf, *nachdem* er
> gestiegen ist. Wer Neulinge auswerten will, braucht die **vollständige**
> Kohorte aus Listing-Ankündigungen, Fehlschläge eingeschlossen.

Diese Sammlung ist genau diese Kohorte. Eingetragen wird bei der
**Ankündigung** — nicht, wenn ein Wert auffällt. Was danach passiert,
entscheidet der Markt und nicht die Auswahl.

**Sie beginnt heute und wächst vorwärts.** Rückwirkend füllen liesse sie sich
nur aus dem eigenen Bestand, und damit wäre der Überlebensirrtum wieder
eingebaut, den sie beheben soll. Ein Neuzugang, der nach dem ersten Tag um
achtzig Prozent fiel und aus jeder Rangliste verschwand, ist dort nie
aufgetaucht — und er ist der Fall, auf den es ankommt.

**Ankündigungen, aus denen nichts wird, bleiben als `ausgefallen` stehen.** Sie
zu löschen wäre dasselbe Auswahlproblem noch einmal: Am Ende stünden nur die
drin, die tatsächlich an den Markt kamen, und die Quote „wie oft wird aus einer
Ankündigung etwas" liesse sich nicht mehr bilden.

---

## Die Quellen — gemessen, nicht vermutet

| Quelle | liefert | Vorankündigung | Stand |
| --- | --- | --- | --- |
| **Nasdaq IPO-Kalender** | Symbol, Firma, Börse, `expectedPriceDate`, Preisspanne | **ja, mit Datum** | 23 Einträge im ersten Lauf |
| **Binance**, Katalog „New Cryptocurrency Listing" (`catalogId=48`) | Titel, Zeitstempel, Artikelcode | ja, Stunden bis Tage | 20 Einträge |
| SEC EDGAR (S-1) | Registrierungen, 539 im August 2026 | ja, Wochen früher | erreichbar, noch nicht angebunden |
| Coinbase / Kraken Produktlisten | vollständige Paarliste | nein — nur Abgleich | erreichbar |
| CoinGecko „neu hinzugefügt" | — | — | **401, nur PRO** |

**Erster gemessener Vorlauf: 9,0 Tage im Mittel.** Das ist die Zahl, an der
sich entscheidet, ob man auf so etwas überhaupt setzen kann — eine Ankündigung,
die am Handelstag selbst kommt, lässt keine Entscheidung mehr zu, egal wie gut
sie sonst wäre.

---

## Zwei Fallen der Quellen

**Binance erlaubt nur bestimmte Seitengrössen.** Gemessen: `pageSize` 5, 10, 20
und 50 antworten mit 200, **30 dagegen mit 400**. Eine Zahl dazwischen zu
wählen sieht harmlos aus und scheitert stumm.

**Der Ticker steht im Titel, nicht in einem Feld.** Er wird aus der Klammer
gelesen — „… (DJTB) …". Die erste Fassung nahm „kurz und ohne Leerzeichen" als
Kriterium und griff damit das Datum am Titelende ab: Ankündigungen standen mit
der Kennung `2026-08-30` in der Tabelle. Jetzt gilt: nur Grossbuchstaben und
Ziffern, zwei bis zwölf Zeichen. **Der Bindestrich ist das
Unterscheidungsmerkmal — ein Ticker hat keinen.**

Findet sich kein Ticker, wird der Titel zur Kennung, damit die Zeile überhaupt
eine hat. Die Anzeige entscheidet dann an der **Form**: kurz und ohne
Leerzeichen wird als Symbol fett gesetzt, alles andere als Titel.

---

## Das Quellen-Protokoll

Je Lauf und Quelle steht fest, ob sie geantwortet hat und mit welcher Meldung,
wenn nicht. **Eine Quelle, die stillschweigend nichts mehr liefert, sieht sonst
aus wie ein Markt ohne Neuzugänge.**

Der erste Lauf ist dafür der beste Beleg: Beide Quellen scheiterten, und das
Protokoll nannte den Grund wörtlich — „The member erwartet of type
System.DateOnly cannot be used as a parameter value" und „400 (Bad Request)".
Ohne diese Zeilen hätte die Seite einfach leer ausgesehen.

---

## Dapper und `DateOnly`

**Dapper kennt `DateOnly` weder als Parameter noch als Ergebnis.** Als Parameter
heisst es „cannot be used as a parameter value"; beim Lesen verlangt es einen
Konstruktor mit `DateTime` an dieser Stelle. Dieselbe Klasse Fehler wie bei den
ValueTuples — der Typ, der im Modell der richtige wäre, ist es an der
Schnittstelle zur Datenbank nicht. `ErwartetAm` ist deshalb ein `DateTime` mit
Uhrzeit auf Mitternacht.

---

## Was die Seite nicht behauptet

Dass sich mit Neuzugängen Geld verdienen lässt. Zwei Dinge stehen dem
entgegen, und beide sind unabhängig von dieser Anwendung:

- **Zuteilung zum Ausgabepreis bekommen Privatanleger praktisch nie.** Wer am
  ersten Handelstag kauft, kauft nach dem Eröffnungssprung — das ist ein
  anderes Geschäft als das, über das die Statistiken zum ersten Tag reden.
- **Die langfristige Unterrendite von Erstnotizen** gehört zu den robustesten
  Befunden der Finanzmarktforschung.

Was die Seite liefert, ist die Voraussetzung dafür, das im eigenen Bestand
**nachzumessen** statt zu glauben: eine vollständige Kohorte mit Startkurs und
Startzeitpunkt. Sobald ein angekündigter Wert im Bestand auftaucht, wird sein
erster bekannter Kurs festgehalten — ab da lässt sich sagen, was aus ihm
geworden ist.

---

## Bedienung

| | |
| --- | --- |
| *Jetzt abholen* | fragt alle Quellen sofort ab |
| *Quellen-Protokoll* | je Lauf und Quelle: geantwortet oder Ausfall, mit Meldung |

Im Tageslauf läuft dasselbe automatisch — **vor** dem Autopiloten, denn die
Kohorte wächst nur vorwärts: Ein verpasster Tag ist eine Lücke, die bleibt.

Schnittstelle: `GET /api/neuzugang/`, `POST /api/neuzugang/sammeln`.
Tabellen `neuzugang` und `neuzugang_lauf`, Migration `042_neuzugaenge.sql`.
