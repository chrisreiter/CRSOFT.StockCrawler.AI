# Der Autopilot

Kursansicht → **Investings** → *Autopilot*.

Das System sucht sich selbst Werte aus, kauft und verkauft — parallel zum
Depot, das der Nutzer von Hand führt.

---

## Die Zahl, die vor allem anderen steht

Aus den Live-Messungen dieser Anlage, **entdoppelt** (je Wert und Zieltag nur
die jüngste Prognose) und **ohne das nachträgliche Aufrollen der Vergangenheit**:

| | |
| --- | ---: |
| Richtungstrefferquote auf einen Tag | **0,474** |
| typische Tagesbewegung | 1,55 % |
| Rundlauf (2 × 0,15 %) | 0,30 % |
| **nötige Trefferquote** | **0,597** |
| Erwartungswert je Umschichtung | **−0,45 %** |

Ein Autopilot auf dieser Grundlage verliert Geld. Er ist trotzdem gebaut — aber
**gegen Grundlinien**, nicht gegen sich selbst. Ohne die Grundlinie sähe ein
Verlust von vier Prozent nach Pech aus, obwohl er der rechnerisch erwartete
Ausgang war.

---

## `invers` — die Gegenkontrolle

Die vierte Strategie tut **genau das Gegenteil von `aktiv`**: Wo `aktiv` die
Werte mit der höchsten Erwartung hält, hält `invers` die mit der niedrigsten —
also genau jene, von denen das Modell einen Rückgang erwartet. Gleiche Anzahl,
gleiche Gebühren, gleicher Takt; nur das Vorzeichen des Signals ist gedreht.

Am ersten Lauf abgelesen:

| `aktiv` nähme | | `invers` kaufte | |
| --- | ---: | --- | ---: |
| HWM | +3,731 % | ZEC-USD | −17,077 % |
| BX | +3,047 % | CRM | −10,757 % |
| BA | +3,030 % | MNT-USD | −9,194 % |
| WLD-USD | +2,866 % | AEM | −4,600 % |
| 6098.T | +2,725 % | XRP-USD | −4,291 % |
| NOW | +2,607 % | CRV-USD | −3,832 % |

### Was sie nicht ist

**Keine Hoffnung auf Gewinn.** Die Seite „Umkehrschluss“ hält seit langem fest:
Ein Signal umzudrehen nützt nur, wenn es **zuverlässig falsch** ist. Eine
Trefferquote von 0,505 umgedreht ergibt 0,495, und die Gebühren bleiben
dieselben — als Geldanlage ist das eine leicht teurere Art zu würfeln.

**Kein Leerverkauf.** Dieses System kann nicht leerverkaufen, also *kauft*
`invers` die schlechtbewerteten Werte. Erwartet das Modell zu Recht einen
Rückgang, verliert dieses Depot; irrt es, gewinnt es.

### Wozu sie dann gut ist

Sie ist die Gegenkontrolle, die bisher fehlte. `halten` misst gegen den Markt,
handelt aber nicht und trägt deshalb ein völlig anderes Kostenprofil. `invers`
handelt genauso oft wie `aktiv`, zahlt dieselben Gebühren, ist demselben
Marktgang ausgesetzt — und benutzt dasselbe Signal mit umgekehrtem Vorzeichen.

**Damit ist `aktiv` minus `invers` der Informationsgehalt des Signals**,
bereinigt um Marktgang *und* Kosten. Laufen beide gleich, ist das Signal
Rauschen — und das lässt sich mit `halten` allein nicht zeigen.

### Drei Dinge, die dabei zu beachten waren

**Die Parameter werden von `aktiv` geerbt.** Eine Kontrolle muss dieselben
Parameter tragen wie das, was sie kontrolliert. Die Voreinstellung der Tabelle
hätte `invers` acht Werte gegeben, wo `aktiv` sechs hält — dann unterschieden
sich die beiden in zwei Dingen, und der Unterschied liesse sich keinem von
beiden mehr zuordnen. Derselbe Fehler wie eine gebührenfreie Grundlinie.

**Kein Nemotron-Veto.** Es prüfte, ob ein Kauf plausibel ist, und lehnte damit
genau das ab, was dieses Depot absichtlich tut. Eine Gegenkontrolle, die man vor
sich selbst schützt, ist keine mehr. Die Spalte zeigt deshalb „–“ statt eines
Kästchens.

**`Punktzahl < 0`, nicht einfach die letzten der Liste.** Ein Wert mit Erwartung
null ist kein erwarteter Rückgang, sondern gar keine Aussage. Ihn zu kaufen wäre
Zufall und nicht das Gegenteil von irgendetwas.

### Vier vergessene Listen

Der Strategiename stand an **vier** Stellen als feste Aufzählung: in
`reset_autopilot_depot`, in `set_leitstrategie`, in der Laufliste des Endpunkts
und in `VergleichAsync`. Die ersten beiden meldeten sich laut („Nur streng,
aktiv oder halten …“), die letzte gar nicht — `invers` handelte, und die
Vergleichstabelle zeigte es einfach nicht an. **Eine Liste, die stillschweigend
unvollständig ist, ist die schlimmere Sorte.**

Migration `043_invers.sql`.

---

## Zwei Fallen, die beim Bauen zuschlugen

### 1. Die Rückrechnung im Datenbestand

Der erste Blick auf die Rangfolge zeigte **11 von 15 Werten mit positivem
Erwartungswert** und Trefferquoten bis 0,727. Das widersprach allem, was sonst
gemessen war.

Die Ursache: `forecast` enthält nicht nur Live-Prognosen. Das Aufrollen der
Vergangenheit hat **rückdatierte** Zeilen über bereits bekannte Kurse
geschrieben. Sie sind an `model_version` erkennbar — `ens-1-bt` gegen `ens-1`.

| | Fälle je Tag | Trefferquote |
| --- | ---: | ---: |
| bis 20.08. (`ens-1-bt`) | 200–830 | 0,49–0,73 |
| **ab 21.08. (`ens-1`)** | 1.296–5.615 | **0,466–0,593** |

> Die Regel stand längst in CLAUDE.md — nur für die andere Tabelle: *„Die
> Prognosegüte nur aus Live-Prognosen. `forecast_track` bleibt draussen."*
> Dass dieselbe Verunreinigung auch in `forecast` selbst steckt, war nicht
> aufgeschrieben, und ich bin darauf hereingefallen.

Der Filter ist `model_version NOT LIKE '%-bt'` — an der Fassung erkannt, nicht
an einem Datum. Ein hartkodierter Stichtag würde beim nächsten Rückroll-Lauf
still falsch.

### 2. Rosinen aus dem Rauschen

Auch live bleibt ein Problem. Über 571 Werte mit im Mittel vier bewerteten
Prognosen:

| | |
| --- | ---: |
| beobachtete Streuung der Quoten | 0,285 |
| Streuung durch **reines Würfeln** bei dieser Fallzahl | 0,250 |

Die Unterschiede zwischen den Werten sind also weit überwiegend Rauschen. Wer
daraus den Wert mit der höchsten Quote auswählt, wählt **den glücklichsten,
nicht den besten** — dieselbe Falle wie beim Umkehrschluss, wo 291 auffällige
Paare gefunden wurden, während der Zufall 653 erwarten liess.

Deshalb wird jede Quote **zum Bestandsmittel hin geschrumpft**. Der Faktor kommt
aus der Zerlegung der Streuung selbst: Was nach Abzug des Rauschens übrig
bleibt, darf durchschlagen — sonst nichts. Er wird nicht gesetzt, und er
verschwindet von allein, sobald die Fallzahl wächst.

Wirkung heute:

| Wert | roh | geschrumpft | Verdienst |
| --- | ---: | ---: | ---: |
| NOC | 0,800 | 0,539 | 0,16 |
| LMT | 0,600 | 0,501 | 0 |
| MNST | 0,200 | 0,425 | 0 |

---

## Die drei Strategien

Alle vier Depots — das manuelle eingeschlossen — teilen sich **denselben**
Buchungsapparat: eine Spalte `depot` auf `invest_buchung`,
`invest_kontobewegung` und `invest_konto`. Ein zweiter Satz Tabellen liefe
unweigerlich auseinander, und spätestens beim ersten Fehler in der
Gebührenrechnung gäbe es zwei Fassungen, von denen eine repariert wird.

| Depot | was es tut |
| --- | --- |
| **streng** | handelt nur mit Nachweis: genug bewertete Live-Prognosen, Verdienst über dem Nullpunkt 0,523, Erwartungswert über den Kosten |
| **aktiv** | folgt der Erwartung des Modells, **auch ohne Nachweis** — und zeigt in Euro, was das kostet |
| **halten** | Grundlinie: kauft einmal die Startauswahl gleichgewichtet und rührt sich nie wieder |

`streng` an denselben Nachweis zu binden wie `aktiv` hiesse, zwei gleiche Körbe
zu bauen und einen Vergleich zu verlieren. Dass `streng` heute **null**
Geschäfte macht, ist die Aussage des Ganzen.

**Die Grundlinie zahlt dieselben Gebühren.** Eine kostenfreie Grundlinie wäre
unschlagbar und damit als Vergleich wertlos.

---

## Woraus die Bewertung entsteht

| Bestandteil | Quelle | Verdienst |
| --- | --- | --- |
| Prognose 1 Tag | `combined_return` — **darin stecken alle Säulen** samt ihrer Gewichtung nach `pillar_skill` | geschrumpfte Trefferquote dieses Wertes |
| Prognose 1 Woche | dieselbe Quelle, Horizont 168 h | halbe Anerkennung — für diesen Horizont ist nichts eigenes gemessen |
| Chartmuster | `get_active_triggers` + `bot_trigger_stat` | Richtungstrefferquote des Auslösers |

Die Säulen werden **nicht** ein zweites Mal einzeln geholt. Sie sind in
`combined_return` bereits enthalten; sie daneben noch einmal zu zählen wäre
dieselbe Zahl doppelt.

Beim Chartmuster gelten die beiden Regeln aus der Säulenmischung unverändert:
Dubletten je (Wert, Auslöser) fliegen raus, und ein Muster, dessen gemessener
Überschuss der eigenen Richtung widerspricht, trägt nichts bei — ein Goldenes
Kreuz mit negativem Überschuss ist kein Verkaufssignal, sondern gar keins.

**Solange kein Bestandteil nachgewiesenen Verdienst hat, gibt es nichts zu
gewichten.** Dann wird schlicht gemittelt, und die Anzeige nennt die Grundlage
beim Namen: *„blosse Erwartung des Modells"* statt *„gemessener Verdienst"*.

---

## Drei Riegel, die unabhängig von jedem Signal gelten

1. **Höchstens 20 %** des Depotvermögens je Wert.
2. **Keine zwei Werte mit Korrelation über 0,9.** Die Korbsuche hatte dasselbe
   Problem: HWM, XAUT-USD und PAXG-USD — die letzten beiden sind goldgedeckte
   Marken und laufen praktisch identisch. Ein Korb aus drei Teilen, von denen
   zwei dasselbe sind, ist ein Korb aus zwei Teilen.
3. **Keine Bar der letzten fünf Tage → nicht handelbar.**

### Die Gebühr gehört in den Nenner

Acht Positionen zu einem Achtel des Vermögens ergeben zusammen genau das
Vermögen — und die acht Gebühren kommen obendrauf. Im ersten Lauf scheiterte
die achte Buchung deshalb **zuverlässig** mit „es fehlen 150,00", und zwar
genau um 8 × 18,75. Die Zielgrösse ist seither

```
Vermögen / (Anzahl × (1 + Gebührensatz))
```

Danach: 8 × 12.481,28 + 149,76 Gebühr = exakt 100.000.

---

## Takt und Hysterese

**Umschichtung** einstellbar auf 1T (Voreinstellung), 1W, 1M, 3M, 6M, 1J.

> **Bewertet und protokolliert wird trotzdem jeden Tag.** Sonst wüsste man bei
> Jahrestakt elf Monate lang nicht, ob der Autopilot überhaupt noch läuft. Der
> Takt bestimmt allein, wann gehandelt werden **darf** — der Lauf hält in
> `handelstag` fest, ob er durfte.

Der erste Einstieg wartet nicht auf den Takt: Ein Depot, das noch nie gehandelt
hat, darf sofort kaufen.

**Hysterese** (Voreinstellung 0,30 %, also genau der Rundlauf): Ein Anwärter
verdrängt einen gehaltenen Wert erst, wenn er ihn um mehr als diese Bandbreite
schlägt. Beobachtet am 27.08.: TSLA rutschte aus den besten sechs, FRE.DE lag
**0,050 %** davor — kein Tausch, denn das deckt den Rundlauf nicht.

**Ein Tausch hat zwei Seiten, und die Sperre muss beide kennen.** Die
Ersatzkandidaten kommen aus einer Schlange, aus der je Verkauf einer entnommen
wird. Vorher verglich jeder Verkauf gegen denselben besten Anwärter: Bei vier
Verkäufen und einem guten Anwärter hätten alle vier die Hysterese gerissen, und
das Depot sässe auf Kasse, für die es keinen Ersatz gibt. Ohne die Sperre tauscht ein Rangwechsel um einen Platz täglich hin und
her und zahlt jedes Mal.

Nachgewiesen: Zweiter Lauf unmittelbar nach dem ersten → **null** Geschäfte in
allen drei Depots.

---

## Nemotron

**Veto und Begründung — kein Stimmrecht über Beträge.** Bei den ersten drei
echten Fragen an diesen Agenten hatte er **zweimal null Werkzeugaufrufe**; er
hat frei geantwortet. Ein Modell, das frei antwortet, darf keine Zahlen setzen.

Vor jedem Kauf des `aktiv`-Depots bekommt es die gemessenen Zahlen und soll mit
`{"ablehnen": …, "grund": …}` antworten. Gesucht wird von der ersten
geschweiften Klammer bis zur letzten — ein strenger Parser auf die ganze
Antwort scheitert an einem einzigen einleitenden Satz.

**Fällt es aus, wird gehandelt.** Ein fehlendes Urteil ist ausdrücklich keine
Ablehnung: Sonst entschiede die Verfügbarkeit einer gemieteten Grafikkarte über
das Depot. Im Protokoll steht dann `kein Urteil` — eine eigene Spalte, die sich
von „zugestimmt" unterscheidet. Frist 120 s je Frage, höchstens 8 Fragen je
Lauf.

Die Grundlinie bekommt kein Veto: Sie soll die Auswahl nicht verändern.

---

## Die Einstellungen

Eine Zeile je Strategie, als **Tabelle** mit ausgerichteten Spalten:

| Spalte | Bedeutung |
| --- | --- |
| Ein/Aus | ob der Tageslauf diese Strategie überhaupt anfasst |
| zählt | welche Strategie ins Gesamtvermögen eingeht — genau eine |
| Budget | Startbetrag und Währung — **1.000 USD** als Vorgabe |
| Werte | wieviele Positionen gleichzeitig gehalten werden |
| Umschichtung | 1T · 1W · 1M · 3M · 6M · 1J |
| Höchstanteil | Obergrenze eines Wertes am Depotvermögen |
| Hysterese | Vorsprung, den ein Anwärter zum Verdrängen braucht |
| Veto | Nemotron darf ablehnen |

> Zuerst standen diese Felder als frei fliessende Zeilen da. Weil die
> Beschreibungstexte verschieden lang sind, fluchtete **keine** Spalte mit der
> darunter — dasselbe Feld stand in drei Zeilen an drei Stellen. Eine Tabelle
> richtet das aus, ohne dass jemand Breiten von Hand pflegen muss.

**Was für eine Strategie keine Bedeutung hat, steht als „–" da** und wird auch
nicht mitgeschickt. Die Grundlinie kauft einmal und rührt sich nie wieder —
Umschichtungstakt, Hysterese und Veto haben für sie keinen Sinn. Ein
Bedienelement, das nichts bewirkt, gehört abgeschaltet und nicht bloss
unbeachtet gelassen; wer sonst daran dreht, wartet vergeblich auf eine Wirkung.

### Nur eine Strategie zählt zum Vermögen

Das Band ganz oben summierte anfangs **alle vier Depots** und meldete
3.422,79 EUR, wo 2.000 USD richtig waren: einmal das manuelle Depot, einmal der
Autopilot.

**`streng`, `aktiv` und `halten` sind keine drei Geldtöpfe.** Sie sind drei
Antworten auf dieselbe Frage — was aus denselben 1.000 geworden wäre, wenn man
so oder so vorgegangen wäre. Sie zu addieren zählt dasselbe Geld dreimal;
dieselbe Art Fehler wie die stündlich wiederholten Prognosen auf denselben
Zielbar: formal eine Summe, inhaltlich eine Scheinmenge.

> **Warum nicht das Budget dritteln.** Naheliegend wäre, 1.000 auf drei
> Strategien à 333 aufzuteilen — die Summe stimmte dann auch. Es wäre trotzdem
> falsch: Die drei blieben Gegenrechnungen, nur kleinere, und man addierte
> weiterhin dasselbe Geld dreimal. Ausserdem verlöre der Vergleich an
> Aussagekraft, weil sechs Positionen aus 333 näher an die Rundung geraten.

Jede Strategie behält also ihr volles Budget, damit sie unter gleichen
Bedingungen antritt. Genau **eine** trägt `zaehlt = 1` und geht ins
Gesamtvermögen ein — voreingestellt `aktiv`, weil sie die Strategie ist, die
das System tatsächlich fahren würde. `streng` handelt nach heutiger Datenlage
nie und stünde für ein Depot, das nichts tut; `halten` misst die anderen,
statt selbst das Ergebnis zu sein.

Die anderen beiden **verschwinden nicht**: Sie stehen weiter im Band, abgeblendet
und als *(Vergleich)* gekennzeichnet, und unverändert in der Vergleichstabelle.
Nur in der Summe sind sie nicht.

Der Auswahlknopf wirkt **sofort**, nicht erst auf „Übernehmen" — ein
Auswahlknopf, der eine Auswahl zeigt, die noch nicht gilt, ist eine
Falschanzeige, und diese hier entscheidet, welche Zahl ganz oben steht. Die
Umschaltung selbst liegt in `set_leitstrategie`: Zwischen „alte löschen" und
„neue setzen" darf es keinen Zustand geben, in dem gar keine oder zwei zählen.

**Das Band rechnet in USD**, weil dort alles läuft. Eine EUR-Vorgabe zeigte für
ein reines USD-Depot eine umgerechnete Zahl, wo eine ungerechnete möglich ist —
und jede Umrechnung ist eine Annahme mehr.

---

### Budget und Zurücksetzen

Jede Strategie hat ihr **eigenes Budget**, voreingestellt auf **1.000 USD**.

**Warum in den Einstellungen und nicht auf dem Konto.** Der Kontostand ist ein
Ist-Wert — die Summe aller Bewegungen, die sich mit jedem Kauf ändert. Das
Budget ist ein Soll-Wert: der Betrag, auf den ein Zurücksetzen zurückführt.
Beides in dieselbe Spalte zu legen hiesse, nach dem ersten Geschäft nicht mehr
sagen zu können, womit die Strategie angetreten ist — und genau das ist die
Zahl, gegen die ihr Ergebnis zu halten ist.

**Warum USD.** Von 594 verfolgten Werten notieren 331 in USD und 145 in EUR, die
gesamte Kryptoseite ohnehin. Ein Depot in EUR könnte einen Grossteil des
Bestands nur über eine Umrechnung halten, die dieses System für Einzelpositionen
bewusst nicht vornimmt.

**Zurücksetzen** — je Strategie oder für alle drei zusammen — löscht Buchungen,
Kassenbewegungen, Läufe und Beschlüsse und legt das Budget als Einzahlung aufs
Konto. Drei Feinheiten:

- **Alle Währungen**, nicht nur die eingestellte. Bliebe ein alter EUR-Stand
  neben einem frischen USD-Budget stehen, zeigte das Gesamtvermögen Geld, das zu
  keiner Strategie mehr gehört.
- **Die Läufe fliegen mit.** Sie belegten sonst Geschäfte, deren Buchungen es
  nicht mehr gibt.
- **Alles in einer Transaktion**, in `reset_autopilot_depot`, und in der von den
  Fremdschlüsseln vorgegebenen Reihenfolge: Beschlüsse vor Läufen,
  Kassenbewegungen vor Buchungen. Ein halb aufgeräumtes Depot wäre schlimmer
  als ein volles.

*Alle zurücksetzen* steht neben der Rangfolge, weil ein Vergleich nur dann eine
Aussage ist, wenn alle vom selben Punkt starten.

> **Kein Bestätigungsdialog.** Das Depot ist virtuell, es geht kein echtes Geld
> verloren — und ein Rückfragefenster für eine folgenlose Handlung erzieht dazu,
> Bestätigungen wegzuklicken, auch die, die zählen. Was verschwindet, steht im
> Titel des Knopfes und danach in der Statuszeile.

Ein geändertes Budget wirkt **erst beim nächsten Zurücksetzen** — es beschreibt
den Start, nicht den laufenden Stand. Die Statuszeile sagt das nach jedem
Übernehmen dazu.

---

## Was er hält — eigenes Raster je Strategie

Unter dem Vergleich steht je Strategie eine **eigene Tabelle** mit den Werten,
die das System selbst ausgesucht hat: Kurs, Nulllinie, Einsatz, Stand heute,
Gewinn, Gebühr, seit wann — und je Zeile das Journal seiner Buchungen.

**Warum getrennt vom manuellen Depot und nicht eine Tabelle mit einer Spalte
„Depot".** Die beiden Listen beantworten verschiedene Fragen. Oben steht, was
der Nutzer eingetragen hat; dort gehört ein Eingabefeld hin. Unten steht, was
das System ausgesucht hat — dort wäre ein Eingabefeld falsch, weil der nächste
Lauf jede Eingabe wieder überschreibt. Zusammengelegt sähe man Zeilen, von
denen die einen bearbeitbar wären und die anderen nicht, ohne dass die Tabelle
sagt, warum.

Die Zeilen sind deshalb **nur lesbar**, bis auf das Journal.

Ein Depot ohne Position bekommt keine leere Tabelle, sondern einen Satz. Bei
`streng` lautet er: *„Hält nichts. Dieses Depot handelt nur mit nachgewiesenem
Vorsprung — dass es leer bleibt, ist derzeit das Ergebnis und kein Fehler."*

> **Der Gewinn einer Position enthält die Gebühr nicht.** Sie hat das *Konto*
> verlassen, nicht die Position: Am Kauftag steht je Zeile +0,00, während der
> Kopf der Strategie −149,76 zeigt. Beides ist richtig, und die Trennung ist
> der Grund, warum das Vermögen oben aus Konto **plus** Kurswert besteht.

---

## Das Protokoll

Je Lauf eine Zeile in `autopilot_lauf`, je geprüftem Wert eine in
`autopilot_entscheidung` — **auch für die abgelehnten**, mit der Zahl, an der es
scheiterte:

> `NOC — nur 5 bewertete Live-Prognosen, nötig 20 — ohne Nachweis wird hier
> nicht gehandelt`

Ein Autopilot, der nur seine Geschäfte protokolliert, lässt sich nicht prüfen:
Man sähe, was er getan hat, und nie, was er erwogen und verworfen hat.

> **Gehaltene Werte stehen als `halten` im Protokoll, nicht als `abgelehnt`.**
> Die erste Fassung schrieb für jeden Wert, der im Zielkorb blieb, „nicht unter
> den besten N" — auch für **MNST mit der höchsten Erwartung des ganzen Laufs
> (7,226 %)**, das in Wahrheit gehalten wurde. Ein Protokoll, das den
> bestbewerteten Wert als verworfen meldet, ist schlimmer als keines: Es
> widerlegt sich selbst, und wer es liest, traut auch dem Rest nicht mehr.

Daneben hat **jede Strategie einen Knopf `Journal`**, der alle ihre Vorgänge in
einem Fenster zeigt — mit dem Kontostand nach jedem Schritt, so dass sich der
heutige Stand von der ersten Einzahlung an nachrechnen lässt. Beschreibung in
[INVESTINGS.md](INVESTINGS.md#das-journal).

---

## Entwicklung je Depot

Unter dem Gesamtvermögensband steht **eine Kurve je Depot** — Mein Depot,
Aktiv, Streng, Grundlinie nebeneinander statt summiert. Genau deshalb dürfen
die Vergleichsläufe hier mit: Was in einer Summe eine Doppelzählung wäre, ist
als eigene Linie die eigentliche Aussage.

Zwei Ansichten, weil es zwei Fragen sind:

| | beantwortet |
| --- | --- |
| **indexiert** (Start = 100) | welche Entscheidung war die bessere |
| **Beträge** | wieviel habe ich wo |

In Beträgen sieht ein Depot mit 20.000 immer besser aus als eines mit 1.000,
auch wenn es schlechter läuft. Solange alle gleich gross sind, führt das nicht
in die Irre — verlassen darf man sich darauf nicht.

Die zählende Strategie bekommt eine **kräftige** Linie, die Vergleichsläufe
eine dünne gestrichelte. Vor dem ersten Tag eines Depots steht `null` und nicht
null-Komma-null: Eine Linie, die bei 0 beginnt und dann springt, behauptet
einen Verlust, den es nie gab.

---

## Wann er läuft

Im **Tageslauf um 02:20 UTC** (`DailyCronUtc = "20 2 * * *"`), nach der
Neuprognose.

> **Gehandelt wird täglich — bewertet laufend.** Das sind zwei verschiedene
> Dinge, und ich hatte sie zusammengeworfen. Positionen werden auf dem
> **jüngsten bekannten Kurs** bewertet, und das ist meist eine Stundenbar: Der
> Depotwert bewegt sich also mit jedem Stundenlauf, auch wenn der Autopilot erst
> am nächsten Morgen wieder bucht.
>
> Gemessen am 26.08.2026 um 14:45 UTC, bevor das behoben war: Tagesbars bis
> 25.08. 00:00, Stundenbars bis 26.08. 14:00 — der angezeigte Depotwert war
> **38 Stunden alt** und stand zwischen zwei Tagesläufen still.

 Vorher gerechnet handelte er nach den
Zahlen von gestern. Nicht im Stundenlauf: Bei 0,33 % Stundenbewegung wären
95,4 % Trefferquote nötig, damit sich ein stündliches Geschäft trägt.

> **„Täglich" heisst: sobald der Rechner das nächste Mal läuft.** Schläft die
> Maschine um 02:20 UTC, findet der Lauf nicht statt — der Zeitplan holt ihn
> beim Aufwachen nach. Gemessen am 27.08.2026: Rechner wach um 07:52 Ortszeit,
> Prognosen fertig 08:00:47, Autopilot 08:00:49 mit vier Geschäften. Der
> reguläre Termin um 04:20 Ortszeit war verschlafen worden.
>
> **Und: Die Datenbank speichert UTC, das Protokoll schreibt Ortszeit.** Die
> vier Geschäfte stehen als `06:00:51` in `invest_buchung` und als `08:00:51`
> im Protokoll — dieselbe Sekunde. Wer das verwechselt, sucht einen Lauf zu
> einer Zeit, zu der der Rechner nachweislich aus war.

Eigenes `try`/`catch` — ein Fehler hier darf den Tageslauf nicht nachträglich
als gescheitert erscheinen lassen, wenn Kurse, Analyse, Bewertung und Prognose
längst erledigt sind. Ebenso läuft jede Strategie in ihrem eigenen `catch`:
Ein Fehler in `aktiv` darf die Grundlinie nicht mitreissen, sonst ist der
Vergleich für diesen Tag verloren.

---

## Schnittstelle

| | |
| --- | --- |
| `GET /api/autopilot/` | Einstellungen, letzte Läufe, Vergleich |
| `GET /api/autopilot/rangfolge` | die Bewertung **ohne jeden Handel** |
| `POST /api/autopilot/einstellung` | Ein/Aus, Zahl der Werte, Takt, Veto |
| `POST /api/autopilot/lauf` | ohne `depot` alle eingeschalteten; `erzwingen=true` übergeht den Takt |
| `GET /api/autopilot/beschluesse/{laufId}` | alle Beschlüsse eines Laufs |
| `GET /api/autopilot/vergleich` | die Strategien gegen die Grundlinie |

`rangfolge` ist absichtlich ein GET ohne Nebenwirkung: Wer eine Handelsstrategie
nur im Nachhinein an ihren Buchungen prüfen kann, prüft sie nicht.

Tabellen: `autopilot_lauf`, `autopilot_entscheidung`, `autopilot_einstellung`,
Migration `038_autopilot.sql`.
