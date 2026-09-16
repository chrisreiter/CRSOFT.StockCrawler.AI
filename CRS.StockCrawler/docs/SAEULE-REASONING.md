# Säule „Reasoning"

**CRSOFT.StockCrawler**, Stand 16.09.2026

Ein Gesprächsagent auf allem, was das System weiß — Prognosen,
Kurvendiskussion, Verknüpfungen, Fachliteratur, Nachrichten.

## Wie diese Säule ein Gewicht bekommt — und warum sie lange keines hatte

Bis 16.09.2026 hatte sie keines, und der Grund stand hier: Sie erzeugt keine
Prognose, sie liest die vorhandenen ab; eine Säule, die dieselben Zahlen noch
einmal einbringt, gäbe ihnen doppeltes Gewicht. Dazu die Messung, dass das
Modell bei den ersten drei echten Fragen zweimal ohne Werkzeugaufruf, also
frei, geantwortet hatte.

Der Betreiber wollte Nemotrons Einschätzung und das Journal trotzdem in die
Prognose mischen können. Der Weg, der die Regel „keine Zahl ohne Messung"
nicht bricht, hat vier Teile:

1. **Das Modell liefert ein Urteil, keinen Betrag.** `UrteilAsync` fragt je
   Wert nach Richtung (−2…+2) und Zuversicht (0…1) als JSON — nachdem es
   `prognose`, `kurs` und nach Bedarf `nachrichten`, `grundschwingungen`,
   `kurvenereignisse` gerufen hat. Der Tageskontext (Journal-Lage, Punkte der
   Tagesübersicht zu diesem Wert) geht als Text mit.
2. **Kein Werkzeugaufruf, kein Urteil.** Antwortet das Modell frei, wird das
   Urteil verworfen und der Grund festgehalten. Das ist die Antwort auf die
   Messung von oben.
3. **Der Betrag kommt aus der Schwankung des Werts**, nicht aus dem Modell:
   Richtung/2 × Zuversicht × σ_Tag × √Horizont, als Aufschlag mit Deckel.
   Ein „+2 bei 100 %" ist eine Standardabweichung — bei einem Anleihen-ETF
   ein Zehntelprozent, bei einem Krypto-Wert fünf.
4. **Der Verdienst kommt aus der Nachprüfung.** `reasoning_urteil` hält
   Urteil, Werkzeuge, Dauer und den Basiskurs; fünf Handelstage später wird
   der Kurs geholt und das Vorzeichen geprüft. Ab zwanzig gerichteten Urteilen
   zählt die Trefferquote, davor 0,25 — derselbe vorsichtige Zwischenwert wie
   bei der ersten Säule, solange sie jung ist.

Der Lauf ist vom Prognoselauf entkoppelt (`ReasoningUrteilService`): zwölf
Urteile je Tag mit einer Stunde Budget, Werte ohne Urteil zuerst, gehaltene
Positionen vor allen anderen. `POST /api/reasoning/urteile/lauf?max=&minuten=`
stößt es von Hand an, `GET /api/reasoning/urteile` zeigt Stand und
Trefferquote, `POST …/bewerten` prüft Fälliges nach. Die Checkbox der Säule
ist frei; ihr Gewicht steht auf null, bis jemand es hebt.

**Was noch nicht gemessen ist:** die Trefferquote. Am 16.09.2026 gab es null
Urteile — Ollama war auf dem Entwicklungsrechner defekt (halbfertiges Update
ohne `llama-server.exe`). Bis zwanzig nachgeprüfte Urteile vorliegen, wirkt
die Säule mit Verdienst 0,25 × Regler; danach entscheidet die Messung, ob sie
mehr oder gar nichts bekommt.

## Werkzeuge statt Kontextblock

Der naheliegende Aufbau wäre, dem Modell zu jeder Frage alles mitzugeben, was
die Anwendung weiß. Das scheitert zweifach: an der Menge — allein die
Kurvendiskussion hat 98.000 Einträge — und an der Herkunft. In einem großen
Block ist nicht mehr zu erkennen, welche Zahl der Antwort zugrunde liegt und
welche nur danebenstand.

Stattdessen ruft das Modell Werkzeuge auf:

| Werkzeug | Was es liefert |
| --- | --- |
| `kurs` | letzter Schluss, Bewegung über 1, 5, 20 Tage |
| `prognose` | alle Bänder für einen Wert, samt Sperrbereichszahlen |
| `modellzustand` | welche Modelle geladen sind und was sie taugen |
| `kurvenereignisse` | auffällige Stellen eines Wertes, stärkste zuerst |
| `verknuepfungen` | Werte mit gleichzeitigen Ereignissen |
| `wissen` | Ähnlichkeitssuche in der Fachliteratur |
| `nachrichten` | Ähnlichkeitssuche in den gesammelten Meldungen |
| `werteliste` | was verfolgt wird |

Höchstens sechs Runden je Frage. Ein fehlgeschlagenes Werkzeug bricht nicht ab —
das Modell sieht die Fehlermeldung und kann es anders versuchen.

## Die Werkzeugspur gehört zur Antwort

Unter jeder Antwort stehen die Aufrufe: Name, Argumente, Ergebnis. Eingeklappt,
weil sie bei sechs Aufrufen länger wären als die Antwort — aber vorhanden, denn
ohne sie wäre nicht zu unterscheiden, ob eine Zahl **gemessen** oder **erzeugt**
wurde.

## Die eingebaute Skepsis

Die Anweisung an das Modell enthält die gemessenen Grenzen dieses Systems, und
zwar als Pflicht:

> Zu jeder Prognose nennst du, was sie im Sperrbereich wert war. Ist das
> Fehlerverhältnis 1 oder größer, MUSST du sagen, dass das Modell die Annahme
> „Kurs bleibt stehen" nicht schlägt und die Zahl daher keine Handelsgrundlage
> ist.

> Gleichzeitigkeit ist kein Vorlauf. Ein hoher Zusammenhang bei einem
> Zeitabstand um null erlaubt keine Vorhersage.

Das ist keine Höflichkeitsfloskel. **Ein Agent, der eine Prognose weitergibt,
ohne ihren gemessenen Wert zu nennen, wäre die gefährlichste Komponente dieses
Systems** — er klänge kompetent und wäre es nicht. Alles andere in diesem
Projekt ist darauf ausgelegt, sich nicht selbst zu betrügen; diese Säule darf
das nicht unterlaufen.

Dass es wirkt, zeigen die Antworten aus dem ersten Lauf, unverändert:

> *„Nur kurzfristiges Modell geladen, aber aufgrund schlechter Prognosequalität
> (Fehlerverhältnis > 1) nicht geeignet für Handelsentscheidungen."*

> *„Alle Paare haben einen Median-Abstand von ±0–1 Bar (nahezu gleichzeitig) […]
> sodass keine Prognose-Grundlage entsteht."*

## Ein Fehler, den erst der Agent sichtbar gemacht hat

Auf die Frage nach BTC-USD antwortete er zunächst: *„Keine Verknüpfung
gefunden."* Das war falsch — es gibt Dutzende. Die Ursache lag im Werkzeug: Es
holte die stärksten 300 Verknüpfungen und filterte **danach** nach Symbol. Bei
135.481 Einträgen ist ein einzelner Wert dort praktisch nie dabei.

Der Filter gehört in die Abfrage, und dazu ein Index je Spalte des ODER —
`(run_id, asset_a, lift DESC)` und `(run_id, asset_b, lift DESC)`. Vorher
Zeitüberlauf, nachher eine Sekunde.

Bemerkenswert daran ist weniger der Fehler als der Weg: Eine Rasteransicht hätte
ihn nie gezeigt, weil sie ohnehin die stärksten Verknüpfungen zeigt. Erst die
Frage nach einem einzelnen Wert brachte ihn ans Licht.

## Der Fragenkatalog — und was er offenlegte

Acht Fragen, jede mit einer Prüfung, die nicht liest sondern nachrechnet:
Steht die Zahl in der Werkzeugspur? Wurde das richtige Werkzeug gerufen? Wird
die Einschränkung genannt?

Erster Durchlauf: **drei von acht.** Der Agent scheiterte nicht am Verstehen,
sondern an der **Werkzeugwahl**.

| Frage | gerufen | richtig gewesen wäre |
| --- | --- | --- |
| „Was sagt die Fachliteratur zu Verlustaversion?" | `kurs` | `wissen` |
| „Was wird über Bitcoin geschrieben?" | `wissen` × 6 | `nachrichten` |
| „Auffällige Stellen von BTC-USD" | keins | `kurvenereignisse` |

Bei der Bitcoin-Frage rief er **sechsmal denselben Aufruf mit denselben
Argumenten** auf, verbrauchte alle Runden und lieferte am Ende gar nichts. Er
hatte die Antwort längst — er erkannte sie nur nicht als solche.

### Drei Korrekturen

**Wiederholte Aufrufe werden abgefangen.** Statt erneut zu rechnen bekommt das
Modell das gespeicherte Ergebnis zurück, mit dem Hinweis, dass es dieselbe
Frage schon gestellt hat und sie jetzt beantworten soll.

**Die Beschreibungen sagen, wofür das jeweils andere Werkzeug zuständig ist.**
Mit „durchsucht die Fachliteratur" gegen „durchsucht die Nachrichten" war die
Wahl nicht entscheidbar. Jetzt heißt es „ZEITLOSE Fachliteratur […] NICHT für
aktuelle Ereignisse — dafür ist `nachrichten` da" und umgekehrt.

**Ein Wegweiser in der Anweisung** — acht Fragetypen, acht Werkzeuge, wörtlich
zugeordnet. Die Werkzeugwahl war die häufigste Fehlerquelle; sie dem Modell zu
überlassen war zu viel verlangt.

## Modell

Voreingestellt `nemotron3:33b`, zur Laufzeit umschaltbar über
`POST /api/reasoning/modell?name=…&denken=…` oder die Auswahl in der
Oberfläche.

Gemessen an derselben Frage („Welche Modelle sind geladen und was taugen sie?"):

| Modell | Zeit | Ergebnis |
| --- | ---: | --- |
| `nemotron3:33b` | 251 s | korrekt |
| `qwen3:8b` mit Denken | 264 s | korrekt, aber **unzuverlässig** — mal ruft es das Werkzeug, mal beschreibt es nur, was es täte |
| `qwen3:8b` ohne Denken | 19 s | **inhaltlich falsch** |
| `qwen3-vl:4b` | 266 s | **Aussage umgedreht** |
| `granite3.2-vision` | — | beherrscht keine Werkzeugaufrufe |

`qwen3-vl:4b` las ein Fehlerverhältnis von 1,0034 als *„nahezu perfekt"* und
behauptete, das Modell schlage den Stillstand — das Gegenteil dessen, was das
Werkzeug wörtlich geliefert hatte. Ohne Denken verlor `qwen3:8b` sämtliche
Zahlen und las „Sperrbereich" als Handelsverbot.

**Ein kleines Modell ist hier gefährlicher als ein langsames.** Die ganze
Skepsis-Mechanik dieses Projekts steht darauf, dass die Einschränkung mitgesagt
wird. Wer Geschwindigkeit braucht, nimmt nicht ein kleineres Modell, sondern
mehr Rechenleistung — siehe unten.

## Rechenleistung mieten

Falls die lokale Hardware nicht reicht: `/api/ollama/endpunkte` verwaltet
Ollama-Endpunkte mit **SSH-Tunnel im eigenen Prozess**, nach dem Vorbild aus
`docuproc`.

```
GET    /api/ollama/endpunkte
POST   /api/ollama/endpunkte      {Id, Name, SshHost, SshPort, NutztTunnel, …}
POST   /api/ollama/waehlen?id=…
POST   /api/ollama/pruefen?id=…
DELETE /api/ollama/endpunkte/{id}
```

**Warum ein Tunnel und keine offene Adresse.** Der nach außen abgebildete Port
einer gemieteten Maschine spricht reines HTTP; dort gingen die Fragen und die
Analysewerte im Klartext durchs Netz. Die Alternative — der Nutzer hält ein
SSH-Fenster offen — ist keine Lösung, sondern eine Fehlerquelle.

Der lokale Port kommt vom Betriebssystem. Eine feste Nummer würde mit einem
lokal laufenden Ollama auf 11434 kollidieren, und genau das ist der Normalfall.

Reasoning **und** Einbettung holen ihre Adresse je Aufruf vom Endpunktdienst —
eine einmal gesetzte `BaseAddress` liesse sich später nicht mehr wechseln.

> Bei vast.ai gilt ein Kontoschlüssel nur für **neu erstellte** Instanzen. Bei
> „Permission denied" hilft nur eine neue Instanz.

Das Modell muss **Werkzeugaufrufe** beherrschen. Geprüft mit:

```bash
curl -s http://localhost:11434/api/chat -d '{"model":"…","stream":false,
  "messages":[{"role":"user","content":"…"}],"tools":[…]}'
```

Eine Falle beim Rückspielen: Ollama erwartet in `tool_calls[].function.arguments`
ein **Objekt**, keine Zeichenkette. Der erste Entwurf baute die Aufrufe neu und
machte eine Zeichenkette daraus — die zweite Runde antwortete mit 400, der Agent
rief also genau einmal ein Werkzeug auf und brach ab. Am einfachsten ist, gar
nichts umzubauen: Was das Modell geschickt hat, geht unverändert zurück.

## Aufrufe

```
GET  /api/reasoning/health
POST /api/reasoning/ask     {frage, verlauf: [{role, content}, …]}
```

Der Verlauf lebt im Browser, nicht auf dem Server: Ein Gespräch ist an das
Fenster gebunden, und ein Neustart der API soll es nicht wegräumen.

## Vorgemerkt

- [ ] **Zugriff auf die Kapitalfluss-Säule** — Rotationsverdacht und
      Flusszuordnung fehlen noch als Werkzeug.
- [ ] **Rückblick als Werkzeug.** „Wie gut lag das Modell bei NVDA in den
      letzten zwei Jahren" ist die naheliegendste Frage und derzeit nicht
      beantwortbar.
- [ ] **Antwortstrom.** Bei zwei bis drei Minuten Wartezeit wäre eine
      Antwort, die wortweise erscheint, deutlich erträglicher.
