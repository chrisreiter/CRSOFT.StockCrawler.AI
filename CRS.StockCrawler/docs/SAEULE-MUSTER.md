# Säule „Muster" — Umsetzungsplan

**CRSOFT.StockCrawler**, Stand 21.08.2026

## Aufgabe

Signifikante Kursänderungen in der Historie finden und durch vorhergehende
Auffälligkeiten erklären. Aus den belastbaren Erklärungen werden Suchmuster,
die bei laufenden Prognosen anschlagen. Die Muster stehen in einem Raster und
lassen sich einzeln aktivieren.

## Was schon steht und wiederverwendet wird

| Baustein | Was er liefert | Wo |
| --- | --- | --- |
| `EventDetector` | Auffälligkeiten je Wert, Stufe 1–100, ohne Vorausschau, mit Verdachtsflag für Datenfehler | `Ingest.Core/Analysis/EventDetector.cs` |
| `FlowCoincidence.FindAnomalies` | Auffällige Kapitalbewegung: Zufluss, Abfluss, Umschichtung | `Ingest.Core/Analysis/FlowCoincidence.cs` |
| `FlowAttribution` | Zuordnung Verlierer → Gewinner bei gleichbleibender Summe | `Ingest.Core/Analysis/FlowAttribution.cs` |
| `NetFlow.RotationScore` | Rotationsverdacht zwischen zwei Werten | `Ingest.Core/Analysis/NetFlow.cs` |
| `Ensemble` | Hedge-Gewichtung mit Richtungsstrafe, nimmt beliebige `IForecastModel` auf | `Ingest.Core/Analysis/Ensemble.cs` |

Die Säule erfindet also keine neue Erkennung, sondern setzt auf dem auf, was
Ereignisse und Kapitalbewegungen bereits messen.

## Was ein Muster ist

Eine Regel in genau einer Form:

> **WENN** ⟨1–3 Vorbedingungen gleichzeitig⟩ **DANN** bewegt sich ⟨Ziel⟩
> innerhalb von ⟨h⟩ Bars in ⟨Richtung⟩.

Die Vorbedingungen stammen aus einem **endlichen, festgelegten Vokabular** —
nicht aus freier Suche. Das ist Absicht: ein geschlossenes Vokabular lässt sich
abzählen, und nur was sich abzählen lässt, lässt sich gegen den Zufall prüfen.

**Vokabular der Vorbedingungen**

| Prädikat | Bedeutung |
| --- | --- |
| `Ereignis(Wert \| Klasse \| Branche, Richtung, ab Stufe s)` | dort war etwas los |
| `Kapital(Zufluss \| Abfluss \| Umschichtung, ab Stufe s)` | am Geld im System hat sich etwas getan |
| `Anteilssprung(Wert, Richtung, ab z)` | ein Wert hat auffällig Marktanteil gewonnen oder verloren |
| `Regime(Volatilität hoch \| niedrig)` | Zustand des Zielwertes vor dem Ereignis |
| `Rotationspartner(Wert, Richtung)` | der bekannte Gegenspieler hat sich bewegt |
| `Handelsphase(Wochentag \| Wochenende)` | bewusst im Vokabular |

Die Handelsphase steht ausdrücklich mit drin. Der Kalender hat sich in dieser
Auswertung schon dreimal als Scheinsignal getarnt — samstags handelt nur
Krypto, und jede Rechnung, die das nicht berücksichtigt, findet zuverlässig
Wochenenden statt Ereignisse. Ist der Kalender ein eigenes Prädikat, taucht er
als *das* auf, was er ist, statt sich in ein anderes Muster einzuschleichen.

**Ziel**: ein einzelner Wert, eine Anlageklasse oder eine Branche, mit
Richtung und Horizont h ∈ {1, 3, 7, 30 Bars}.

## Der schwierige Teil: Scheinmuster

Bei 276 Werten, zwei Richtungen, vier Horizonten und dem obigen Vokabular
entstehen Millionen von Kandidaten. Prüft man sie alle auf dem üblichen
Signifikanzniveau, sind allein durch Zufall **zehntausende** davon
„signifikant". Wer das ignoriert, baut sich eine Säule aus Rauschen und merkt
es erst, wenn die Prognose live schlechter wird.

Vier Gegenmaßnahmen, alle vier nötig:

1. **Mindest-Support.** Unter 20 Vorkommen wird nichts weiterverfolgt. Drei
   Treffer aus vier Gelegenheiten sind keine Regel, sondern eine Anekdote.

2. **Zeitliche Dreiteilung.** Muster werden nur auf dem *Entdeckungszeitraum*
   gesucht (2001–2018), auf dem *Bestätigungszeitraum* geprüft (2019–2022) und
   erst ganz am Ende einmalig gegen den *Sperrzeitraum* (2023–heute) gehalten.
   Der Sperrzeitraum wird bis dahin nicht angefasst — auch nicht „nur mal
   kurz zum Schauen". Genau dieses Schauen ist der Weg, auf dem sich Wissen
   über die Zukunft in die Suche einschleicht.

3. **Permutationstest.** Die Zielereignisse werden zeitlich zufällig
   verschoben und die identische Suche wiederholt, mehrere hundert Mal. Das
   ergibt die Verteilung der Faktoren, die reiner Zufall hergibt. Ein Muster
   muss über deren 99. Perzentil liegen. Dieser Test ist der wichtigste der
   vier, weil er keine Verteilungsannahme braucht — er misst den Zufall an
   genau diesen Daten, statt ihn zu unterstellen.

4. **False Discovery Rate nach Benjamini-Hochberg**, nicht Bonferroni.
   Bonferroni wäre bei dieser Kandidatenzahl so streng, dass auch echte Muster
   durchfallen. Benjamini-Hochberg begrenzt stattdessen den *Anteil* der
   Fehltreffer unter den akzeptierten — bei dieser Aufgabe die passendere
   Frage.

**Ehrliche Erwartung vorweg:** Nach diesen vier Filtern wird wenig übrig
bleiben. Die Kopplungsanalyse von heute ist ein Vorgeschmack — über zehn Jahre
und 160 Kapitalauffälligkeiten kamen vier Werte mit einem Faktor zwischen 1,3
und 1,9 heraus, und keiner davon lief zuverlässig voraus. Sollte die Säule am
Ende zwanzig Muster liefern statt zweitausend, ist das kein Misserfolg,
sondern der Zweck der Übung: zu wissen, was trägt, und nicht zu glauben, was
gefällt.

## Datenhaltung

Neue Migration `infra/sql/016_patterns.sql` — legt nur Tabellen an, ergänzt
keine Spalten an bestehenden, sortiert deshalb unproblematisch hinter `012`.

```
pattern          pattern_id, name, antecedent_json, consequent_json,
                 horizon_bars, support, confidence, lift, p_value, q_value,
                 discovered_range, validated_range, status, created_utc
pattern_match    pattern_id, ts_utc, asset_id, fired, outcome
pattern_setting  user_id, pattern_id, enabled, weight
```

`status` ∈ {entdeckt, bestätigt, verworfen, gesperrt}. `pattern_match` hält die
Trefferhistorie — daraus speisen sich sowohl die Chart-Marker als auch die
laufende Nachkontrolle. `pattern_setting` hängt an der ohnehin geplanten
Sitzungs- und Benutzerverwaltung.

## Einbau in die Prognose

`PatternModel : IForecastModel` als sechstes Teilmodell neben `naive`, `drift`,
`momentum`, `meanrev` und `leadlag`. Bei jedem Prognoseschritt: Welche
aktivierten Muster feuern gerade? Der erwartete Log-Return ist das mit
Konfidenz und Faktor gewichtete Mittel der historischen Zielbewegungen.

Feuert kein Muster, liefert das Modell null und trägt nichts bei. Das ist die
wichtigste Eigenschaft des Entwurfs: **die Säule kann die Prognose nicht
verschlechtern, nur schweigen.** Trägt sie dauerhaft nichts bei, drückt die
Hedge-Gewichtung ihr Gewicht von selbst gegen null — ohne dass jemand
eingreifen muss.

Nachweis am Ende gegen die bekannte Grundlinie: saubere Walk-Forward-Richtung
liegt bei **52,3 %**. Alles, was darunter oder gleichauf landet, war die Mühe
nicht wert und wird auch so berichtet.

## Oberfläche

Neuer Reiter „Muster" in den Säulen-Einstellungen, mit derselben Kopfzeile wie
die anderen Säulen: Häkchen „in die Prognose einbeziehen" und Gewicht 0–100.

Darunter das Raster:

| ✓ | Muster | Horizont | Vorkommen | Trefferquote | Faktor | q | Status | letzter Treffer |
|---|---|---|---|---|---|---|---|---|

Muster werden als deutscher Satz gezeigt, nicht als JSON:

> „Wenn NVDA um mehr als drei Sigma fällt und gleichzeitig ein Kapitalabfluss
> ab Stufe 60 auftritt, steigt XAUT-USD innerhalb von drei Tagen."
> 34 Vorkommen · Trefferquote 68 % · Faktor 2,1 · q = 0,03

Dazu je Zeile „im Chart zeigen": setzt die historischen Treffer als Marker in
die Kurscharts, damit man sie mit eigenen Augen prüfen kann, statt einer
Kennzahl glauben zu müssen.

## Reihenfolge

Vier Schritte, jeder für sich lauffähig und bewertbar:

1. **`PatternMiner` in Core + `/api/patterns/mine`** — sucht, speichert nichts.
   Nach diesem Schritt weiß man, ob überhaupt etwas Belastbares in den Daten
   steckt. Das ist bewusst der erste Schritt: Es wäre unsinnig, Persistenz,
   Modell und Oberfläche für etwas zu bauen, das es womöglich nicht gibt.
2. **Persistenz und Prüfung** — Migration, Permutationstest, FDR, Statuspflege.
3. **`PatternModel` ins Ensemble** — mit Walk-Forward-Nachweis gegen 52,3 %.
4. **Oberfläche** — Raster, Aktivierung, Chart-Marker.

Nach Schritt 1 lohnt eine Zwischenentscheidung: Fällt die Ausbeute sehr mager
aus, ist die richtige Antwort womöglich, das Vokabular zu erweitern, statt
Schritte 2 bis 4 auf dünner Grundlage zu bauen.
