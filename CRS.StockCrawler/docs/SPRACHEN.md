# Sprachen — die Oberflächentexte als Ressourcen

Umschalter oben rechts. Deutsch und Englisch liegen bei; eine weitere Sprache
ist eine Datei und sonst nichts.

---

## Der Ausgangspunkt, und warum er die Bauweise bestimmt

Diese Anwendung wurde mit fest eingebauten deutschen Texten geschrieben — an
**1.711 Stellen** in `index.html` und `app.js`. Das hätte von Anfang an anders
gehört, und der Betreiber hat es so gesagt: „eine Anwendung kann keine
statischen Texte beinhalten“.

Nachträglich stehen zwei Wege offen:

| | |
| --- | --- |
| **Schlüssel vergeben** (`kurse.titel`) | in einer neu gebauten Anwendung richtig — hier hiesse es, 1.711 Stellen anzufassen, und jede ist eine Gelegenheit, die Anwendung zu zerlegen |
| **Der deutsche Text IST der Schlüssel** | `index.html` und `app.js` bleiben unberührt; der Preis steht unten und wird nicht beschönigt |

Gewählt ist der zweite. Damit ist `loc.res.de.xml` die **Identität** — Schlüssel
und Wert sind derselbe Text — und ebendadurch zugleich der **Katalog** dessen,
was überhaupt übersetzbar ist.

---

## Die Dateien

`infra/loc/loc.res.<kürzel>.xml`

```xml
<sprache name="English">
  <t k="Kurse">Prices</t>
  <t k="Anmelden" offen="1">Sign in</t>
</sprache>
```

| | |
| --- | --- |
| `name` | wie die Sprache **in sich selbst** heisst — „Italiano“, nicht „Italienisch“. Eine Liste, die die Sprachen in einer fremden Sprache benennt, ist für den unbrauchbar, der sie braucht |
| `k` | der deutsche Urtext. Wird er geändert, verliert der Eintrag seinen Bezug |
| `offen="1"` | darf schon **vor** der Anmeldung ausgeliefert werden |

**Das Kürzel kommt aus dem Dateinamen, nicht aus einem Attribut.** Sonst können
beide auseinanderlaufen: `loc.res.it.xml` mit `code="en"` überschriebe Englisch,
und man suchte den Fehler in der englischen Übersetzung.

**Ein leerer Wert ist eine Lücke, keine leere Übersetzung.** Wer ihn übernähme,
löschte den Text in der Oberfläche — aus einem noch nicht übersetzten Wort würde
eine leere Stelle, und das sieht nach einem Fehler der Anwendung aus.

### Eine Sprache hinzufügen

`loc.res.de.xml` kopieren, Kürzel im Dateinamen ändern, `name` setzen, Werte
übersetzen, **Schlüssel unangetastet lassen**. Beim nächsten Start steht sie in
der Auswahl. Es gibt keine Liste im Quelltext, in die man sie zusätzlich
eintragen müsste — eine solche Liste wäre die Stelle, an der eine mitgelieferte
Sprache stillschweigend nicht erscheint.

Zum Übersetzen ohne Neustart: `POST /api/loc/auffrischen`.

`loc.res.it.xml` liegt als absichtlich **unvollständige** Probe bei. Sie belegt
beides: dass eine neue Datei ohne jede Änderung erkannt wird, und dass eine
Lücke als Lücke gemeldet wird. In der Auswahl steht deshalb „Italiano (1189)“ —
die Zahl ist, was noch fehlt.

---

## Wie es arbeitet

Nicht über einen Bauplan, sondern über das **fertige DOM**: Nach jeder Änderung
werden Textknoten und die Attribute `title`, `placeholder` und `aria-label`
durchgegangen und ersetzt. Damit ist gleichgültig, ob ein Text aus `index.html`
stammt oder aus einer Tabelle, die `app.js` gerade zusammengesetzt hat.

**Ein Beobachter statt Aufrufen an jeder Stelle, die etwas zeichnet.** `app.js`
schreibt an über hundert Stellen `innerHTML`. Jede um einen Aufruf zu ergänzen
hiesse wieder, hundert Stellen anzufassen — und die eine vergessene fiele erst
auf, wenn jemand genau diese Ansicht auf Englisch öffnet.

**Der deutsche Urtext wird je Knoten gemerkt.** Ohne ihn wäre der Wechsel
Englisch → Italienisch nicht möglich: Man müsste aus dem Englischen
zurückübersetzen, und zwei deutsche Texte können dieselbe englische Entsprechung
haben. Steht in einem Knoten etwas anderes, als die Schicht zuletzt geschrieben
hat, hat `app.js` ihn neu befüllt — dann ist das Neue der Urtext. Ohne diese
Prüfung übersetzte die Anwendung nach dem ersten Neuaufbau einer Tabelle
dauerhaft den Text von vorgestern.

### Zusammengesetzte Sätze — und die zwei Riegel daran

`app.js` baut Sätze aus Bruchstücken: `n + ' Positionen, zusammen ' + x`. Im DOM
steht davon **ein** Knoten — „3 Positionen, zusammen 614,93.“ —, und den gibt es
im Katalog nicht. Deshalb ein zweiter Anlauf, der bekannte Bruchstücke im Text
ersetzt. Nur als Rückfall, nie zuerst.

Der erste Entwurf nahm dafür **alle** Schlüssel, und im Browser stand daraufhin
**„Pricebewegung“ und „Hourbar“**: „Kurs“ und „Stunde“ sind eigene Einträge und
wurden mitten im zusammengesetzten Wort ersetzt. Zwei Riegel:

1. **Nur mehrwortige Schlüssel.** Ein einzelnes Wort, das allein im DOM steht,
   findet ohnehin den genauen Treffer — als Bruchstück wird es nicht gebraucht
   und richtet nur Schaden an.
2. **Wortgrenzen.** Deutsche Zusammensetzungen haben zwischen „Kurs“ und
   „bewegung“ keine Lücke. `\b` genügt nicht, weil JavaScript Umlaute nicht als
   Wortzeichen führt — die Bedingung muss ausdrücklich lauten: links und rechts
   kein Buchstabe.

Längste zuerst: Stünden „im Sperrbereich“ und „im Sperrbereich ab“ beide im
Katalog, gewänne sonst das kürzere und liesse „ab“ stehen.

---

## Die Anmeldeseite

Eine Anmeldeseite, die nur auf Deutsch erscheint, wäre der eine Ort, an dem die
Sprachwahl nichts nützt: Man bekommt sie erst zu Gesicht, nachdem man sich
angemeldet hat.

`/api/loc/` liegt hinter der Anmeldung — die Sprachdateien enthalten sämtliche
Erklärtexte der Anwendung samt ihrer Messwerte, und CLAUDE.md hält fest, dass
„kein Kurs“ noch keine Harmlosigkeit ist. `/loc-offen/` liefert dagegen **nur**,
was die Sprachdatei selbst mit `offen="1"` kennzeichnet: derzeit fünf Zeilen.
Welche das sind, entscheidet damit die Datei und nicht eine Liste im Quelltext,
die beim nächsten neuen Text jemand zu pflegen vergisst.

---

## Die Antworten des Reasoning-Agenten

Der Agent antwortet in der Sprache der **Frage**; ist sie zu kurz für ein
Urteil, in der eingestellten Sprache.

**Die Sprache wird auf dem Server bestimmt, nicht vom Modell.** Der erste
Entwurf überliess es dem Modell: „ANTWORTSPRACHE: English. Ist die Frage
erkennbar in einer anderen Sprache gestellt, antwortest du in DIESER Sprache."
Gemessen an `qwen3-vl:4b` mit einer deutschen Frage kam die Antwort auf
**Englisch**. Ein Modell wägt zwei widersprüchliche Anweisungen nicht ab, es
folgt der deutlicheren.

Wird die Sprache dagegen vorher bestimmt, bekommt das Modell genau **eine**
Anweisung — und die befolgt es nachweislich in beide Richtungen: Einstellung
Englisch mit deutscher Frage ergab Englisch, Einstellung Deutsch mit englischer
Frage ergab Deutsch.

`LocService.Erkenne` zählt dafür, wie viele Wörter der Frage im Wortschatz jeder
vorhandenen Sprache vorkommen. **Der Wortschatz stammt aus den Sprachdateien
selbst** — die Werte von `loc.res.it.xml` sind italienisch. Eine eingebaute
Wortliste je Sprache wäre die zweite Stelle, die man beim Hinzufügen einer
Sprache pflegen müsste, und genau die vergisst man.

Zwei Riegel gegen falsche Sicherheit:

- **Unter fünf Wörtern wird nicht geraten.** „NVDA?" ist keine Sprache, und eine
  falsche Vermutung ist hier schlechter als keine — sie überstimmt eine
  ausdrückliche Einstellung.
- **Ein klarer Vorsprung, nicht bloss ein Vorsprung.** Deutsch und Englisch
  teilen sich kurze Wörter; verlangt wird mindestens das Doppelte der
  zweitbesten Sprache.

Nachsehen lässt sich das unter `GET /api/loc/erkenne?text=…`. Gemessen an sechs
Proben: deutsche Fragen `de`, englische `en`, „NVDA?" nichts.

Die **Regeln** des Systemprompts bleiben deutsch und unverändert — sie sind
Anweisung an das Modell, nicht Teil der Antwort. Insbesondere die Pflicht, bei
einem Fehlerverhältnis ab 1 zu sagen, dass die Zahl keine Handelsgrundlage ist,
gilt in jeder Sprache. Die Werkzeugspur unter der Antwort bleibt roh: Sie ist
die Stelle, an der sich eine übersetzte Antwort nachprüfen lässt.

---

## Die serverseitig erzeugten Texte — doch mit erfasst

Hier stand, dieser Teil sei nicht erfasst und der Umbau der Vorlagen auf
Platzhalter-Aufrufe sei dafür nötig. **Beides war falsch.**

Diese Texte kommen als Daten in den Browser und landen dort im DOM — die
Übersetzungsschicht sieht sie also längst. Es fehlten nur die Einträge im
Katalog. Geerntet werden die **Stücke zwischen den Interpolationslöchern**: Aus
`$"Gerechnet in {ziel}, umgerechnet über EURUSD=X mit dem Kurs des jeweiligen "`
wird `, umgerechnet über EURUSD=X mit dem Kurs des jeweiligen`. Genau dieses
Stück steht später wörtlich im DOM. **1.070 Texte, davon 1.050 übersetzt**;
kein einziger C#-Aufruf musste dafür geändert werden.

Ausgenommen bleiben zwei Mengen, und zwar mit Grund:

| | |
| --- | --- |
| **`ReasoningService.cs`** | Systemprompt und Werkzeugbeschreibungen richten sich an das Modell, nicht an den Leser. Sie zu übersetzen änderte das Verhalten. Die Antwort selbst wird längst sprachlich gesteuert. |
| **Das Stimmungs-Wörterbuch** | „aufwärtstrend“, „übertrifft“, „enttäuscht“ sind die Messgrössen der Nachrichtenstimmung. Übersetzt wäre die Säule kaputt. |
| **Protokollzeilen** | Alles auf einer Zeile mit `_log.` geht auf die Konsole, nicht in die Oberfläche. |

**Journal und Tagesübersicht sind damit mit übersetzt** — und zwar ohne das
Sprachmodell einzuschalten. Die Vorlagen bleiben Vorlagen, jede Zahl stammt
weiter aus einer Abfrage. Der schnelle Weg, das fertige Journal durch Nemotron
übersetzen zu lassen, hätte genau das Risiko zurückgeholt, wegen dessen die
Vorlagen existieren.

---

## Drei Fehler, die dabei zu beheben waren

**Ein C#-Leser über `"…"` schneidet an der falschen Stelle, sobald ein
Anführungszeichen im Interpolationsloch steht.** Bei
`$"… {string.Join(", ", draussen)}. Die Strategien des …"` endete die
Zeichenkette mitten im `Join`, und aus dem Rest entstanden Schlüssel, die es so
nie gab. Im Browser stand daraufhin ein deutsch-englischer Mischsatz — „Nicht in
the Summe“. Der Leser zählt jetzt die Klammertiefe mit.

**Die Wortgrenze gilt je Schlüssel, nicht für die ganze Alternation.** Der erste
Entwurf klammerte sie einmal aussen herum. Damit fand **kein einziger**
Schlüssel, der mit einem Satzzeichen beginnt, je seinen Text: „, umgerechnet
über …“ steht im DOM hinter dem „D“ von USD, also wurde dort eine Wortgrenze
verlangt, wo es keine geben *kann*. Betroffen war der ganze serverseitige Teil,
denn dessen Bruchstücke stehen naturgemäss zwischen zwei Werten.

**Die Längenbremse schnitt die längsten Absätze ab.** Sie stand bei 400 Zeichen
— und die serverseitigen Hinweisabsätze sind mit 400 bis 900 Zeichen die
längsten der Anwendung. Sichtbar wurde es daran, dass „Kurs des jeweiligen
Tages“ übersetzt war und „Gerechnet in“ im selben Absatz nicht.

---

## Was diese Lösung nicht kann

**Ein geänderter deutscher Text verliert seine Übersetzung — still.** Wer in
`app.js` ein Wort umformuliert, dessen Eintrag findet keinen Schlüssel mehr, und
die Stelle bleibt in jeder Sprache deutsch. Es gibt keine Fehlermeldung, weil es
kein Fehler ist. Gegenmittel ist die Lückenzahl aus `/api/loc/`, die je Sprache
sagt, wieviele Katalogeinträge ihr fehlen — sie springt nach oben, sobald ein
Text auseinandergelaufen ist.

**Zahlen und Datumsangaben bleiben deutsch formatiert** („2.004,40“). Das ist
Absicht und keine Nachlässigkeit: Das Dezimalkomma steckt nicht nur in der
Anzeige, sondern auch im Lesen der Eingabefelder — `fmtNum` taugt schon heute
nicht als Wert für `<input type="number">`. Die Formatierung mitzuschalten wäre
ein deutlich grösserer und riskanterer Eingriff als die Texte, und ein halb
umgestelltes Zahlenformat ist schlimmer als ein einheitliches.

**Beschriftungen aus einem Wort plus Zahl bleiben deutsch.** „Streng (Vgl.)“
oder „eingezahlt 1.000,00“ stehen im DOM als **ein** Knoten, für den es keinen
genauen Treffer gibt, und ihr übersetzbarer Teil ist ein einzelnes Wort. Auch
einzelne Wörter in die Bruchstück-Ersetzung aufzunehmen wurde ausprobiert und
verworfen: Gemessen an 1.859 Textknoten läuft ein Durchgang mit den
mehrwortigen Schlüsseln (~1.200 Zweige) in **24 ms**; mit allen Schlüsseln ab
fünf Zeichen (~2.270 Zweige) kam derselbe Durchgang **binnen 45 Sekunden nicht
zum Ende**. Eine sichtbare Lücke und eine schnelle Oberfläche ist die bessere
Hälfte der Wahl.

**In einem unsichtbaren Tab wird nachgezogen, nicht mitgezogen.** Die Schicht
sammelt Änderungen über `requestAnimationFrame`, und das feuert im
Hintergrund-Tab nicht. Neu gezeichnete Inhalte bleiben dort deutsch, bis der Tab
wieder sichtbar wird. Sichtbar wird davon nichts — es hat aber beim Messen
zweimal wie ein Fehler ausgesehen.

## Schnittstelle

| | |
| --- | --- |
| `GET /api/loc/` | gefundene Sprachen mit Anzahl und Lücken |
| `GET /api/loc/{code}` | eine Sprachdatei |
| `POST /api/loc/auffrischen` | Verzeichnis neu einlesen |
| `GET /loc-offen` · `GET /loc-offen/{code}` | ohne Anmeldung, nur `offen="1"` |

Dienst `LocService` (Singleton — gelesen wird beim Start, nicht je Anfrage),
Schicht `wwwroot/loc.js`, Verzeichnis über `Ablage.Sprachen`. Dort gewinnt
anders als bei Modellen und Quellenliste der **Entwicklungspfad**: An einer
Übersetzung wird während der Entwicklung dauernd gearbeitet, und eine ältere
Kopie in `bin/loc`, die den Ausschlag gäbe, wäre genau die Falle, wegen der die
Fassungsnummern aus dem Änderungszeitpunkt der Datei kommen.
