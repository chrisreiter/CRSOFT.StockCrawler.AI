# CLAUDE.md

Hinweise für Claude Code (claude.ai/code) zur Arbeit in diesem Repository.

## Worum es geht

.NET 8 System (CRSOFT.StockCrawler), das Kurse von Aktien, Fonds/ETFs und Kryptowährungen in ein
einheitliches Modell sammelt, in einer Weboberfläche darstellt, die
Wechselwirkungen zwischen den Kursen analysiert und daraus selbstlernende
Prognosen erzeugt.

Ausführliche Beschreibung in [README.md](README.md) — einschließlich der
Provider-Bewertung und der Begründung der Architekturentscheidungen.

## Aufbau

```
src/
  Ingest.Core/            Modelle, Schnittstellen, Analyse-/Prognosemathematik
    Analysis/             Statistics, SeriesAligner, BarNormalizer,
                          CrossingDetector, ForecastModels, Ensemble,
                          SavitzkyGolay, CurveDiscussion, CurveEventLinker
  Ingest.Infrastructure/
    Providers/            YahooProvider, TwelveDataProvider,
                          CoinGeckoUniverseProvider, YahooScreenerUniverseProvider
    Repositories/         Dapper; SqlBulkCopy für Massenschreibvorgänge
    Services/             Universe, Ingest, Analysis, Forecast, Scoring, Backtest
  Ingest.Api/             Minimal-API, CronScheduler, wwwroot (Oberfläche)
infra/sql/                010 Schema, 011 Analyse/Prognose, 012 Prozeduren
infra/sql/legacy/         abgelöstes Ursprungsschema, nur als Referenz
```

## Befehle

```bash
dotnet build                                    # aus CRS.StockCrawler/
powershell -File infra/apply-sql.ps1            # Schema einspielen (idempotent)
cd src/Ingest.Api && dotnet run                 # API auf http://localhost:5011
```

Erstbefüllung: `POST /api/ingest/bootstrap?months=24`

## Regeln, die beim Ändern zu beachten sind

**Zeitstempel immer über `BarNormalizer` rastern.** Yahoo stempelt Tagesbars von
US-Aktien auf 13:30 UTC, Krypto auf 00:00; Stundenbars auf `:30` gegenüber
`:00`. Ohne Rasterung haben zwei Reihen null gemeinsame Zeitpunkte und jede
Korrelation ist leer. Passiert in `IngestService.IngestOneAsync`.

**Analyse richtet paarweise aus, nie global.** Eine gemeinsame Zeitachse über
alle verfolgten Werte existiert nicht — ein Feiertag an einer Börse macht den
Tag für sämtliche Paare unbrauchbar. `SeriesAligner.Intersect` ist nur für
kleine, bewusst gewählte Mengen gedacht.

**Der Prognoseverlust bestraft auch die Richtung.** Optimiert man allein den
Betragsfehler, gewinnt `naive` („Kurs bleibt gleich") — rechnerisch korrekt,
als Aussage wertlos. Der Parameter `directionPenalty` in
`Ensemble.UpdateWeights` verhindert das. Belegt: Trefferquote 51,7 % → 60,4 %
bei unverändertem Betragsfehler.

**Array-Optionen nicht vorbelegen.** Der Konfigurations-Binder hängt Werte an
ein vorhandenes Array AN, statt es zu ersetzen. `IngestOptions.Horizons` startet
deshalb leer; der Standard kommt über `EffectiveHorizons`.

**Jede Auswertung über mehrere Werte nur auf gemeinsamen Handelszeitpunkten.**
Aktien und Krypto haben verschiedene Handelskalender: samstags handelt nur
Krypto, der Aktienumsatz ist null. Wer über das rohe Zeitraster rechnet, misst
den Kalender statt den Markt — und zwar überzeugend genug, um es zu glauben.
Belegt in drei Anläufen: Rotationsverdacht kam für jedes Krypto-Aktien-Paar auf
−0,93 (perfekt gegenläufig, ohne jede Aussage); die Flusszuordnung wies
17.192 Mrd $ von Krypto in Aktien aus, weil Montage höher bewertet werden als
Samstage (nach der Korrektur: 1.268 Mrd $); die Kapitalauffälligkeiten waren zu
hundert Prozent Wochenenden mit −93 % und +1.187 %. Das Muster ist immer
dasselbe: je Schritt nur die Werte einbeziehen, die zu BEIDEN Zeitpunkten
gehandelt haben, und die Anteile auf diese Teilmenge neu beziehen. Umgesetzt in
`FlowAttribution.Attribute`, `FlowCoincidence.FindAnomalies` und
`/api/flow/rotation-pairs`.

**Spektren von Kursreihen sind rot, nicht flach.** Die Leistung fällt monoton
mit der Frequenz. Wer eine Spitze gegen den Median des Bandes misst, findet
deshalb IMMER die längste betrachtete Periode als angeblich dominant — NVDA,
Bitcoin und BNB meldeten so alle drei denselben „Zyklus“ mit dreistelliger
Prominenz, nämlich exakt ein Drittel der Teilfensterlänge. Gemessen wird gegen
den örtlichen Untergrund in einem Ring um die Stelle (`Spectrum.LocalBackground`).
Weitere vier Fallen derselben Art in [docs/SAEULE-MATHEMATIK.md](docs/SAEULE-MATHEMATIK.md).

**Prognosebeiträge brauchen eine Rückhalteprüfung, keine Selbstauskunft.**
„Erklärte Streuung“ sagt nichts über Prognosegüte: Auf Log-Kursen erklärt die
erste SSA-Komponente über 95 %, weil sie den Trend einfängt. Verfahren der
zweiten Säule bekommen ihr Gewicht deshalb aus `SpectralEndpoints.Holdout` —
dem gemessenen Vorsprung gegenüber „der Kurs bleibt stehen“. Ohne Vorsprung
null Gewicht. Auf echten Daten trägt die Säule bei drei von vier Werten nichts
bei, und das ist das erwartete Ergebnis.

**Überlappende Zeitfenster nicht als eigenständige Beobachtungen zählen.**
Wird eine Folgerendite über h Bars an jeder einzelnen Bar ausgewertet, geht
dieselbe Kursbewegung h-fach in die Rechnung ein. Die Fallzahl sieht h-mal
größer aus, als sie ist, und jeder t-Wert ist um rund √h zu hoch. Belegt am
Buchgewinn-Merkmal: überlappend t = 10,56, überlappungsfrei t = 1,64 — aus
einem scheinbar zwingenden Befund wurde ein nicht signifikanter. Auswerten
deshalb nur jede h-te Bar (`FeatureControl.Compare`). Vergleiche über
**getrennte Zeiträume** (Modenanalyse, Vorlaufordnung, Differential-Säule) sind
davon nicht betroffen — sie stellen keine t-Werte über überlappende Fenster auf.

**Ein neues Merkmal muss gegen das nächstliegende bekannte kontrolliert werden.**
Der geschätzte Buchgewinn der Halter ist der Abstand des Kurses von einem
gleitenden umsatzgewichteten Mittel — dem Bauplan nach Momentum. Gemessene
Korrelation der beiden: 0,88. Gemeinsam gerechnet erreicht keines von beiden
Signifikanz. Ohne die Kontrolle wäre ein bekannter Effekt unter neuem Namen als
eigene Säule gelandet. Muster siehe `FeatureControl.Compare` — einzeln,
gemeinsam und als Doppelsortierung.

**Erhaltung verknüpft Gleichzeitiges, nicht Aufeinanderfolgendes.** Der
Querschnitt aller Kurse eines Tages erklärt die Bewegung eines einzelnen am
selben Tag sehr gut — R² 0,355 im Mittel, bis 0,641; als Netz Fehlerverhältnis
0,915 bei 66,5 % Richtung. Einen Tag vorausgesetzt bleibt davon R² 0,0039,
Faktor 91. Drei unabhängige Verfahren, jedes außerhalb des Anpassungszeitraums
gemessen. Passt zur Modenanalyse: gemeinsamer Modus 53 % bei 2,8 Bars
Phasenstreuung — die Werte bewegen sich gemeinsam, nicht nacheinander.
Belege in [docs/ERHALTUNG-QUERSCHNITT.md](docs/ERHALTUNG-QUERSCHNITT.md).
**Ein Negativergebnis braucht seine Kontrolle**: dieselbe Architektur auf die
gleichzeitige Frage angesetzt (`train_cross.py --contemporaneous`), sonst ist
„das Modell findet nichts" nicht von „der Code ist kaputt" zu unterscheiden.

**Nichts stillschweigend aus einer Antwort weglassen.** `/api/series/` verwarf
Werte ohne Bars kommentarlos — wer drei Kurse wählte und zwei Linien sah, suchte
den Fehler in der Auswahl. Tatsächlich hatte ein junger Kryptowert keine
Stundendaten, während das Diagramm auf Stundenintervall stand. Die Antwort führt
weggelassene Werte jetzt unter `skipped` mit Begründung; die Oberfläche zeigt sie
über dem Diagramm.

**uPlot nie in einer verborgenen Ansicht endgültig vermessen.** Die Breite ist
eine feste Pixelzahl, gemessen beim Zeichnen. Seit die Sitzung wiederhergestellt
wird, zeichnet der Seitenaufbau in eine Ansicht hinter `display: none` — das
Diagramm blieb schmal und wurde es auch, als die Ansicht aufging. Das
`resize`-Ereignis greift nicht, weil sich das Fenster nicht ändert. Ein
`ResizeObserver` auf `#charts` zieht die Größe nach.

**Eigene Dateien mit Fassungsnummer ausliefern.** `app.js` wurde vom Browser
gehalten; man sucht dann Fehler in Code, der gar nicht geladen ist. `Program.cs`
ersetzt `@BUILD@` in `index.html` beim Ausliefern durch die Startzeit —
`UseDefaultFiles` musste dafür weichen, weil es `/` umschreibt, bevor der
Endpunkt drankommt.

**Zwei fast gleiche Kurven übereinander sind kein Diagramm.** Prognose und
Ist-Verlauf liegen bei Tagesdaten immer dicht beieinander, auch wenn das Modell
nichts kann — 2,173 % gegen 2,170 %. Der Rückblick zeigt deshalb den
**aufsummierten Vorsprung**: je Vorhersage der Fehler des Stillstands minus der
eigene. Die Linie steigt, solange das Modell beiträgt, und fällt sonst.

**Ableitungen über eine lokale Polynomanpassung, nicht über Differenzen.**
Ein gleitendes Mittel verschmiert genau an den Wenden am stärksten — dort, wo
die Kurve am meisten zu sagen hat —, und jede Differenz verdoppelt den
Rauschanteil, die zweite vervierfacht ihn. `SavitzkyGolay.Fit` legt an jeder
Stelle ein Polynom zweiten Grades an; Wert, Steigung und Krümmung sind dessen
Koeffizienten. Gerechnet wird auf Log-Kursen, sonst hängt die Steigung am
Kursniveau. Zentriert für die Beschreibung der Vergangenheit, kausal für alles,
was in eine Prognose fließt — die Wahl steht in der Oberfläche und wird je Lauf
mit abgelegt. Belege in [docs/SAEULE-KURVENDISKUSSION.md](docs/SAEULE-KURVENDISKUSSION.md).

**Nullstellen über den Vorzeichenwechsel, nicht über „nahe null".** Ein
Schwellenwert auf |f'| findet in ruhigen Phasen hunderte Stellen und in
bewegten keine.

**Filter gehören in die Abfrage, nicht dahinter.** `LinksAsync` holte die
stärksten 300 Verknüpfungen und filterte danach nach Symbol. Bei 135.481
Einträgen ist ein einzelner Wert dort praktisch nie dabei — der Reasoning-Agent
meldete daraufhin wahrheitswidrig, es gebe keine. Dazu ein Index je Spalte eines
ODER (`asset_a`, `asset_b` getrennt), sonst Zeitüberlauf statt einer Sekunde.
Bemerkenswert: Eine Rasteransicht hätte den Fehler nie gezeigt, weil sie ohnehin
die stärksten Einträge zeigt.

**Nutzlast in Qdrant typgerecht ablegen.** `source_id` lag als Zeichenkette vor,
der Löschfilter vergleicht gegen eine Zahl — „1" ist nicht 1, also traf er nie
etwas. Beim erneuten Einbetten blieben alte Vektoren stehen, während die
Abschnitte in SQL gelöscht wurden; die Suche fand Punkte ohne Text und lieferte
für drei von vier Fragen gar nichts. Kein Fehler, keine Meldung, nur leere
Antworten.

**PDF-Kopf- und Fußzeilen über die Geometrie entfernen, nicht über den Text.**
`page.Text` liefert die ganze Seite als eine Zeichenkette ohne Zeilenumbrüche,
und eine Fußzeile trägt meist die Seitenzahl mit — sie ist also nie wortgleich.
Über `page.GetWords()` und deren Grundlinien lässt sich das oberste und unterste
Zwanzigstel verwerfen und zugleich die Zeilenstruktur zurückgewinnen. Ohne das
antwortete die Wissenssuche mit Fußnotenapparat und Literaturverzeichnis; das
Literaturverzeichnis erkennt man an der Dichte von Jahreszahlen in Klammern,
nicht am Ziffernanteil. Details in
[docs/SAEULEN-WISSEN-SEMANTIK.md](docs/SAEULEN-WISSEN-SEMANTIK.md).

**Werkzeugaufrufe unverändert an Ollama zurückspielen.** Erwartet wird in
`tool_calls[].function.arguments` ein Objekt, keine Zeichenkette. Wer die
Aufrufe neu baut, bekommt in der zweiten Runde 400 — der Agent ruft dann genau
einmal ein Werkzeug auf und bricht ab.

**Der Reasoning-Agent muss die Grenzen mitsagen.** Seine Anweisung verpflichtet
ihn, zu jeder Prognose das Fehlerverhältnis im Sperrbereich zu nennen und bei
Werten ab 1 ausdrücklich zu sagen, dass sie keine Handelsgrundlage ist. Ein
Agent, der eine Prognose ohne ihren gemessenen Wert weitergibt, wäre die
gefährlichste Komponente dieses Systems — er klänge kompetent und wäre es nicht.

**Die Auswahl muss das Diagramm neu zeichnen.** `renderAvailable` und
`renderSelected` haben die Liste gepflegt, aber nicht neu gezeichnet — das
geschah nur beim Klick auf „Anzeigen". Wer einen vierten Wert dazunahm, sah
weiterhin die drei von vorher, während der Zähler links bereits vier zeigte. Das
liest sich als „es wird immer einer weniger angezeigt als ausgewählt" und wurde
auch so gemeldet. `redrawSoon` (verzögert, damit mehrfaches Klicken nicht
mehrfach abfragt) hängt jetzt an beiden Handlern.

**Fassungsnummern aus dem Änderungszeitpunkt der Datei, nicht aus der
Startzeit.** Der erste Entwurf stempelte `@BUILD@` mit `DateTime.UtcNow` beim
Anwendungsstart. Das bustet den Cache bei jedem Neustart und — entscheidend —
NICHT, wenn man `app.js` ändert, ohne neu zu starten. Genau das ist beim
Entwickeln der Normalfall, und die Folge ist, dass man Code debuggt, der gar
nicht geladen ist. Zweimal in dieser Sitzung passiert. `File.GetLastWriteTimeUtc`
je Datei löst es.

**Ein Hinweis muss zur Lage passen.** Die Meldung über ausgelassene Werte riet
immer „mit Tagesintervall erscheinen sie meist" — auch dann, wenn das Diagramm
bereits auf Tagesintervall stand. Ein Rat, der zu etwas rät, das der Nutzer
gerade tut, ist schlimmer als keiner: Er schickt ihn auf eine Fährte, die es
nicht gibt.

**Der Stillstand ist nur die erste Latte. Die zweite ist die blosse Drift.**
Über lange Horizonte steigen Kurse im Mittel; ein Modell, das nur „aufwärts"
sagt, schlägt den Stillstand zwangsläufig. Das lange Modell meldete bei 250
Tagen Fehlerverhältnis 0,9604 bei 64,3 % Richtung und sah nach dem ersten
Erfolg dieses Projekts aus. Die mittlere Rendite des Trainingszeitraums — EINE
Zahl — kam auf 0,9139 bei 75,1 %; von den Zielwerten im Sperrbereich waren
75,1 % positiv, die Trefferquote des Netzes lag also unter der eines Würfels,
der immer „aufwärts" sagt. **Eine einzige Zahl schlug alle neun
Modellausgaben, auf beiden Maßen.** Umgesetzt in `train_deep.py`
(`drift_error_ratio_by_horizon`), `DeepModelInfo.Carries()` und der
Bänder-Ansicht. Belege in
[docs/SAEULE-DEEP-LEARNING.md](docs/SAEULE-DEEP-LEARNING.md).

**Patch-Skripte müssen prüfen, ob ihr Muster getroffen hat.** Ein `str.replace`
ohne `assert` schlägt still fehl, und der alte Code bleibt stehen. Genau so kam
in der Bänder-Ansicht „33 % bewährt" heraus, wo 0 % richtig war: Der Ersatz für
die Gewichtsrechnung traf wegen eines abweichenden Feldnamens nicht, und die
alte Zeile prüfte weiterhin nur die erste Latte.

**Ein Filter, der für HTML gebaut ist, darf nicht auf reinen Text los.** Der
Fließtext-Filter gegen Navigationsmenüs urteilte zeilenweise. Gutenberg-Texte
sind hart auf siebzig Zeichen umbrochen — also ist jede Zeile zu kurz und
enthält keinen vollständigen Satz. Von zwölf Büchern kam eines durch, der Rest
meldete „Text zu kurz". Zwei Korrekturen: Der Inhaltstyp entscheidet, ob
überhaupt gereinigt wird, und der Filter urteilt über **Absätze** statt Zeilen.
*Reminiscences of a Stock Operator* ging von 0 auf 633 Abschnitte.

**Eine Adresse sagt nicht, was hinter ihr steckt.** PDF, reiner Text und HTML
kommen über dieselbe Schnittstelle. Ein PDF durch die HTML-Reinigung zu schicken
liefert Byte-Salat, den niemand als Fehler erkennt: Es entstehen Abschnitte, sie
werden eingebettet, und die Suche findet später Unsinn mit ordentlich
aussehenden Ähnlichkeitswerten. Der Inhaltstyp der Antwort entscheidet.

**Nachrichten über Feeds holen, nicht über Seiten.** Eine Seite liefert bei
jedem Lauf dieselbe Adresse mit anderem Inhalt — man weiß danach nicht, was neu
war. Ein Feed liefert einzelne Meldungen mit Adresse und Zeitstempel. Erst das
erlaubt beides: nur Neues einzubetten und eine Aussage später in Bezug zu einer
Kursbewegung zu setzen. Ein Artikel wird deshalb eine eigene Quelle mit
`parent_source_id` auf seinen Feed. Details in
[docs/QUELLEN-UND-FEEDS.md](docs/QUELLEN-UND-FEEDS.md).

**Deutsche Anführungszeichen in interpolierten C#-Zeichenketten.** `$"… „{x}" …"`
endet am geraden Anführungszeichen und bricht die Übersetzung. Das schließende
deutsche Zeichen ist `“`, nicht `"`. Dreimal in einer Sitzung passiert.

**Kennungen aus dem Inhalt statt aus dem Zufall.** Solange `vector_id` ein
`NEWID()` war, hieß jeder Neuaufbau: erst löschen, dann schreiben. Zwei
gleichzeitige Läufe auf derselben Quelle löschen beide und schreiben danach
beide — das Ergebnis ist die doppelte Menge, ohne Fehlermeldung. Genau so
bekamen sechs Quellen exakt doppelt so viele Abschnitte, wie ihre eigene
Zählung meldete. Die Kennung ist jetzt `SHA-256(source_id | ordinal | text)`,
in SQL wie in Qdrant dieselbe; geschrieben wird per MERGE beziehungsweise
Upsert. Damit ist die Operation **idempotent**: Duplikate werden unmöglich,
statt nachträglich entfernt zu werden. Nebenertrag: Ein erzwungener Neuaufbau
über unveränderten Text kostet 2 statt 66 Sekunden, weil nur eingebettet wird,
was sich geändert hat. Ein eindeutiger Index auf `(source_id, ordinal)` sichert
es auf Datenbankebene ab.

**Verwaiste Vektoren erzeugen keinen Fehler, sondern weniger Treffer.** Ein
Punkt in Qdrant ohne zugehörigen Abschnitt in SQL wird gefunden, nachgeschlagen
— und verschwindet still aus der Ergebnisliste. `POST /api/knowledge/aufraeumen`
gleicht beide Seiten ab; er bleibt als Prüfung stehen, denn ein Abgleich, den
man jederzeit fahren kann, ist mehr wert als die Annahme, es könne nicht mehr
vorkommen.

**Ein Inhalts-Hash muss auch das Verfahren kennen.** `ChunkHash` bestand
zuerst nur aus `(source_id, ordinal, text)`. Als die Positionsspalten
(`char_from`, `anchor`) dazukamen, änderte sich der Text nicht — also meldete
jeder Lauf „alle unverändert", schrieb nichts, und die neuen Spalten blieben bei
1.064 Abschnitten leer. Die Ersparnis eines übersprungenen Laufs war damit
erkauft, dass eine Verbesserung nie ankommt. `VerarbeitungsFassung` steckt jetzt
im Hash; Hochzählen erzwingt genau einen vollständigen Neuaufbau. Dieselbe Rolle
wie `FeatureSet.Version` beim Merkmalsexport.

**Eine Sperre muss in jedem Fall wieder gelöst werden.** Die Einbettungssperre
wurde vor der Arbeit gesetzt und nur auf dem Erfolgspfad gelöst. Brach etwas
dazwischen ab, blieb die Quelle eine halbe Stunde blockiert und antwortete mit
„läuft bereits", obwohl nichts lief. Der Rumpf steht jetzt in einem eigenen
Aufruf, den ein `catch` umschließt.

**Ein Treffer ohne Fundstelle ist eine Behauptung.** Wer ihn nachschlagen will,
müsste die ganze Quelle durchsehen — genau das soll die Suche ersparen. Jeder
Abschnitt trägt deshalb `char_from`/`char_to` und einen Textanker; die Antwort
liefert `fundstelle` und `verweis`. PDFs bekommen `#page=N`, alles andere
`#:~:text=…`. Der Anker muss am **Satzanfang** beginnen: Durch die Überlappung
fängt ein Abschnitt oft mit einem Punkt oder halben Wort an, und der Browser
vergleicht wörtlich.

**Ein Bedienelement muss wirken.** Die Säulengewichte wurden gespeichert und
von nichts gelesen — die Prognose kam allein aus `ForecastService`/`Ensemble`.
Wer an den Reglern zog, änderte nichts. `CombinedForecastService` mischt jetzt
nach **Gewicht × Verdienst**; der Verdienst kommt aus dem Sperrbereich, nicht
aus der Einstellung. Hat keine Säule Verdienst, zählt das Gewicht allein — der
erste Entwurf mittelte dort gleich, und das nimmt dem Nutzer die Kontrolle
genau dort, wo das System ihm nichts Besseres anzubieten hat. Nachgewiesen an
NVDA, ein Tag: −0,4504 % / −0,1895 % / +0,0720 % bei learning:deep von 100:0,
50:50, 0:100.

**Zentrierte Glättung kann „heute" nicht.** Ein zentriert geglätteter
Kurvenlauf lässt die letzten `halbfenster` Bars grundsätzlich unbewertet — sein
Fenster reicht dort über das Ende der Reihe hinaus. Gemessen: Lauf bis
11. August, jüngste Bar 22. August. Elf Tage, genau die halbe Fensterbreite.
Ein frisch gerechneter zentrierter Lauf hilft nicht; für die Gegenwart braucht
es `LatestCausalRunAsync`.

**Der Rückfall auf die Startseite darf nicht für `/api` gelten.** Ein
vertippter API-Pfad lieferte HTML mit Status 200. Der eigene Prüflauf meldete
drei falsche Pfade als bestanden — die unangenehmste Art von Fehler.

**Tote Werte erzeugen Scheinbefunde, keine Fehler.** JUP-USD meldete −58 % je
Bar bei Kurs 0,0000 und Stufe 100. Formal richtig, gemessen an der eigenen
Schwankung dieser Reihe — es beschreibt aber das Ende eines Wertes und nicht
den Markt. Von 324 verfolgten Werten waren 39 betroffen; `/api/hygiene/stumm`
findet sie mit vier getrennten Gründen, Probelauf als Voreinstellung.

**Ein kleines Sprachmodell ist hier gefährlicher als ein langsames.**
`qwen3-vl:4b` las ein Fehlerverhältnis von 1,0034 als „nahezu perfekt" und
behauptete, das Modell schlage den Stillstand — das Gegenteil dessen, was das
Werkzeug wörtlich geliefert hatte. `qwen3:8b` **mit** Denken gibt die Urteile
korrekt wieder (264 s); **ohne** Denken verlor es alle Zahlen und las
„Sperrbereich" als Handelsverbot (19 s). Die Voreinstellung ist deshalb die
genaue. Wo Rechenleistung fehlt, hilft `/api/ollama/endpunkte` mit
SSH-Tunnel — nicht ein kleineres Modell.

**Merkmalsfenster nicht je Aufruf neu laden.** `BuildWindowAsync` holt die
Kurse aller verfolgten Werte; bei fünf Werten und drei Bändern sind das
fünfzehn Ladungen derselben Daten. Gemessen: 130 Sekunden für ein Diagramm.
Mit einem Zwischenspeicher von fünf Minuten, verworfen nach jedem Kursabruf:
4 Sekunden.

**Werkzeugargumente tolerant lesen.** Ein Sprachmodell hält sich nicht an das
Schema: Auf die Frage nach auffälligen Stellen von BTC-USD rief es `prognose`
mit `{"wert":"BTC-USD","sperrbereich":"letzte 7 Tage","qualitaet":"hohe"}` auf
— „wert" statt „symbol", dazu zwei erfundene Felder. Ein strenger Leser findet
dann nichts, meldet „Kein verfolgter Wert mit dem Symbol ." und das Modell
versucht es mit denselben Namen erneut, bis alle Runden verbraucht sind.
`Str(params string[] namen)` liest jetzt `symbol`, `wert`, `ticker`, `name`,
`asset`, `kurs` — und im zweiten Anlauf ohne Rücksicht auf Groß- und
Kleinschreibung. Erfundene Zusatzfelder werden übergangen.

**Eine Fehlermeldung muss sagen, was fehlt.** „Kein verfolgter Wert mit dem
Symbol ." nennt weder das Problem noch den Ausweg. Jetzt: welches Feld erwartet
wird, mit Beispiel, und der Hinweis auf `werteliste`.

**Dublettenschlüssel dürfen nicht an der Feldreihenfolge hängen.** Dasselbe
Modell schickte dieselbe Abfrage dreimal mit anders sortierten Feldern — als
Zeichenkette verglichen waren das drei verschiedene, und die Sperre griff nicht.

**Ein Werkzeug, das still falsch arbeitet, ist schlimmer als keines.** Der
eigene Anführungszeichen-Korrektor hat gültigen Code zerstört: Er ersetzte das
schließende `"` einer Zeichenkette, wenn davor mehrere deutsche
Anführungszeichen standen. Das letzte gerade Anführungszeichen einer Zeile ist
der Abschluss und wird nie angetastet. Dieselbe Lehre wie beim `str.replace`
ohne `assert`.

**Der Erwartungswert entscheidet, nicht die Trefferquote.** Der erste Entwurf
der Day-Trading-Seite verlangte „Trefferquote über 0,523 UND Bewegung über den
Kosten" — und wies den Stundenhorizont als tragfähig aus: 52,4 % bei 0,47 %
mittlerer Bewegung. Nachgerechnet sind das (2·0,524−1)·0,47 % = 0,023 %
Bruttovorsprung gegen 0,3 % Rundlauf, also **−0,277 % je Geschäft**. Beide
Bedingungen erfüllt, und trotzdem ein Verlustgeschäft. Richtig ist
`(2p − 1)·E|r| − Rundlauf` (`Handelskosten.Erwartungswert`). Die Umkehrung ist
die ernüchterndste Zahl der Anwendung: Bei 0,33 % mittlerer Stundenbewegung in
Krypto wären **95,4 %** Trefferquote nötig.

**Ein Tausch hat zwei Beine.** Gebühr und Schlupf fallen zweimal an. Wer nur
einmal rechnet, halbiert die Schwelle und findet doppelt so viele Gelegenheiten,
die keine sind. Voreinstellung 0,3 % je Rundlauf (`Handelskosten`).

**Eine hohe Trefferquote ist kein Gewinn.** Von 154 Kreuzungspaaren mit genug
Vorgeschichte bestehen 54 die Trefferquote von 55 %, aber nur **42** bringen
mehr ein, als der Tausch kostet. ETHFI-USD gegen TLH trifft in 59 % der Fälle
und verliert im Mittel 1,17 %. Viele kleine Gewinne, seltene große Verluste —
das Profil, das in jeder Rückrechnung hervorragend aussieht. Neben der
Trefferquote muss immer der mittlere Ertrag nach Kosten stehen.

**Eine Rangliste nach erzieltem Gewinn muss je Symbol gedeckelt werden.**
MNT-USD verlor in einem Monat die Hälfte — damit standen **24 der 25** besten
Kreuzungszeilen auf „irgendetwas gegen MNT-USD". Jede rechnerisch richtig, als
Übersicht eine Zeile, vierundzwanzigmal geschrieben. Gezählt wird auf beiden
Seiten; Voreinstellung drei.

**Ein Ein-Bar-Sprung von 50 % ist kein Kurs.** Yahoo liefert für MNST um den
2:1-Split vom 10.08.2026 eine **gemischte** Reihe: 97,65 → 48,19 → 93,55 →
90,36 → 45,53, mit `adj_close = close` durchgehend. Ein vollständiger Neuabruf
ändert nichts — der Fehler sitzt beim Anbieter. Ohne die Sprungprüfung waren
zwanzig der fünfundzwanzig besten „Paargewinne" nichts als „irgendetwas gegen
MNST". Ausgelassene Paare werden **mit Grund angezeigt**, sonst verbirgt der
Filter zugleich, dass eine Kursreihe kaputt ist.

**Bewährung und Anzeige brauchen getrennte Zeiträume.** Misst man die
Trefferquote früherer Kreuzungen einschließlich der gerade angezeigten, benotet
sich jede Zeile selbst. `c.ts_utc < @seit` in `KreuzungsBewaehrung`.

**Erst die jüngste Kreuzung, dann filtern.** Bei den Tauschvorschlägen wird je
Paar die letzte Kreuzung genommen und *danach* geprüft, ob der gehaltene Wert
unten liegt. Andersherum bekommt man ein Verkaufssignal, das eine spätere
Gegenkreuzung längst aufgehoben hat.

**Dieselbe Quelle zweimal einzutragen ist kein Fehler.** `AddWebAsync` ließ die
Verletzung des eindeutigen Index bis zum Aufrufer durchschlagen: Beim erneuten
Einlesen derselben arXiv-Auswahl waren das neununddreißig Antworten mit Status
500, obwohl nichts kaputt war. Vorhandenes wird jetzt zurückgegeben —
dieselbe Regel wie bei den Abschnittskennungen: Doppeltes unmöglich machen,
statt es hinterher aufzuräumen.

**`first_seen_utc` ist kein Erstnotiz-Datum.** Es steht bei allen 338 Werten auf
dem Tag, an dem die Datenbank gefüllt wurde. Schwerer wiegt: Das Universum ist
die Rangliste nach Marktkapitalisierung, ein Wert taucht also erst auf, *nachdem*
er gestiegen ist. Eine Statistik über „Neuzugänge" misst damit Gewinner mit
bereits gelaufenem Anstieg. Wer Neulinge auswerten will, braucht die
**vollständige** Kohorte aus Listing-Ankündigungen, Fehlschläge eingeschlossen.

**Deep-Horizonte zählen in Tagesbars.** Sie auf der Day-Trading-Seite als
Stundenmaß auszugeben wäre ein Faktor 24 daneben. Was dort gilt, sind die
bewerteten Prognosen mit `horizon_hours` 1 und 4.

**`apply-sql.ps1` filterte auf `01*.sql`** und übersprang damit still die
Migrationen 020 bis 024. Ein Migrationsläufer, der Dateien überspringt, ohne es
zu sagen, ist schlimmer als keiner.

**Die Bewertung staut sich am Kopf der Warteschlange.** `get_due_forecasts`
nimmt die ältesten 5.000 nach Zielzeitpunkt. Von 18.086 fälligen Prognosen sind
**13.988 dauerhaft nicht bewertbar** — ihr Zielzeitpunkt fällt in ein
geschlossenes Marktfenster, dort kann nie eine neue Bar erscheinen. Sie stehen
vorn und blockieren die 4.098 bewertbaren dahinter. Seit dem 22.08. 16:09 wurde
nichts mehr bewertet; `Ensemble.UpdateWeights` lernt seither nicht. Erkennbar
sind sie: Existiert eine Bar **nach** dem Zielzeitpunkt und ist die letzte davor
nicht neuer als die Prognose, kann keine mehr kommen.

**Der Median, nicht der Mittelwert — sobald sterbende Werte im Bestand sind.**
Die Bot-Auslöser-Auswertung meldete für Krypto „RSI unter 30 → +45,0 % am
Folgetag" und einen Volumenfaktor von 15.591. Beides beschreibt das Ende von
Kleinstwerten: Ein Kurs von 0,0000 erzeugt beim kleinsten Sprung dreistellige
Prozentzahlen, und ein Volumen gegen ein Durchschnittsvolumen nahe null ergibt
beliebige Vielfache. Mit dem Median: +0,005 % und Faktor 0,75. Der Median
braucht keine Ausschlussregel für „tote" Werte, die man erst erfinden und
begründen müsste.

**Streuung misst sich an der ENGSTEN Bindung, nicht am Durchschnitt.** Die
Korbsuche wählte HWM, XAUT-USD und PAXG-USD — die letzten beiden sind
goldgedeckte Marken und laufen praktisch identisch. Die MITTLERE Korrelation
blieb bei 0,39, weil der dritte Wert sie herunterzog. Ein Korb aus drei Teilen,
von denen zwei dasselbe sind, ist ein Korb aus zwei Teilen.

**Korrelationen sind nicht stabil, und die Instabilität geht in die falsche
Richtung.** XAUT-USD gegen PAXG-USD: 0,838 in der Auswahlhälfte, **0,984** im
Sperrbereich. Die Streuung, auf die sich eine Auswahl stützt, ist zum
Messzeitpunkt möglicherweise weg. Deshalb zeigt die Langfrist-Ansicht beide
Zahlen nebeneinander — die Drift ist die ehrlichste Spalte der Seite.

**`Intersect` nur über kleine, bewusst gewählte Mengen — auch beim zweiten
Mal.** Der erste Entwurf der Langfrist-Ansicht schnitt die Zeitachsen aller 285
Reihen und behielt von fünf Jahren **48 Handelstage**: Ein einziger junger Wert
schneidet die Achse für alle ab. Kennzahlen eines einzelnen Wertes brauchen
keine gemeinsame Achse; nur ein Korb braucht sie, und dort umfasst sie genau
seine Mitglieder.

**Nachrichten hängen mit heute zusammen, nicht mit morgen.** Gemessen an
GDELT (Tonalität zu „stock market OR equities", tägliche Änderung) gegen SPY
über 648 Handelstage: **0,1365 gleichzeitig, 0,0419 prognostisch.** Die
Signifikanzschwelle liegt bei dieser Fallzahl um 0,077 — von vier geprüften
Kombinationen (Ton und Meldungsmenge, gegen SPY und BTC-USD) überschreitet sie
genau eine, und zwar die gleichzeitige. Die MENGE der Meldungen sagt auch
gleichzeitig nichts (−0,0167 gegen SPY): Es ist der Ton, nicht die Zahl.
Dieselbe Signatur wie beim Querschnitt (Faktor 91) und bei den
Kurvenereignissen (Medianabstand +1 Bar). Ein viertes Modell mit
Nachrichtenmerkmalen ist damit nicht nur mangels Daten verworfen, sondern
mangels Signal.

**Die Semantik-Säule taugt nicht als Modellmerkmal.** 115 Tage mit Meldungen
von 1.827 Handelstagen — 6,3 % —, und davon fast alles aus den letzten zehn
Tagen (22.08.: 382 Artikel, 15.08.: 3). Ein Merkmal, das nur für die jüngste
Vergangenheit existiert, lernt das Datum und nicht die Nachricht. Für eine
historische Nachrichtenreihe braucht es eine Quelle mit Historie; GDELT liefert
Tagesreihen ab 2015 (945 Punkte für 2024–2026 im Versuch), drosselt aber hart —
zwischen zwei Abfragen gehört mehr als eine Minute Pause.

**Ein gefilterter Index verlangt QUOTED_IDENTIFIER ON für JEDES Update auf der
Tabelle.** `CREATE INDEX … WHERE unscoreable_utc IS NULL` ließ den
Bewertungslauf mit Fehler 1934 scheitern — zur Laufzeit und mit einer Meldung,
die den Zusammenhang nicht nennt. Ein gewöhnlicher Index über dieselben Spalten
tut es fast genauso gut und stellt keine Bedingung an die Sitzung, in der
später jemand eine Prozedur anlegt.

**sqlcmd braucht `-f 65001` für UTF-8-Skripte.** Ohne das landeten die
Auslöser-Bezeichnungen als „RSI Ã¼ber 70" in der Datenbank — in einer Spalte,
die danach in der Oberfläche steht.

**Die Bot-Herde hinterlässt eine Spur, und die Verkaufssignale zeigen in die
falsche Richtung.** Über fünf Jahre, Median gegen den Median aller Tage
derselben Werte: Bei Aktien hat **jeder** der sieben geprüften Auslöser einen
positiven Zwanzigtagesüberschuss — auch die bärischen. Deren
Richtungstrefferquote liegt bei 0,40 bis 0,43, also unter dem Münzwurf. Am
deutlichsten „Bollinger-Ausbruch nach unten": 6.117 Fälle, **+1,006 %**
Überschuss, Richtung 0,40, Volumen am Auslösetag 1,30-fach. Von sieben
Auslösern schlagen drei den Rundlauf von 0,3 %.

**Der Umkehrschluss trägt nicht.** Von 26.130 Kreuzungspaaren mit mindestens
acht Kreuzungen weichen 291 auffällig nach unten ab — allein durch Zufall wären
bei so vielen Prüfungen **653** zu erwarten. Es gibt also weniger zuverlässig
falsche Paare, als der Zufall hervorbringt; die mittlere Trefferquote liegt bei
0,5096. Die Extremfälle sind ausnahmslos fast identische Paare — Gold gegen
Gold, irgendetwas gegen eine Stablecoin — und heißen dort Paarhandel.

**Ein laufendes Shell-Skript nicht überschreiben.** `sh` liest die Datei
nicht auf einmal, sondern merkt sich einen Byte-Offset und setzt dort fort. Wer
das Skript währenddessen austauscht, bringt die Shell dazu, an dieser Stelle in
der NEUEN Datei weiterzumachen — mitten in einer Zeile. Beim Umstellen des
Trainings von 8 auf 20 Fäden führte das dazu, dass der alte Lauf Bruchstücke
des neuen ausführte (`error: the following arguments are required: --csv`), die
Abschnittsmarker des neuen Skripts ins Protokoll schrieb und parallel zum neuen
Lauf um dieselben Kerne kämpfte. Zwei Trainingsprozesse auf 24 Kernen halbieren
beide. Ein neues Skript gehört unter einen neuen Namen — oder der Aufruf
gleich ohne Datei.

**Das Universum war ausschließlich amerikanisch, und niemand hat es gesehen.**
`SELECT COUNT(*) FROM dbo.asset WHERE symbol LIKE '%.%'` ergab **null** — kein
einziges nicht-amerikanisches Papier. Ursache: Die sechs vordefinierten
Yahoo-Screener (`most_actives`, `day_gainers`, …) kennen keinen
Regionsparameter. Aufgefallen ist es erst an der Frage „warum sehe ich
Rheinmetall nicht". Der in der README versprochene Weg, Papiere von Hand zu
ergänzen, existierte nicht. Jetzt: `POST /api/assets/aufnehmen` und
`POST /api/assets/welt` über `infra/welt.json`.

**`upsert_asset` schreibt `is_tracked` nicht.** Das Feld am Modell zu setzen
bleibt wirkungslos. Beim ersten Weltlauf waren 269 Werte angelegt und **keiner
verfolgt** — sie standen in der Datenbank und tauchten nirgends auf. Das
Einschalten ist ein eigener Aufruf (`SetTrackedAsync`).

**Die Rasterung zerstört die Zeitzonen-Reihenfolge.** Alle Tagesbars sitzen auf
Stunde 0 — Tokio und New York auf demselben Rasterpunkt. Wer daraus eine
„gleichzeitige" Korrelation macht, misst in Wahrheit vierzehn Stunden Vorsprung
und baut sich Lookahead ein. Die Reihenfolge muss aus der **Börse** kommen, nie
aus dem Zeitstempel.

**Und die zweite Falle darunter: überlappende Handelszeiten.** Xetra schließt
16:30 UTC, die NYSE öffnet 13:30 — drei Stunden gemeinsamer Handel. Die Zeile
Europa → Amerika zeigte 0,197 handelbare Korrelation und sah wie ein Fund aus;
sie ist Gleichzeitigkeit. Nur Asien → Amerika (0 h Überlappung) ist sauber, und
dort steht **−0,010** bei einer Signifikanzschwelle von 0,039. Der Vorlauf
zwischen den Zeitzonen ist echt und groß (0,396 bis 0,703 gegen den
Eröffnungssprung) — und bis zum Eröffnungskurs vollständig eingepreist. Belege
in [docs/WELTBESTAND-UND-ZEITZONEN.md](docs/WELTBESTAND-UND-ZEITZONEN.md).

**Ein Paargewinn setzt ZWEI Positionen voraus.** Trefferquote, Ø Gewinn und
Paargewinn der Kreuzungs-Rangliste gelten für die Differenz: obere Seite kaufen
UND untere leerverkaufen. Wer nur die Kaufseite kauft, bekommt deren eigene
Rendite samt Marktgang und nicht das Gemessene. Ohne Leerverkauf ist die Zeile
eine **Umschichtung** — das ist der Fall, für den die Tausch-Seite gebaut ist.
Der Hinweis steht jetzt auf beiden Seiten; ohne ihn liest sich die Rangliste
wie eine Kaufliste.

**Ein Filter hinter der Mengenbegrenzung kostet das Zwölffache.**
`crossings/chancen` holte die Bewährung für `limit * 12` Paare — je Paar die
gemeinsame Kursreihe über die ganze Historie — und warf danach elf Zwölftel
durch die Symbolgrenze weg. Mit 285 Werten fiel das nicht auf (5 s), mit 593
schon: **65 Sekunden**, und die Oberfläche wirkte eingefroren. Die Grenze hängt
nur an Symbolen und lässt sich vorziehen. Danach 5,2 s.

**Ein laufendes Shell-Skript nicht überschreiben.** `sh` merkt sich einen
Byte-Offset und setzt dort in der NEUEN Datei fort — mitten in einer Zeile.
Beim Umstellen des Trainings führte der alte Lauf Bruchstücke des neuen aus und
kämpfte parallel zum neuen um dieselben Kerne. Zweimal passiert: einmal über
die Datei, einmal, weil ich eine zweite Kette startete, während die erste noch
lief. Vier Trainingsprozesse auf 24 Kernen, und beinahe zwei Schreibvorgänge in
dieselbe Modelldatei.

**Mitgelieferte Dateien nicht über `GetCurrentDirectory() + "../.."` suchen.**
Drei Dienste taten das für Modelle und Quellenlisten. Das stimmt beim
Entwickeln und zeigt in einer Veröffentlichung irgendwohin — die Anwendung
startet dann ohne Modelle und meldet „kein Modell geladen", was auch der
Zustand ist, wenn wirklich keines da ist. Zwei Ursachen, dieselbe Meldung.
`Ablage` sucht der Reihe nach: Umgebungsvariable, neben der Anwendung,
Entwicklungspfad.

**Die Rollentrennung hängt an der HTTP-Methode, nicht an einer Liste.**
„Nutzer dürfen lesen, aber keine Läufe anstoßen" liesse sich auch mit einer
Liste erlaubter Endpunkte umsetzen — die müsste jemand bei jedem neuen
Endpunkt pflegen, und wer einen vergisst, hat ein Loch, das niemandem
auffällt, weil alles funktioniert. GET, HEAD und OPTIONS sind erlaubt, alles
andere nicht; die Regel gilt auch für Endpunkte, die es heute noch nicht gibt.
Ausnahmen sind nur `/api/state` und das eigene Kennwort — beides betrifft
ausschließlich den Anfragenden selbst.

**Ein Lebenszeichen muss offen bleiben.** `/api/health` unter die
Anmeldepflicht zu stellen ließ das eigene Neustartskript die laufende
Anwendung für tot halten — es fragt genau dort nach. Offen ist deshalb
buchstabengetreu `/api/health`; `/api/health/stats` nicht, dort stehen
Bestandszahlen.

**Der erste Verwalter darf nicht der erste Besucher sein.** Eine frisch
ausgelieferte Anwendung hat keinen Benutzer. „Wer sich zuerst meldet, wird
Verwalter" ist auf einem öffentlichen Server genau das Loch, das die Anmeldung
schließen soll. Stattdessen schreibt der Start ein Einrichtungswort ins
Protokoll — wer es lesen kann, hat Zugriff auf den Server.

**Die Maßnahme gegen das Ausspähen von Anmeldenamen verriet sie selbst.** Bei
unbekanntem Namen wurde zum Zeitausgleich `Hashen` gerufen UND geprüft — also
doppelte Arbeit: gemessen 133 ms für einen unbekannten gegen 69 ms für einen
bekannten Namen. Jetzt wird gegen einen einmal vorberechneten Blind-Hash
geprüft, eine einzige PBKDF2-Ableitung wie im echten Fall; danach 98–161 ms
gegen 98–140 ms, also Rauschen.

**Der `AuthService` ist Singleton, nicht Scoped.** Das Einrichtungswort muss
über die Aufrufe hinweg dasselbe bleiben — bei Scoped bekäme jeder Aufruf ein
neues und die Einrichtung wäre unmöglich.

**Die Anmeldung muss VOR dem ersten Datenabruf stehen.** Sonst holt der
Seitenaufbau zwanzig Abfragen, die alle mit 401 antworten, und der Nutzer
sieht ein Feld voller Fehlermeldungen statt eines Anmeldefeldes.
`pruefeAnmeldung()` entscheidet, ob `startUp()` überhaupt läuft.

**Ein gemieteter Endpunkt spricht Basic, nicht Bearer.** Bei vast.ai steht vor
Ollama ein Caddy mit `WWW-Authenticate: Basic realm="restricted"`, Benutzer
`vastai`, Kennwort ist das OPEN_BUTTON_TOKEN. Ein Bearer-Header wird dort mit
401 abgewiesen — und ein 401 liest sich wie „Modell nicht da“, nicht wie
„falsche Anmeldeart“. Unterschieden wird am Doppelpunkt (`OllamaEndpoint.
Anmeldung`): Ein Bearer-Token enthält keinen, ein Anmeldepaar genau einen. Ein
Feld weniger ist ein Feld weniger, das falsch stehen kann. Über den SSH-Tunnel
entfällt das Ganze — er endet hinter dem Caddy, direkt an Ollamas 11434.

**Eine Anmeldung gehört nicht in `DefaultRequestHeaders` eines geteilten
HttpClient.** `EmbeddingClient` und `ReasoningService` bekommen je einen aus dem
Container und benutzen ihn für alle Läufe. Die Kopfzeile dort zu setzen wirkt
auf jede gleichzeitig laufende Anfrage; wechselt jemand während eines
Einbettungslaufs den Endpunkt, geht der Schlüssel des einen an den Server des
anderen. `OllamaAnfrage` baut deshalb je Aufruf ein `HttpRequestMessage`.

**Ein SSH-Schlüssel lässt sich sehr wohl nachreichen** — die aus docuproc
übernommene Behauptung „geht nicht, es hilft nur eine neue Instanz" ist für die
heutige API falsch. `POST /api/v0/instances/{id}/ssh/` mit dem öffentlichen
Schlüssel antwortet `{"success": true, "msg": "SSH key added to instance."}`,
und der Zugang steht Sekunden später. An einer laufenden Instanz erprobt.
Trotzdem gehört der Schlüssel vor der Miete ins Konto, denn ohne ihn ist der
erste Verbindungsversuch immer ein Fehlschlag, den man erst deuten muss.

**Der Schlüssel muss in EINER Zeile stehen.** Ein Umbruch zerlegt den
Base64-Block in zwei Wörter; vast.ai speichert brav vier statt drei Teile, und
sshd verwirft die Zeile. Genau so passiert: Mein Vorschaukasten hat den
Schlüssel umbrochen, der Umbruch wurde mitkopiert, und im Konto stand
`ssh-ed25519 AAAA…iPtl kxJ6…Zro+ docuproc-vastai`. Prüfen heißt zählen:
`len(s.split()) == 3`.

**Der Prüfpfad ist `/api/v0/ssh/`, nicht `v1`.** `/api/v1/ssh/` antwortet mit
404 — und ein 404 sah für mich aus wie „kein Schlüssel hinterlegt". Ich bin
damit eine Stunde nach dem Aufschreiben in genau die Falle getappt, die zwei
Absätze weiter unten steht. Der Pfad für Instanzen ist `v1`, der für Schlüssel
`v0`; sie sind nicht einheitlich.

**Der Sprungrechner von vast.ai ist nicht der verlässliche Weg.** Am 24.08.2026
schloss `ssh2.vast.ai:39384` jede Verbindung sofort („Connection closed before a
valid SSH identification string was received"), während derselbe Container über
`public_ipaddr:16054` — die Aussenabbildung von `22/tcp` — anstandslos
antwortete. `ssh_host`/`ssh_port` aus der API zeigen auf den Sprungrechner;
`ports["22/tcp"]` auf den direkten Weg. `VastInstanz.SshZugang` nimmt deshalb
erst den direkten und den Sprungrechner nur als Rückfall. Nebenbei: Der Port
`machine_dir_ssh_port` führt zum **Wirtsrechner**, nicht in den Container — er
meldet „Permission denied", was wie ein Schlüsselproblem aussieht und keines
ist.

**Ollama steckt im Abbild `vastai/ollama` hinter Port 21434, nicht 11434.**
`PORTAL_CONFIG` meldet `localhost:21434:11434` — der Caddy horcht auf 21434 und
reicht an Ollama weiter. Gemessen: Die Aussenabbildung von 21434 antwortete mit
`401 Basic realm="restricted"`, die von 11434 nahm überhaupt keine Verbindung
an. Wer nur 11434 sucht, findet einen toten Port und hält die Instanz für
kaputt. `OllamaContainerPorts` probiert deshalb 21434 zuerst.

**Die Vorlage lädt selbst ein Modell, und die Platte ist knapp.** `OLLAMA_MODEL`
stand auf `qwen3.5:35b`; beim ersten Blick waren von 42 GB nur noch 20 GB frei,
und `nemotron3:33b` braucht 27,6 GB. Beim Mieten zählt also nicht nur der
Grafikspeicher, sondern auch die Platte — mindestens 60 GB, wenn ein zweites
Modell danebenliegen soll.

**Eine leere Liste ist kein Beweis.** `InstanzenAsync` gibt bei jedem Fehler `[]`
zurück, damit die Endpunktverwaltung ohne vast.ai funktioniert — dieselbe
Antwort wie bei einem Konto ohne Instanzen. Beim ersten Test sah das nach einem
kaputten Aufruf aus. Unterschieden wird an zwei Stellen: der Warnung im
Protokoll und einem direkten Aufruf gegen die API (`total_instances: 0`, HTTP
200). Wer den Unterschied nicht prüft, hält eine stille Fehlfunktion für ein
leeres Konto — oder umgekehrt.

**Der vast.ai-Schlüssel kommt aus `CRS_VASTAI_KEY`, nicht aus
`appsettings.json`.** Die Datei liegt in der Versionsverwaltung und wird mit
ausgeliefert; ein Schlüssel darin wäre in jedem Klon und in jedem
Auslieferungsordner. Die Konfiguration bleibt als zweiter Weg stehen — wer ihn
dort hinterlegen will, soll das können, aber er muss es tun, statt es zu erben.
Dasselbe Problem hat `ConnectionStrings:Sql` bis heute.

**Eine Datei, die geschrieben wird, braucht eine andere Suchreihenfolge als
eine, die nur gelesen wird.** `Ablage.FindeDatei` liefert am Ende den
Entwicklungspfad zurück, auch wenn dort nichts liegt — zum Lesen richtig, zum
Schreiben eine Datei irgendwo neben der Anwendung. `Ablage.OllamaEndpunkte`
entscheidet deshalb am **Verzeichnis**: Existiert das Entwicklungs-`infra`, gilt
es; sonst wird neben der Anwendung geschrieben.

**Eine Ladeanzeige darf ein Ergebnis nicht überschreiben.** `api()` setzte beim
Start „lädt …" und löschte im `finally`. Nach einer erfolgreichen Aktion läuft
aber fast immer sofort ein Nachladen — beim Anlegen eines Benutzers etwa
`ladeBenutzer()`. Dessen `api()` überschrieb „Benutzer angelegt" mit „lädt …"
und löschte danach alles; die Erfolgsmeldung lebte Millisekunden. Nach aussen
sah das aus, als tue die Schaltfläche nichts — der Benutzer WURDE angelegt, nur
sagte es niemand. Gemeldet als „man kann keinen user anlegen, keine reaktion
der ui". Fehler blieben stehen, weil danach nichts nachlädt; **ausgerechnet der
Erfolgsfall war stumm**, und man sucht den Fehler dann dort, wo alles richtig
läuft. `setStatus` merkt sich jetzt die Art der Meldung: Busy verdrängt kein
Ergebnis, Erfolg verschwindet nach sechs Sekunden, Fehler bleiben.

**Nichts in einer Flex-Kopfzeile absolut positionieren.** `.wer` stand auf
`position: absolute`; die Zeile wusste nichts von der Leiste, und das
Statusfeld lief darunter hindurch — eine rote Fehlermeldung überdruckte den
Anmeldenamen, beides unlesbar. Als gewöhnliches Flex-Element mit `order` regelt
die Zeile den Abstand selbst. Dazu `flex-wrap`: Bei 900 px Breite fällt sonst
das Statusfeld aus dem Bild, und es ist das letzte, das das darf.

**Eine Auswahl, die stillschweigend zurückfällt, ist schlimmer als keine.**
`Waehle` setzte den aktiven Ollama-Endpunkt nur im Arbeitsspeicher. Nach jedem
Neustart galt wieder der als Standard markierte, also der lokale — wer eine GPU
gemietet und ausgewählt hatte, rechnete danach wieder auf der CPU, ohne Meldung
und sechsmal langsamer. Aufgefallen ist es nur, weil im Journal als Endpunkt
„Dieser Rechner" stand, wo die GPU stehen sollte: 524 s statt 83 s. Die Auswahl
wird jetzt in die Endpunktdatei geschrieben. `PruefeAsync` schaltet weiterhin
nur vorübergehend um — eine Prüfung soll den aktiven Endpunkt nicht verstellen.

**Jede Reasoning-Antwort wird abgelegt, nicht die auf Knopfdruck gemerkte.** Ein
„Speichern"-Knopf setzte voraus, dass man vor dem Lesen weiss, ob die Antwort
es wert ist — das weiss man nie, und die Rechenzeit ist zu diesem Zeitpunkt
schon verbraucht. `reasoning_log` (Migration 031) schreibt Frage, Antwort,
Modell, Endpunkt, Dauer **und die Werkzeugspur**; ohne sie wäre eine abgelegte
Antwort eine Behauptung. Der Dienst schluckt seine eigenen Fehler: Eine
Antwort, die da ist, darf nicht daran scheitern, dass die Ablage klemmt.
Sichtbar wird dadurch etwas Unbequemes — bei den ersten drei echten Fragen
hatten **zwei** null Werkzeugaufrufe, das Modell hat dort frei geantwortet.

**Ein Schleier über der geladenen Anwendung ist keine Zugangskontrolle.** Der
erste Entwurf lieferte alles aus und legte einen halbdurchsichtigen Kasten
darüber. Die Daten waren dicht — jeder `/api`-Pfad antwortete 401, an der
laufenden Auslieferung nachgemessen. Aber `index.html` (91 KB) und `app.js`
(237 KB) gingen an jeden, der die Adresse kannte, samt aller Erklärtexte und
Messwerte („Sperrbereich" siebenmal, die Fehlerverhältnisse, der ganze Aufbau).
Wer den Schleier im Browser wegräumte, sah die Anwendung. Gemeldet wurde es mit
„nur verdunkelt, aber nicht gesperrt" — zutreffend.

Zwei Fehler steckten darin, und der zweite ist der lehrreichere:

1. **`UseStaticFiles()` stand in `Program.cs` VOR `UseAnmeldepflicht()`.** Die
   Datei-Middleware beantwortet die Anfrage selbst und beendet die Kette; die
   Sperre wurde für `app.js` und `app.css` nie erreicht. Eine Zugangskontrolle
   hinter dem, was sie schützen soll, ist Dekoration.
2. **Der Kommentar an der Sperre begründete den Fehler auch noch:** „Die
   Startseite und die statischen Dateien bleiben offen — sonst könnte niemand
   das Anmeldefeld sehen. Sie enthalten keine Daten." Der erste Halbsatz ist
   falsch: Ein Anmeldefeld braucht keine Anwendung, es braucht eine
   Anmeldeseite. Der zweite ist zu eng: Kein Kurs ist noch keine Harmlosigkeit.
   **Eine Begründung, die man einmal aufgeschrieben hat, wird beim
   Wiederlesen nicht mehr geprüft** — sie war der Grund, warum mir die Lücke
   monatelang nicht auffiel.

Jetzt: `anmeldung.html`, eigenständig, ohne ein Bauteil der Anwendung, und die
Sperre liefert ohne gültige Sitzung ausschliesslich diese aus — mit 200 unter
der angeforderten Adresse, damit man nach dem Anmelden dort landet, wo man hin
wollte. Ohne Cookie messen alle Pfade dieselben 6.118 Bytes; Treffer für
`PILLAR_KEYS`, `Sperrbereich` und `nemotron`: null. Fehlt `anmeldung.html` in
der Auslieferung, wird eine nackte Fehlerseite ausgegeben — eine kaputte
Auslieferung darf nicht zum offenen Zugang führen.

**Das Datenbankkennwort in `appsettings.json` ist eine bewusste Entscheidung,
keine Nachlässigkeit.** Es steht in fünf Stellen des Auslieferungspakets. Von
Hand entfernen trägt nicht: Die Quelle ist `src/Ingest.Api/appsettings.json`,
und jedes `dotnet publish` kopiert sie zurück — genau so ist es nach einer
früheren Bereinigung wieder hineingeraten. Am 25.08.2026 mit dem Betreiber
abgestimmt und so belassen, weil die Datenbank nicht von aussen erreichbar ist.
Wer das ändert, ändert die Repository-Datei, nicht das Paket.

**`bge-m3` ist keine Zusatzausstattung, sondern Laufzeitbedarf.** Die Frage
„wofür braucht die Anwendung Ollama ausser für das Reasoning" hat eine längere
Antwort als erwartet. Das Einbettungsmodell (1,2 GB) wird an drei Stellen
gebraucht, und zwei davon laufen von selbst:

| | wo | wann |
| --- | --- | --- |
| Feeds einbetten | `CronScheduler` → `RefreshFeedsAsync("semantic", 25)` | **stündlich** |
| Frage einbetten | `KnowledgeService.SearchAsync` | bei **jeder** Suche |
| Quellen aufnehmen | Upload und Web-Quellen | bei Bedarf |

Entscheidend ist die zweite Zeile, denn die Suche hängt an mehr als der
Wissensseite: `DayTradingService`, `JournalService` und das `wissen`-Werkzeug
des Agenten rufen sie alle. Ohne `bge-m3` bleiben **Day Trading und
Tagesjournal inhaltsleer** — nicht kaputt, was die Diagnose erschwert. Auf der
CPU ist das billig; Einbetten kostet einen Bruchteil von Texterzeugung.

`nemotron3:33b` (27,6 GB) ist dagegen wirklich nur die Reasoning-Seite, und
`qwen2.5vl` unter `/api/vlm` ist tot — null Treffer in der Oberfläche.

**Bei gemieteten GPUs kostet Stillstand mehr als Arbeit.** Für die RTX 5090 zu
0,4194 $/h gerechnet, angehalten rund 6 % davon (an einer Instanz gemessen:
0,0210 gegen 0,3543 $/h):

| | pro Monat |
| --- | ---: |
| Dauerbetrieb | ~306 $ |
| angehalten, täglich 1 h an | ~30 $ — davon **17 $ nur für die Platte** |
| nach Gebrauch zerstören | ~13 $, dafür 27,6 GB Neuladen je Mal |

Die eigentliche Wahl ist deshalb nicht „dauernd oder täglich", sondern
„angehalten lassen oder zerstören": Das Vorhalten der Platte kostet mehr als
das Rechnen. Wer täglich eine Stunde fragt, zahlt fürs Warten mehr als fürs
Arbeiten. **Und nach jedem Fortsetzen vergibt vast.ai Adresse und SSH-Port
neu** — die Übernahme muss erneut laufen, sonst zeigt der Endpunkt ins Leere.

**Eine gemerkte Auswahl überlebt das Gewählte nicht.** Dass `Waehle` den
Endpunkt in die Datei schreibt, war richtig — aber die gemietete GPU wurde
zerstört, und die Auswahl zeigte weiter auf sie. Danach lief jeder
Einbettungsversuch in einen toten SSH-Tunnel: Die Wissenssuche fand zu keiner
Frage mehr etwas, lokal wie auf dem Server. Die Meldung lautete „Ollama
antwortet nicht oder bge-m3:latest fehlt" — **beides war falsch**, Ollama lief
und das Modell lag bereit. Eine Fehlermeldung, die die zwei naheliegendsten
Ursachen nennt und die tatsächliche verschweigt, kostet mehr Zeit als gar
keine.

Drei Dinge folgen daraus, und alle drei sitzen in `ZugangAsync`:

1. **Rückfall auf den lokalen Endpunkt**, wenn der gewählte nicht erreichbar
   ist. Eine gemietete Instanz verschwindet irgendwann — das ist der Normalfall
   und darf nicht das stündliche Einbetten mitreissen.
2. **Die Auswahl wird NICHT zurückgesetzt.** Eine angehaltene Instanz kommt
   beim Fortsetzen wieder; wer die Wahl automatisch verwürfe, müsste sie jedes
   Mal neu treffen und würde es nicht merken.
3. **Eine Schonfrist von einer Minute.** Ohne sie kostet ein toter Endpunkt
   seine volle Verbindungsfrist *pro Einbettung* — gemessen 25 s bei jedem
   Aufruf, ein Stapel über hundert Abschnitte stünde still, obwohl der Rückfall
   greift. Dazu die SSH-Frist von 30 auf 8 Sekunden. Danach: erster Aufruf
   10,3 s, jeder weitere 0,2 s.

**Der Rückfall muss sichtbar sein.** `/api/ollama/endpunkte` meldet ihn unter
`stoerung`. Ein stiller Rückfall wäre nur eine andere Art, das Problem zu
verstecken — die Anlage rechnete wochenlang lokal, während jemand die gemietete
Karte bezahlt.

**Eine Auslieferung darf keine Endpunktdatei mitbringen.** Auf dem Produktivsystem
stand nach dem Ausrollen dieselbe zerstörte GPU als aktiver Endpunkt, weil die
Datei mitkopiert wurde. Ein Rechner erbt damit die Gerätewahl eines anderen.

**„Alles einbetten" muss trennen, was nie eine Chance hatte.** Von 504 nie
eingebetteten Semantik-Quellen stehen **469 auf „kein Text gefunden"** — dort
scheiterte die Textgewinnung, nicht das Einbetten. Ein erneuter Versuch lädt
dieselbe Seite und scheitert genauso; gemessen an einem Lauf über 205 davon kam
**genau eine** durch. Der Umfang `Fehlend` lässt sie deshalb aus, `AuchLeere`
nimmt sie mit — das lohnt erst nach einer Änderung an der Textgewinnung, wie
damals beim Absatzfilter, der von zwölf Büchern elf verwarf.

Drei weitere Punkte, die der Knopf mitbringen musste:

- **Der Lauf gehört in den Server, nicht in den Browser.** Der Vorgänger
  schickte eine Anfrage je Quelle und lud dazwischen **jedes Mal die ganze
  Quellenliste neu** — bei 504 offenen Quellen über tausend Anfragen, und mit
  dem Tab war der Fortschritt weg.
- **„Ohne Text" ist kein Fehler des Laufs, sondern ein Befund über die Quelle.**
  Getrennt gezählt; sonst liest sich ein sauberer Lauf wie ein kaputter.
- **Sequenziell, nicht nebenläufig.** Eingebettet wird gegen ein einziges
  Ollama; zehn gleichzeitige Quellen teilen sich dieselbe Rechenzeit, machen
  aber den Fortschritt unleserlich und den Abbruch wertlos.

**Nicht als gemessen aufschreiben, was nur vermutet ist.** Hier stand, Dapper
fülle keine ValueTuples und der Sammellauf wäre über lauter „Quelle 0" gelaufen.
Das war eine Vermutung: Ich hatte den benannten Typ vorsorglich genommen, ohne
den Tupel je scheitern zu sehen. `KnowledgeService` benutzt seit jeher
`QueryAsync<(int SourceId, string Origin, string Title, string? Region)>` für die
fälligen Feeds, und die werden geholt — Dapper ordnet Tupel also positionsweise
zu. Ein benannter Typ mit `AS`-Aliasen ist trotzdem die bessere Wahl, weil er
nicht an der Spaltenreihenfolge hängt; aber das ist Geschmack, kein Befund.

Die Lehre ist die allgemeinere: **Ein „gemessen" in dieser Datei muss eine
Messung hinter sich haben.** Sonst ist sie nach ein paar Monaten eine Sammlung
plausibler Behauptungen, und niemand weiss mehr, welche davon geprüft sind.

**`System.Threading.Lock` gibt es erst ab .NET 9.** Dieses Projekt steht auf 8;
dort ist es ein gewöhnliches `object`.

**Eine Spalte muss zeigen, was ihre Überschrift verspricht.** Im Quellenraster
hiess die vierte Spalte bei der Semantik „zuletzt geholt" und beim Wissen
„eingebettet" — beide rendern dieselbe Zelle, nämlich `indexed_utc`. Für einen
Feed ist die systematisch leer: Ein Feed wird nie eingebettet, er erzeugt
Artikel. **28 von 35 Feeds** zeigten deshalb „–", obwohl jeder stündlich geholt
wird und `last_checked_utc` bei ausnahmslos allen gesetzt war. Die Spalte
behauptete „nie geholt", wo „läuft, wird aber nie eingebettet" richtig gewesen
wäre — gemeldet als „warum steht da nichts". Das gelieferte Feld
`zuletztGeprueft` wurde nirgends angezeigt. Jetzt stehen beide Tatsachen
untereinander in einer Spalte mit der Überschrift „geholt / eingebettet".

**Dass gelernt wird, ist nicht dasselbe wie besser werden.** Die Rückkopplung
der ersten Säule läuft nachweislich: `ScoringService` ruft
`Ensemble.UpdateWeights` je bewerteter Prognose, und die Gewichte folgen der
Trefferquote — bei Horizont 24 über 3,07 Mio. Beobachtungen `meanrev` 0,386
(Trefferquote 0,530), `momentum` 0,312 (0,500), `drift` 0,193 (0,463), `naive`
und `leadlag` je unter 0,06. Je Wert eigene Gewichte, von 0,0097 bis 0,961.

Daraus folgt aber **nicht**, dass die Prognose besser wird: Ein Verfahren kann
fleissig lernen und auf der Stelle treten, weil das Signal nicht da ist. Das
entscheidet allein die Lernkurve (`/api/guete/lernkurve`) — Median des
Fehlerverhältnisses je Zieltag. Sie wird erst ab sieben Tagen mit einem Trend
beschriftet; aus drei Punkten eine Steigung zu rechnen wäre Kaffeesatz, der wie
ein Befund aussieht.

**Die Prognosegüte nur aus Live-Prognosen.** `forecast_track` bleibt draussen —
der Walk-Forward wurde nachträglich über bekannte Kurse gerechnet und belegt
nichts. Die Trennung ist der ganze Sinn der Auswertung.

**Je Wert und Zieltag genau eine Beobachtung.** Prognosen entstehen stündlich,
der Zielbar nicht. Bei einem Anleihen-ETF steht der Kurs zwischen den Stunden
still: Vier Läufe sahen dieselbe Basis (52,110), stellten dieselbe Prognose
(52,136) und trafen denselben Ist-Wert (52,140). Als vier Fälle gezählt meldete
die erste Fassung der Rangliste **100 % der Werte schlagen den Stillstand** bei
Trefferquoten um 100 %. Nach der Entdopplung: **42,4 %** und Median 1,022 — der
typische Wert ist also etwas schlechter als Stillhalten. Eine Auswertung, die
das Gegenteil behauptet, ist nicht optimistisch, sondern falsch gezählt.

**Sortiert wird nach dem Fehlerverhältnis, nie nach dem Fehler.** Ein mittlerer
Fehler von 1,2 % sagt nichts, wenn sich der Kurs ohnehin nur um 0,9 % bewegt
hat. Nach dem rohen Fehler zu sortieren spült zuverlässig die ruhigsten Werte
nach oben, nicht die am besten getroffenen.

**Der Horizontsatz ist [24, 168, 336, 720, 2160, 4380, 8760].** Die
Stundenhorizonte 1 und 4 sind draussen — sie erzeugten bei ruhigen Werten
identische Zeilen und täuschten Fallzahl vor. 72 (3 Tage) ebenfalls; dafür neu
336 (2 Wochen), die grösste Lücke im Satz. Prognostiziert wird immer vom
jüngsten Kurs aus über alle Horizonte; die Prognose-Ansicht rechnet beim Öffnen
nach, wenn ein Bar dazugekommen ist (`/api/forecast/stand`, `/auffrischen` —
gemessen 606 Werte, 3.985 Prognosen, 13,6 s). **Die alten Zeilen bleiben
stehen**: Was gestern für heute geschätzt wurde, muss heute noch dastehen, sonst
lässt sich nichts nachprüfen.

**Ein Horizont ohne Zeilen ist kein Datenverlust.** Eine Jahresprognose ist
frühestens nach einem Jahr nachprüfbar. Die Übersicht schreibt das an jede leere
Zeile — ohne den Satz sucht man einen Fehler, den es nicht gibt. Genau so kam
die Meldung „zwischen 21. und 25.08. fehlen die Prognosedaten": Der
Walk-Forward endet bei Zieldatum 21.08. für **jeden** Horizont, und für längere
Horizonte gibt es noch keine gereiften Live-Prognosen.

**`PERCENTILE_CONT` ist eine Fensterfunktion, keine Aggregation.** Sie liefert
je Zeile denselben Wert und lässt sich nicht in `MIN()` schachteln — SQL
antwortet mit „Windowed functions cannot be used in the context of another
windowed function or aggregate". Median und Aggregat gehören in getrennte CTEs.

**Alle Säulen fliessen in die Prognose ein — aber keine ohne gemessenen
Rückhalt.** Das ist der Kern der Säulenmischung, und er hält beide Ansprüche
zusammen: „Arbeite das gesamte Wissen ein" und „erfinde kein Signal". Der Regler
in der Oberfläche bestimmt, WIE VIEL von etwas Brauchbarem einfliesst; ob etwas
brauchbar ist, entscheidet allein die Messung in `pillar_skill`. Eine Säule mit
Verdienst null bewegt nichts, auch bei Regler auf hundert.

**Grundlage oder Aufschlag — die Unterscheidung ist nicht Zierde.** Der erste
Entwurf behandelte alle Säulen als gleichrangige Prognostiker und mittelte sie
gewichtet. Bei FCX schätzte Säule 1 −15,7 % über einen Monat, die Wissenssäule
+0,25 % aus vier gemessenen Mustern — und weil deren Verdienst höher war, bekam
die Zahl von einem Viertelprozent **72 % Gewicht** und zog die Prognose auf
−4,1 %. Ein Musterüberschuss ist aber keine Kursprognose, sondern eine Aussage
über das, was ZUSÄTZLICH zum üblichen Gang zu erwarten ist. Seither:
*Grundlagen* (learning, math) werden gewichtet gemittelt, *Aufschläge*
(knowledge, semantic) kommen obendrauf und sind auf den Betrag der Grundlage
gedeckelt. Danach: höchste Abweichung 0,669 % statt 15,94 %.

**Nicht weiter behaupten, als geprüft wurde.** Die Spektralsäule misst ihren
Rückhalt über 40 Bars. In der ersten Fassung schrieb sie trotzdem 90 Tage fort —
für ENA-USD +33,4 %, gestützt auf einen Vorsprung, der über vierzig Tage
gemessen war; die Mischung zog die Prognose damit um 42 Prozentpunkte.
`RenditeNach` gibt jenseits des geprüften Abschnitts jetzt nichts zurück, und
ein zweiter Riegel verwirft Fortschreibungen jenseits des Dreifachen der eigenen
üblichen Schwankung. Sichtbare Folge: Über 3 Monate, 6 Monate und 1 Jahr trägt
ausser Säule 1 nichts bei — und das ist richtig so.

**Ein Rückfallwert ist keine Messung.** Der Verdienst von Säule 1 kam zuerst aus
der Zuversicht des Ensembles, mit 0,25 als Rückfall, sobald sie unter der
Schwelle lag. Das ist fast immer der Fall und machte Säule 1 grundsätzlich
schwach: Jede andere Säule mit bescheidenem Vorsprung bekam die Hälfte des
Gewichts. Jetzt zählt die gemessene Trefferquote der bereits bewerteten
Prognosen dieses Wertes und Horizonts; der Rückfall gilt nur unter zwanzig
Fällen.

**Die Nachrichtenstimmung ist verdrahtet und trägt null — gemessen.** Über
`Nachrichtenstimmung` (Wörterbuch deutsch/englisch, Verneinung über drei Wörter
zurück, Halbwertszeit ein Tag) entsteht je Wert eine Zahl zwischen −1 und +1.
Was daraus für eine Rendite folgt, bestimmt allein die Kalibrierung gegen
eingetroffene Kurse. Ergebnis am 25.08.2026: **1 Tag −0,0178 bei Schwelle
0,0517; 1 Woche −0,0587 bei 0,0763; 2 Wochen −0,0536 bei 0,0884; 1 Monat
−0,0835 bei 0,1109.** Alle unter der Zufallsschwelle, alle negativ — die
Stimmung läuft der Folgerendite eher entgegen, als ihr voraus. Deckt sich mit
GDELT (0,0419 prognostisch gegen 0,077). Die Säule wird erkannt, angezeigt und
trägt nichts bei; genau das soll die Anzeige zeigen.

**Die Wissenssäule ist die gemessene Bot-Musterspur.** Von allem, worüber die
Literatur schreibt, sind die Auslöser aus `bot_trigger_stat` das Einzige, was am
Kurs geprüft ist: je Muster der Median-Ertrag nach 1, 5 und 20 Tagen gegen eine
Grundlinie, über tausende Ereignisse. Die Erkennung benutzt **dieselbe** SQL-
Bedingung wie die Messung (`get_active_triggers` spiegelt 027 wörtlich) — eine
zweite Umsetzung in C# liefe unweigerlich auseinander, und der Verdienst gehörte
dann zu einem anderen Signal als dem gemeldeten. Der Verdienst hängt an der
Richtungstrefferquote, nicht am Überschuss: Ein Auslöser mit grossem Überschuss
und Trefferquote 0,40 beschreibt seltene Ausschläge, nicht Vorhersagbarkeit.
Dubletten je (Wert, Auslöser) werden entfernt — ein Bollinger-Ausbruch feuert
gern an zwei Tagen hintereinander und ginge sonst doppelt ein.

**`predicted_close` und `combined_close` stehen nebeneinander, und das muss so
bleiben.** An `predicted_close` hängt die Rückkopplung: `ScoringService`
verschiebt danach die Gewichte der fünf Teilmodelle. Stünde dort die gemischte
Zahl, lernte Säule 1 aus einem Fehler, den sie nicht gemacht hat — die
Komponenten erklärten die Vorhersage nicht mehr. Beide werden getrennt bewertet
(`forecast_score` und `forecast_score_combined`); nur dadurch lässt sich
überhaupt sagen, ob das Mischen etwas bringt (`/api/guete/mischvergleich`).

**Wer Kurse holt, schätzt danach neu.** Der Tageslauf tat es nicht: Er holte die
Tagesbars, rechnete die Analyse, bewertete die fälligen Prognosen — und liess
die Schätzung auf dem Stand des letzten Stundenlaufs. Für Werte mit Tagesbars
und ohne Stundendaten hiess das: neuer Kurs, alte Prognose.

**Dapper nimmt ValueTuples als Ergebnis, aber nicht als Parameter.** Als
Rückgabetyp ordnet es sie positionsweise zu; als Parameterobjekt weist es sie
ausdrücklich zurück — „the language-level names are not available to use as
parameter names". Die Namen aus dem Quelltext gibt es zur Laufzeit nicht. Ein
anonymes Objekt löst es. (Damit ist die frühere Notiz in dieser Datei endgültig
richtig: Ergebnis ja, Parameter nein.)

**Eine Prozedur, die Spalten liefert, muss neue Spalten auch liefern.**
`get_due_forecasts` zählt die Spalten einzeln auf. Nach dem Anlegen von
`combined_close` stand die Spalte beim Bewerten auf NULL, und die Mischung wäre
nie bewertet worden — stillschweigend. Die Prognose sähe vollständig aus, die
Auswertung „bringt das Mischen etwas?" bliebe für immer leer, und man suchte den
Fehler in der Auswertung.

**Auch `forecast` enthält die Rückrechnung, nicht nur `forecast_track`.** Die
Regel „Prognosegüte nur aus Live-Prognosen" stand hier — für die falsche
Tabelle. Das Aufrollen der Vergangenheit hat in `forecast` selbst rückdatierte
Zeilen über bereits bekannte Kurse geschrieben. Sichtbar wird es an der
Tagesmenge und an der Trefferquote: bis 20.08. je Tag 200–830 Zeilen mit 0,49
bis 0,73, ab 21.08. dann 1.296–5.615 Zeilen mit 0,466 bis 0,593. Die erste
Rangliste des Autopiloten meldete daraufhin **11 von 15 Werten mit positivem
Erwartungswert** und Trefferquoten bis 0,727 — ein Depot danach auszurichten
hiesse, auf eine Rückrechnung mit bekanntem Ausgang zu setzen. Unterschieden
wird an `model_version`: `ens-1-bt` gegen `ens-1`. **Ein hartkodierter Stichtag
wäre die schlechtere Lösung** — er würde beim nächsten Rückroll-Lauf still
falsch, ohne dass jemand etwas merkt.

**Die beste Trefferquote aus vielen Werten ist die glücklichste, nicht die
beste.** Über 571 Werte mit im Mittel vier bewerteten Live-Prognosen streuen die
Quoten mit 0,285 — reines Würfeln erzeugte bei dieser Fallzahl schon 0,250. Die
Unterschiede zwischen den Werten sind also weit überwiegend Rauschen, und wer
den Höchstwert auswählt, wählt Zufall und nennt es Auswahl. Dieselbe Falle wie
beim Umkehrschluss (291 gefundene Auffälligkeiten gegen 653 vom Zufall
erwartete). Gegenmittel ist die Schrumpfung zum Bestandsmittel, und **der Faktor
wird aus der Zerlegung der Streuung selbst bestimmt, nicht gesetzt**: Was nach
Abzug des Rauschens übrig bleibt, darf durchschlagen. Er verschwindet von
allein, sobald die Fallzahl wächst — es braucht keine Regel, die man später
zurückdrehen muss. Wirkung: NOC von roh 0,800 auf 0,539, MNST von 0,200 auf
0,425.

**Ein Rückfall auf 0,5 ist keine Vorsicht, sondern eine Behauptung.** Wo nichts
gemessen ist, steht `null` und wird nicht gehandelt — nicht „Münzwurf".

**Die Gebühr gehört in den Nenner der Positionsgrösse.** Acht Positionen zu
einem Achtel des Vermögens ergeben zusammen genau das Vermögen; die acht
Gebühren kommen obendrauf. Die achte Buchung scheiterte deshalb zuverlässig mit
„es fehlen 150,00" — exakt 8 × 18,75. Richtig ist
`Vermögen / (Anzahl × (1 + Gebührensatz))`.

**`Close` ist als Spaltenalias ein reserviertes Wort.** `[close] AS Close`
scheitert mit „Incorrect syntax near the keyword 'Close'" — zur Laufzeit, nicht
beim Übersetzen, und die Meldung nennt den Alias, nicht die Abfrage.

**Dapper füllt keinen NULLBAREN record struct.** `QuerySingleOrDefaultAsync<T?>`
liefert für einen `record struct` kommentarlos `null`, statt die Spalten zu
füllen — kein Fehler, keine Meldung. Sichtbar wurde es an „für NVDA liegt kein
Tagesschluss vor", während die Zeile daneben den Kurs zeigte. An der zweiten
Stelle wäre es nicht aufgefallen: Eine bestehende Position hätte immer als leer
gegolten, jede Anpassung also verdoppelt statt angepasst.

**Dieses System führt sehr wohl Devisenreihen.** Ich habe an mehreren Stellen
das Gegenteil geschrieben, ungeprüft. `EURUSD=X` liegt mit 5.899 Tagesbars
verfolgt im Bestand, dazu GBP, CHF und JPY. Damit ist ein Gesamtvermögen über
EUR und USD möglich. Umgerechnet wird mit dem Kurs **jenes Tages**, nicht dem
von heute: Ein historischer Verlauf zum heutigen Wechselkurs behauptet, das Geld
hätte damals anders gestanden, als es dastand — der Fehler ist am Ende der Reihe
null und wächst nach hinten, sieht also aus wie ein Trend.

**Ein Vermögensverlauf ohne den Kontostand ist falsch.** Nach einem Verkauf
steckt das Geld nicht mehr im Kurs, sondern im Konto; eine Kurve nur über die
Positionen stellte den Verkauf als Absturz dar. Genau deshalb ist auch das Konto
ein Journal und keine gespeicherte Zahl — aus einer Zahl liesse sich der
gestrige Stand nicht zurückrechnen.

**Fortschreiben ist hier richtig, Schneiden wäre falsch.** Die Regel „über
mehrere Werte nur auf gemeinsamen Handelszeitpunkten" schützt *Messungen von
Zusammenhängen* davor, den Kalender statt den Markt zu messen. Ein
Vermögensverlauf ist keine Messung, sondern eine Addition: Die Aktie ist am
Samstag nicht wertlos, sie wird nur nicht gehandelt. Wer hier schnitte, bekäme
ein Depot, das an jedem Wochenende auf den Kryptoanteil zusammenfiele. Was aus
so einer Reihe **nicht** werden darf, ist eine Statistik über Tagesrenditen —
die fortgeschriebenen Tage sind keine Beobachtungen.

**Eine Grundlinie muss dieselben Kosten tragen wie das, was sie misst.** Der
Autopilot vergleicht gegen „einmal gekauft, nie wieder angefasst". Wäre diese
Grundlinie gebührenfrei, wäre sie unschlagbar und als Vergleich wertlos.

**Ein fehlendes Urteil ist keine Ablehnung.** Antwortet Nemotron nicht oder ohne
verwertbares JSON, wird gehandelt und `kein Urteil` vermerkt — eine eigene
Spalte, die sich von „zugestimmt" unterscheidet. Sonst entschiede die
Verfügbarkeit einer gemieteten Grafikkarte über das Depot. Gemessen am ersten
Lauf: Das Modell antwortete tatsächlich ohne JSON, und der Lauf ging weiter.

**Auch die abgelehnten Werte gehören ins Protokoll — mit der Zahl, an der es
scheiterte.** Ein Autopilot, der nur seine Geschäfte aufschreibt, lässt sich
nicht prüfen: Man sieht, was er getan hat, und nie, was er erwogen und verworfen
hat. Beim strengen Depot ist das Ausbleiben von Geschäften sogar das eigentliche
Ergebnis; ohne eine Zeile je Fall sähe ein Depot, das nichts tut, genauso aus
wie eines, das nicht läuft.

**Ein Bestand ohne Buchung ist nicht ein Bestand im Wert von null.** Ein Wert
ohne Position zeigt „–", nicht „0,00" — sonst liest sich die Zeile wie eine
Position, die alles verloren hat. Eine aufgelöste Position dagegen steht
berechtigt bei null: Dort sind die Anteile weg, aber der Ertrag der Rundreise
steht noch im Gewinn.

**Eine Spalte muss heissen, was sie rechnet.** „Einstand" fiel nach einer
Entnahme von 200,85 auf 191,34 — zu diesem Kurs hat nie ein Kauf stattgefunden.
Ein Einstandskurs ändert sich beim Verkauf gar nicht; die Zahl ist der noch
investierte Betrag je Anteil und heisst deshalb „Nulllinie".

**`fmtNum` taugt nicht als Wert für `<input type="number">`.** Die deutsche
Schreibweise („5.000,00") wird vom Feld verworfen, und die Vorbelegung ist
stillschweigend weg — genau die Vorbelegung, die den ganzen Zweck ausmacht.

**Gegenrechnungen dürfen nicht addiert werden.** Das Gesamtvermögensband
summierte alle vier Depots und meldete 3.422,79 EUR, wo 2.000 USD richtig
waren. Die drei Autopilot-Strategien sind aber keine drei Geldtöpfe, sondern
drei Antworten auf dieselbe Frage — was aus DEMSELBEN Budget geworden wäre, wenn
man so oder so vorgegangen wäre. Sie zu addieren zählt dasselbe Geld dreimal;
formal eine Summe, inhaltlich dieselbe Scheinmenge wie bei den stündlich
wiederholten Prognosen auf denselben Zielbar. Genau eine Strategie trägt
`zaehlt = 1`. Das Budget zu dritteln hätte die Summe ebenfalls geradegezogen und
wäre trotzdem falsch gewesen: Die drei blieben Gegenrechnungen, nur kleinere.

**Ein Bestand wird auf der jüngsten Bar bewertet, nicht auf dem letzten
Tagesschluss.** Das Depot rechnete ausschliesslich mit `interval_code = '1d'`.
Gemessen am 26.08.2026 um 14:45 UTC: Tagesbars bis 25.08. 00:00, Stundenbars bis
26.08. 14:00 — der angezeigte Wert war **38 Stunden alt** und bewegte sich
zwischen zwei Tagesläufen überhaupt nicht, obwohl stündlich frische Kurse
hereinkamen. Wer zusah, hielt das Depot für eingefroren.

**Handeln und Bewerten sind zwei verschiedene Takte.** Der Autopilot bucht
täglich; der Wert dessen, was er hält, ändert sich stündlich. Die beiden
zusammenzuwerfen war der eigentliche Denkfehler dahinter.

**Und der letzte Kurs braucht EINE Definition.** Er wird an vier Stellen
gebraucht: angezeigter Positionswert, Buchungskurs, Handelbarkeit in der
Rangfolge, letzter Punkt der Vermögenskurve. Liefen sie auseinander, zeigte die
Tabelle einen Preis und die Buchung benutzte einen anderen. Deshalb
`dbo.letzter_kurs(@asset_id)` — dieselbe Überlegung wie bei
`get_active_triggers`, das die SQL-Bedingung der Messung wörtlich spiegelt,
statt sie in C# nachzubauen. Nur Stundenbars zu nehmen geht nicht: 34 verfolgte
Werte haben keine.

**Ein einzelner Punkt ist kein Diagramm.** uPlot kann aus einem x-Wert keinen
Bereich ableiten und spannte die Achse über Oktober 2026 bis April 2029 für
einen Punkt von heute. Das sieht nach kaputten Daten aus und ist bloss ein
fehlender zweiter Tag — unter zwei Punkten gehört ein Satz hin, kein Diagramm.

**Die Zeigerform ist eine Aussage, keine Verzierung.** Das Zoom-Plugin setzte
`cursor: grab` über die ganze Diagrammfläche — also auch dann, wenn gar nichts
zu ziehen war. Die Hand sagt „hier wird verschoben" und verdeckt, dass dieselbe
Fläche vor allem zum Ablesen da ist; gemeldet wurde es als „dadurch haben wir
kein mouse hover mehr". Der Hover funktionierte tatsächlich die ganze Zeit —
Fadenkreuz, Punkte und Legendenwerte waren alle da. Die falsche Form allein
liess ihn für kaputt halten. Jetzt `crosshair` in Ruhe und `grabbing` nur
während des Ziehens.

**Werte gehören dorthin, wo der Zeiger steht.** uPlots Legende steht unter dem
Diagramm und führt ALLE Reihen auf; bei fünf Werten mit Prognose und Rückschau
sind das fünfzehn Einträge in drei Zeilen. Wer wissen will, was die Linie unter
dem Zeiger wert ist, sucht sie dort erst — der Blick geht vom Kurs weg, und beim
Zurückschauen ist der Zeiger woanders. `hoverPlugin` zeigt die Werte am Zeiger,
**nach senkrechtem Abstand sortiert**, die nächste fett: Bei sechzehn Reihen
wäre eine unsortierte Liste eine Wand aus Zahlen, in der die gesuchte nicht
auffällt.

**Ein Kasten, der dem Zeiger folgt, braucht `pointer-events: none`.** Sonst
fängt er die Maus ab, sobald er sie berührt: uPlot bekommt kein mousemove mehr,
der Kasten verschwindet, der Zeiger ist wieder frei — und das Ganze flackert im
Kreis.

**Eine CSS-Regel für ein neues Bauteil darf nicht global greifen.** Für den
Umschalter der Depotkurve kam `.chart-title { display: flex }` dazu — und legte
damit JEDEN Diagrammtitel der Anwendung auf Flex um. Eingegrenzt auf
`.chart-title:has(.depot-schalter)`.

**Synthetische Mausereignisse im Prüf-Browser können den Tab einfrieren.** Ein
`dispatchEvent(new MouseEvent('mousemove'))` auf das uPlot-Overlay legte den
Renderer lahm, und zwar über Neuladen hinweg, bis der Tab geschlossen war. Das
sah aus wie ein Fehler der Anwendung und war einer des Prüfvorgehens: Ein
frischer Tab zeigte dieselbe Seite tadellos. **Zum Prüfen echte Zeigerbewegungen
benutzen, keine erzeugten** — und: Bewegt sich der Zeiger auf dieselbe Stelle,
entsteht gar kein Ereignis, was leicht als „reagiert nicht" fehlgedeutet wird.

**Die Datenbank speichert UTC, das Protokoll schreibt Ortszeit.** Bei zwei
Stunden Versatz sucht man sonst einen Lauf zu einer Zeit, zu der der Rechner
nachweislich aus war — die vier Geschäfte des 27.08. stehen als `06:00:51` in
`invest_buchung` und als `08:00:51` im Protokoll. Dieselbe Sekunde.

**Ein Zeitplan läuft nicht, während der Rechner schläft — er holt nach.** Der
Tageslauf um 02:20 UTC fand nicht statt; der Rechner wachte um 07:52 Ortszeit
auf, und um 08:00:47 waren die Prognosen fertig, um 08:00:49 lief der Autopilot.
„Täglich" heisst auf einer Arbeitsmaschine also „täglich, sobald sie das nächste
Mal läuft".

**Wer im Protokoll steht, muss darin richtig stehen.** Der Autopilot schrieb für
jeden gehaltenen Wert, der im Zielkorb blieb, „nicht unter den besten N" — auch
für den mit der HÖCHSTEN Erwartung des Laufs (MNST, 7,226 %), der in Wahrheit
gehalten wurde. Ein Protokoll, das den bestbewerteten Wert als verworfen meldet,
widerlegt sich selbst; wer es liest, traut danach auch dem Rest nicht mehr. Der
Fehler entstand durch ein `continue` in der Verkaufsschleife: Der Wert fiel
durch bis in die Sammelschleife am Ende, die alles Unbehandelte als abgelehnt
ablegt. **Ein `continue` überspringt nicht nur die Arbeit, sondern auch das
Aufschreiben.**

**Die Kohorte der Neuzugänge gibt es jetzt — und sie wächst nur vorwärts.**
Die alte Notiz „wer Neulinge auswerten will, braucht die vollständige Kohorte
aus Listing-Ankündigungen" ist umgesetzt: `dbo.neuzugang` sammelt aus dem
Nasdaq-IPO-Kalender und Binances Katalog „New Cryptocurrency Listing".
Eingetragen wird bei der ANKÜNDIGUNG, nicht wenn etwas auffällt; was nichts
wird, bleibt als `ausgefallen` stehen statt zu verschwinden. Rückwirkend füllen
liesse sie sich nur aus dem eigenen Bestand — und damit hätte man den
Überlebensirrtum wieder eingebaut. **Ein verpasster Tag ist eine Lücke, die
bleibt**; deshalb läuft der Sammler im Tageslauf vor dem Autopiloten. Erster
gemessener Vorlauf: **9,0 Tage** zwischen Ankündigung und erwartetem Start.

**Dapper kennt `DateOnly` weder als Parameter noch als Ergebnis.** Als Parameter
„cannot be used as a parameter value", beim Lesen verlangt es einen Konstruktor
mit `DateTime`. Dieselbe Klasse Fehler wie bei den ValueTuples: Der Typ, der im
Modell der richtige wäre, ist es an der Schnittstelle zur Datenbank nicht.

**Binance erlaubt nur bestimmte Seitengrössen.** Gemessen antworten `pageSize`
5, 10, 20 und 50 mit 200 — **30 dagegen mit 400**. Eine Zahl dazwischen zu
wählen sieht harmlos aus und scheitert stumm.

**Ein Ticker hat keinen Bindestrich.** Die Erkennung „kurz und ohne Leerzeichen"
griff das Datum am Ende von Binance-Titeln ab; Ankündigungen standen mit der
Kennung `2026-08-30` in der Tabelle. Richtig ist: nur Grossbuchstaben und
Ziffern, zwei bis zwölf Zeichen.

**Ein zusätzlicher Reiter kann die Kopfzeile sprengen.** `.tabs` war
`display: flex` ohne Umbruch. Mit dem elften Reiter wurde die Leiste bei 830 px
Sichtbreite 882 px lang, schob den ganzen Rumpf waagrecht und liess die
Kopfzeile aus dem Bild wandern — es sah nach einem Fehler der neuen Seite aus
und war einer der Navigationsleiste. **Eine waagrecht unbegrenzte Leiste ist
eine Zeitbombe: Sie fällt erst auf, wenn ein Eintrag zuviel dazukommt.**

**`min-width: 0` gehört auf JEDE Ebene zwischen Raster und Rollbereich.** Ein
`overflow-x: auto` allein genügt nicht — ein Raster- oder Flex-Kind bekommt
`min-width: auto` und wächst mit seinem Inhalt. Dieselbe Lösung wie bei
`.chart-box`, aus demselben Grund.

**Eine Grundlinie, die nicht handelt, kann den Informationsgehalt eines Signals
nicht messen.** `halten` misst gegen den Markt, traegt aber ein voellig anderes
Kostenprofil. Die vierte Strategie `invers` haelt deshalb die
SCHLECHTBEWERTETEN Werte: gleiche Anzahl, gleiche Gebuehren, gleicher Takt,
gedrehtes Vorzeichen. `aktiv` minus `invers` ist damit der Informationsgehalt
des Signals, bereinigt um Marktgang UND Kosten. Sie ist keine Gewinnhoffnung —
0,505 umgedreht ist 0,495 bei gleichen Kosten —, sondern die fehlende
Gegenkontrolle. **Eine Kontrolle muss dieselben Parameter tragen wie das, was
sie kontrolliert**: `invers` erbt Werteanzahl, Hoechstanteil und Hysterese von
`aktiv`, sonst unterscheiden sich beide in zwei Dingen und der Unterschied
gehoert keinem.

**Ein Strategiename stand an vier Stellen als feste Aufzaehlung** —
`reset_autopilot_depot`, `set_leitstrategie`, die Laufliste des Endpunkts und
`VergleichAsync`. Die beiden Prozeduren meldeten sich laut, die
Vergleichstabelle gar nicht: `invers` handelte, und die Zeile fehlte einfach.
**Eine Liste, die stillschweigend unvollstaendig ist, ist die schlimmere
Sorte** — dieselbe Lehre wie bei den erlaubten Endpunkten und beim
Sprachverzeichnis.

**Serverseitig erzeugte Texte brauchen keinen Umbau, um uebersetzbar zu
sein.** Ich hatte in docs/SPRACHEN.md geschrieben, die 144 Journal-Vorlagen
muessten dafuer auf Platzhalter-Aufrufe umgestellt werden. Falsch: Diese Texte
landen als Daten im DOM, die Uebersetzungsschicht sieht sie also laengst. Es
fehlten nur die Eintraege im Katalog. Geerntet werden die STUECKE ZWISCHEN den
Interpolationsloechern — 1.070 Texte, 1.050 uebersetzt, kein einziger geaenderter
C#-Aufruf. Ausgenommen bleiben Systemprompt, Stimmungswoerterbuch und
Protokollzeilen; das Stimmungswoerterbuch zu uebersetzen machte die Saeule kaputt.

**Eine Wortgrenze um die ganze Alternation ist keine Wortgrenze.** Sie muss JE
SCHLUESSEL stehen. Aussen herum geklammert fand kein Schluessel, der mit einem
Satzzeichen beginnt, je seinen Text: „, umgerechnet ueber …" steht im DOM hinter
dem „D" von USD, dort KANN es keine Wortgrenze geben. Betroffen war der ganze
serverseitige Teil, denn dessen Bruchstuecke stehen zwischen zwei Werten.

**Eine Regex-Alternation waechst nicht linear.** Gemessen an 1.859 Textknoten:
mehrwortige Schluessel (~1.200 Zweige) 24 ms je Durchgang; alle Schluessel ab
fuenf Zeichen (~2.270 Zweige) kamen binnen 45 Sekunden nicht zum Ende. Jeder
Zweig traegt einen Rueckblick, und die Maschine probiert an jeder Stelle jeden
Zweig. Der Preis der schnellen Variante ist benannt: Beschriftungen aus einem
Wort plus Zahl bleiben deutsch.

**`requestAnimationFrame` feuert im Hintergrund-Tab nicht.** Die
Uebersetzungsschicht sammelt darueber ihre Durchgaenge; in einem unsichtbaren
Tab bleibt neu gezeichneter Inhalt deutsch, bis er sichtbar wird. Beim Messen
sah das zweimal wie ein Fehler aus und war keiner.

**Ein Modell waegt zwei widersprechende Anweisungen nicht ab — es folgt der
deutlicheren.** Die Antwortsprache des Agenten stand zuerst als „ANTWORTSPRACHE:
English. Ist die Frage erkennbar in einer anderen Sprache gestellt, antwortest
du in DIESER Sprache" im Prompt. Gemessen an `qwen3-vl:4b` mit einer deutschen
Frage: Antwort auf **Englisch**. Mit genau EINER Anweisung befolgt dasselbe
Modell die Sprache dagegen in beide Richtungen. Die Sprache wird deshalb
**vorher auf dem Server** bestimmt (`LocService.Erkenne`), und der Prompt
enthält nur noch das Ergebnis. Allgemein: Was sich vor dem Aufruf entscheiden
lässt, gehört nicht in den Prompt.

**Der Wortschatz zur Spracherkennung kommt aus den Sprachdateien selbst.** Eine
eingebaute Wortliste je Sprache wäre die zweite Stelle, die man beim Hinzufügen
einer Sprache pflegen müsste — dieselbe Falle wie eine Liste erlaubter
Endpunkte. Die Werte von `loc.res.it.xml` SIND italienisch. Zwei Riegel: unter
fünf Wörtern wird nicht geraten („NVDA?" ist keine Sprache), und die beste
Sprache braucht mindestens das Doppelte der zweitbesten.

**Journal und Tagesübersicht entstehen ohne Sprachmodell — das ist kein
Versehen.** Sie in einer anderen Sprache zu erzeugen heisst deshalb, die 144
Vorlagen zu übersetzen, nicht das Modell schreiben zu lassen. Der schnelle Weg
holte genau das Risiko zurück, wegen dessen die Vorlagen existieren.

**Ein XML-Kommentar darf kein `--` enthalten.** Der erzeugte Sprachkatalog
trug im Kopfkommentar Gedankenstriche als `--`; `XDocument.Load` scheiterte
daran, der Dienst fing es je Datei ab — und die Sprachliste blieb **leer**, was
genauso aussieht wie „keine Sprachdateien vorhanden". Zwei sehr verschiedene
Ursachen, dieselbe Erscheinung. Im Kommentar gehört der Gedankenstrich `—` hin.

**Eine Anwendung kann keine statischen Texte enthalten — und diese tat es an
1.711 Stellen.** Nachträglich Schlüssel zu vergeben hiesse, jede dieser Stellen
in `index.html` und `app.js` anzufassen; jede ist eine Gelegenheit, die
Anwendung zu zerlegen. Deshalb **ist der deutsche Text der Schlüssel**:
`loc.res.de.xml` ist die Identität und ebendadurch der Katalog dessen, was
übersetzbar ist. Übersetzt wird über das **fertige DOM** — ein Beobachter statt
Aufrufen an den über hundert Stellen, die `innerHTML` schreiben. Der Preis ist
benannt und nicht wegzudiskutieren: Wer einen deutschen Text ändert, verliert
dessen Übersetzung **still**. Gegenmittel ist die Lückenzahl je Sprache aus
`/api/loc/`. Einzelheiten in [docs/SPRACHEN.md](docs/SPRACHEN.md).

**Bruchstück-Ersetzung braucht Wortgrenzen — und `` genügt dafür nicht.** Die
Übersetzungsschicht ersetzt in zusammengesetzten Sätzen bekannte Bruchstücke.
Der erste Entwurf nahm dafür alle Schlüssel, und im Browser stand daraufhin
**„Pricebewegung" und „Hourbar"**: „Kurs" und „Stunde" sind eigene Einträge und
wurden mitten im Kompositum ersetzt. Zwei Riegel: nur mehrwortige Schlüssel
(ein einzelnes Wort findet ohnehin den genauen Treffer) und ausdrückliche
Wortgrenzen, denn JavaScript führt Umlaute nicht als Wortzeichen — `Kurs`
trifft in „Kursbewegung" nicht, `[^A-Za-zÄÖÜäöüß]` dagegen schon.

**Ein Regex über `"…"` zählt C#-Zeichenketten falsch, sobald `"""` im Bestand
ist.** Die Suche nach deutschen Texten im Server meldete für `InvestService.cs`
genau EIN Literal mit Umlaut, wo mehrere stehen: Bei 353 Roh-Zeichenketten sieht
der Regex darin ein leeres Literal, und danach läuft die Paarung der
Anführungszeichen für den **Rest der Datei** verschoben. Kein Fehler, keine
Meldung, nur zu wenig Treffer — mit einem richtigen Leser waren es **1.128**
statt 27.

**Massenschreibvorgänge über SqlBulkCopy.** 300 verfolgte Werte ergeben ~35.000
Paare und bis zu 190.000 Kreuzungen. Einzelne INSERT/MERGE-Aufrufe sind dabei
chancenlos — Muster siehe `PairStatRepository`.

**Yahoo und CoinGecko brauchen einen Browser-User-Agent**, sonst antworten sie
mit 403. Zentral in `DependencyInjection.BrowserUserAgent`.

**Diagramme erst einhängen, dann vermessen.** uPlot bekommt seine Breite als
feste Pixelzahl. Wird sie unmittelbar nach dem Einhängen einer Box gemessen,
hat das CSS-Grid in der Ansicht „nebeneinander" erst ein Kind — `auto-fit`
streckt es über die volle Breite, und sobald weitere Boxen dazukommen, ragt das
zu breite Canvas über die Nachbarspalten. `drawCharts` legt deshalb erst alle
Boxen an (`createBox`) und zeichnet danach in einem zweiten Durchgang
(`renderPlot`). `.chart-box` hat zusätzlich `min-width: 0` und
`overflow: hidden` als Netz.

## Neuen Provider ergänzen

`IMarketDataProvider` implementieren (Kurse) oder `IUniverseProvider`
(Rangliste), in `DependencyInjection` registrieren. Die Reihenfolge der
Registrierung entscheidet, wer einspringt, wenn der am Asset hinterlegte
Provider nicht einsatzbereit ist. `IsConfigured` meldet fehlende Schlüssel —
ein Provider ohne Schlüssel wird stillschweigend übergangen, statt den Lauf zu
sprengen.

## Nächste sinnvolle Schritte

- Automatisierte Tests für `Ingest.Core/Analysis` — die Mathematik ist bewusst
  abhängigkeitsfrei und gut testbar, hat aber noch keine Testabdeckung.
- Symbole ohne Kursdaten beim Universum-Lauf aussortieren, statt sie dauerhaft
  ohne Bars mitzuführen. Stand 22.08.2026 bei 324 verfolgten Werten: **12 ohne
  jede Tagesbar, 34 ohne Stundenbar, 22 mit Tagesdaten älter als 90 Tage.** Sie
  fallen in jedem Diagramm auf, weil dort weniger Linien erscheinen als
  ausgewählt — die Antwort führt sie inzwischen unter `skipped` mit Begründung,
  aber die eigentliche Lösung wäre, sie gar nicht erst zu verfolgen.
- Zugangsdaten aus `appsettings.json` in User Secrets verschieben.
- Kopfstau in `get_due_forecasts` auflösen — siehe oben. Ohne das lernt die
  Prognose nicht weiter.

Wie die Säulen in die Prognose eingehen — und warum die meisten davon nichts
beitragen, obwohl sie eingebaut sind — steht in
[docs/SAEULENMISCHUNG.md](docs/SAEULENMISCHUNG.md).

Die Sprachumstellung der Oberfläche samt ihrer Grenzen steht in
[docs/SPRACHEN.md](docs/SPRACHEN.md).

Vorankündigungen neuer Werte samt ihrer Quellen stehen in
[docs/NEUZUGAENGE.md](docs/NEUZUGAENGE.md).

Das virtuelle Depot (Konto, Gebühren, Vermögensverlauf) ist in
[docs/INVESTINGS.md](docs/INVESTINGS.md) beschrieben, der selbsthandelnde
Autopilot samt seiner beiden Datenfallen in
[docs/AUTOPILOT.md](docs/AUTOPILOT.md).

Die Handelsansichten (Kreuzungs-Rangliste, Tausch, Day Trading) samt
Kostenmodell sind in [docs/HANDELSSEITEN.md](docs/HANDELSSEITEN.md)
beschrieben, die Ratgeber-Ansichten (Langfrist, Bot-Herde, Umkehrschluss) in
[docs/RATGEBERSEITEN.md](docs/RATGEBERSEITEN.md), der Weltbestand und der
Zeitzonen-Vorlauf in
[docs/WELTBESTAND-UND-ZEITZONEN.md](docs/WELTBESTAND-UND-ZEITZONEN.md).

**Ein Bauskript, das sein Zielverzeichnis ausraeumt, muss sagen, was es dabei
mitnimmt.** `1-export-source.ps1` beginnt mit `Remove-Item $OutDir -Recurse` --
richtig, denn ein Buendel aus alten und neuen Teilen waere schlimmer als
keines. Beim Neubau am 31.08.2026 verschwanden damit `LIESMICH.md`, `doku\`
und `skripte\`: von Hand dazugelegt, dem Skript unbekannt, in keinem Repo und
in keinem Papierkorb. Es zaehlt jetzt vorher, was es NICHT wieder anlegt, und
bricht ab statt zu loeschen (`-Ueberschreiben` hebt das auf). Die
Dokumentation liegt seither unter `deploy/` und wird mitversioniert.

Eine auslieferbare Fassung samt Einrichtungsskripten liegt unter
`D:\setup\stockcrawler` — Aufbau und Voraussetzungen stehen in deren
`LIESMICH.md` — dort steht auch, wie der erste Verwalter angelegt wird und was
die beiden Rollen dürfen. Wichtig daran: Zur Laufzeit braucht die Anwendung **nur
`bge-m3`** (1,2 GB) für Wissen und Semantik. `nemotron3:33b` ist optional und
nur für die Reasoning-Seite; die Prognosemodelle laufen als ONNX im Prozess auf
der CPU.
