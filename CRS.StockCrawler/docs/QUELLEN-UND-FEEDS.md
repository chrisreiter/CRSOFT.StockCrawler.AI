# Quellen: Nachrichten und Fachliteratur

**CRSOFT.StockCrawler**, Stand 22.08.2026

Wie Text in die Säulen „Semantik" und „Knowledge" kommt.

---

## Ein Feed ist etwas anderes als eine Seite

Das ist die Entscheidung, an der alles hängt.

Eine Nachrichtenseite abzurufen liefert bei jedem Lauf **dieselbe Seite mit
anderem Inhalt**. Man weiß danach nicht, was daran neu war — also müsste man
alles erneut einbetten. Bei 35 Quellen und stündlichem Lauf wäre das dieselbe
Arbeit für ein Ergebnis, das schon dasteht.

Ein Feed liefert **einzelne Meldungen mit Adresse und Zeitstempel**. Erst das
erlaubt beides, was diese Säule braucht:

1. **Nur Neues einbetten** — eindeutig ist die Adresse, nicht der Titel. Titel
   werden nachträglich geändert, Adressen nicht.
2. **Eine Aussage in Bezug zu einer Kursbewegung setzen** — ohne Zeitstempel
   ist ein Text zu keinem Kurs zuzuordnen, und die ganze Säule wäre eine
   Volltextsuche mit Zusatzaufwand.

Ein Artikel wird deshalb eine **eigene Quelle** mit `parent_source_id` auf
seinen Feed. Damit greift die vorhandene Zerlegungs- und Einbettungsmaschinerie
unverändert, und Zustand, Kennung und Zeitstempel gehören dem Artikel.

Im Raster erscheinen **nur die Feeds** mit der Zahl ihrer Artikel. Bei 35
Quellen wären es sonst nach einer Woche tausende Zeilen, und die Übersicht über
die Quellen wäre dahin.

---

## Die Nachrichtenquellen

35 Feeds, alle geprüft. Die Liste steht unter `infra/quellen.json` und lässt
sich dort ergänzen, ohne etwas zu übersetzen.

| Region | Quellen |
| --- | --- |
| USA | Reuters, WSJ (Märkte, Wirtschaft), MarketWatch (2), CNBC (2), Yahoo Finance, Nasdaq, Seeking Alpha |
| Europa | Financial Times, Guardian, BBC |
| DACH | Handelsblatt, FinanzNachrichten.de, NZZ, Der Standard, ORF |
| Asien | Nikkei Asia, Japan Times, South China Morning Post, Economic Times India |
| Welt | Investing.com (Nachrichten, Indikatoren) |
| Krypto | CoinDesk, Cointelegraph, Bitcoin Magazine, Decrypt |
| Notenbanken | Fed, EZB, Bank of England, BIZ-Arbeitspapiere |
| Aufsicht | SEC |
| Weltereignisse | UN News, NASA-Naturereignisse |

**Warum UN News und NASA.** Weil Weltereignisse auf Kurse wirken und in
Wirtschaftsmeldungen erst auftauchen, wenn sie sich bereits in den Kursen
niedergeschlagen haben. Ein Erdbeben, ein Konflikt, eine Dürre stehen zuerst
dort.

Jede Quelle trägt ihren eigenen **Abstand**: CoinDesk 30 Minuten,
BIZ-Arbeitspapiere 720. Vor dessen Ablauf wird sie nicht erneut abgerufen — das
ist gegenüber der Gegenstelle höflich und spart Arbeit, die nichts bringt.

### Drei Adressen waren falsch

Beim ersten Lauf meldeten drei Feeds 404 oder 301: `boerse.de`,
South China Morning Post und die BIZ-Publikationsliste. Ersetzt durch geprüfte
Adressen — `finanznachrichten.de`, SCMP mit Schrägstrich am Ende,
`bis.org/doclist/wppubls.rss`.

Zwei weitere Kandidaten (Wiener Börse, Börse Frankfurt) haben **keinen Feed**;
sie wurden wieder entfernt, statt eine kaputte Quelle stehen zu lassen, die bei
jedem Lauf einen Fehler produziert.

---

## Die Fachliteratur

16 frei zugängliche Quellen, Rechtslage je Eintrag vermerkt.

**Zwölf gemeinfreie Klassiker** aus dem Project Gutenberg: Lefèvre
(*Reminiscences of a Stock Operator*, 1923), Selden (*Psychology of the Stock
Market*, 1912), Harper (*The Psychology of Speculation* und der Nachtrag nach
dem Crash von 1929), Clews, Crump, Butler, Gibson, Lawson, Rice, Brandenburg,
Francis.

Hundert Jahre alte Bücher sind keine überholte Wahl, sondern eine bewusste. Was
sie beschreiben — Panik, Herdenverhalten, das Verhältnis von Kurs und Erwartung
— ist genau das, was auch heute in den Kursen steht. Und sie sind gemeinfrei,
was für neuere Literatur nicht gilt.

**Vier wissenschaftliche Arbeiten**: eine offen zugängliche Dissertation zu
Marktmikrostruktur und algorithmischem Handel (Universität Luxemburg), zwei
arXiv-Arbeiten (*Limit Order Books*, *Trading Invariance Hypothesis*) und ein
BIZ-Arbeitspapier (*Quantifying the High-Frequency Trading Arms Race*).

---

## Drei Fallen beim Einlesen

### Navigationsmenüs in jedem Abschnitt

Der erste Lauf über echte Nachrichtenseiten lieferte auf jede Frage Abschnitte
wie:

> *„Skip Navigation Markets Pre-Markets U.S. Markets Europe Markets China
> Markets Asia Markets World Markets…"*

Das Menü steht auf jeder Seite derselben Quelle, es ist lang, und es hat keinen
Inhalt. Eingebettet zieht es **alle Vektoren einer Quelle in dieselbe Ecke** —
die Suche findet danach vor allem, von welcher Seite ein Text stammt.

Erkannt wird es nicht an Schlüsselwörtern, sondern an der **Struktur**: Ein Menü
besteht aus kurzen Bezeichnungen ohne Satzzeichen. Fließtext hat Sätze, also
Punkte. Ein Satzende je 220 Zeichen trennt beides zuverlässig, ohne Wissen über
die einzelne Seite.

### Derselbe Filter zerstörte die Bücher

Der Filter urteilte **zeilenweise**. Gutenberg-Text ist hart auf siebzig Zeichen
umbrochen — also ist jede Zeile zu kurz, enthält keinen vollständigen Satz und
fliegt raus. Von zwölf Büchern kam **eines** durch, der Rest meldete „Text zu
kurz".

Zwei Korrekturen: Reiner Text geht gar nicht erst durch die HTML-Reinigung, und
der Filter urteilt über **Absätze** statt Zeilen. Ein Absatz ist die kleinste
Einheit, die eine Aussage tragen kann — bei umbrochenem Buchtext wie bei einem
HTML-Block.

Ergebnis: *Reminiscences of a Stock Operator* ging von 0 auf **633 Abschnitte**.

### PDFs aus dem Netz wurden als Text gelesen

Die wissenschaftlichen Arbeiten sind PDFs. Sie kamen über dieselbe Schnittstelle
wie Nachrichtenseiten und liefen durch die HTML-Reinigung — das ergibt
Byte-Salat, den **niemand als Fehler erkennt**: Es entstehen Abschnitte, sie
werden eingebettet, und die Suche findet später Unsinn mit ordentlich
aussehenden Ähnlichkeitswerten.

Jetzt entscheidet der Inhaltstyp: PDF landet kurz auf der Platte und geht durch
PdfPig samt Geometriefilter für Kopf- und Fußzeilen.

---

## Warum Abschnitte eine Kennung aus ihrem Inhalt haben

Ursprünglich bekam jeder Abschnitt eine Zufallskennung (`NEWID()`). Damit war
jedes Schreiben ein Neuschreiben: erst den alten Bestand löschen, dann den neuen
anlegen.

Das hat zwei Nachteile, und beide sind eingetreten.

**Zwei gleichzeitige Läufe verdoppeln alles.** Beide löschen, beide schreiben —
ohne Fehlermeldung. Sechs Quellen hatten hinterher exakt doppelt so viele
Abschnitte, wie ihre eigene Zählung meldete.

**Und jeder Neuaufbau kostet voll**, auch wenn sich am Text nichts geändert hat.

Die Kennung ist jetzt

```
SHA-256(source_id | ordinal | text)   →   in SQL und in Qdrant dieselbe
```

Geschrieben wird per `MERGE` beziehungsweise Upsert. Derselbe Abschnitt bekommt
dieselbe Kennung und wird an Ort und Stelle überschrieben:

| | vorher | nachher |
| --- | --- | --- |
| Zweiter erzwungener Lauf, gleicher Text | 66 s, 496 neu eingebettet | **2,4 s, nichts** |
| Zwei Läufe gleichzeitig | doppelte Abschnitte | unmöglich |

Ein eindeutiger Index auf `(source_id, ordinal)` sichert es auf Datenbankebene
ab — was auch immer zwei Läufe versuchen, ein zweiter Eintrag entsteht nicht.

Gelöscht wird nur noch der **Überhang**: Wird eine Quelle kürzer, bleiben sonst
die Abschnitte hinter dem neuen Ende stehen.

### Der Hash muss auch das Verfahren kennen

`ChunkHash` bestand zuerst nur aus `(source_id, ordinal, text)`. Als die
Positionsspalten dazukamen, änderte sich der Text nicht — also meldete jeder
Lauf brav „alle unverändert", schrieb nichts, und `char_from` blieb bei 1.064
Abschnitten leer.

Die Ersparnis eines übersprungenen Laufs war damit erkauft, dass eine
Verbesserung nie ankommt. Der Hash sagt jetzt nicht nur, **was** in einem
Abschnitt steht, sondern auch, **wie er entstanden ist**:

```csharp
private const int VerarbeitungsFassung = 2;
var roh = $"v{VerarbeitungsFassung}|{sourceId}|{ordinal}|{text}";
```

Hochzählen erzwingt genau einen vollständigen Neuaufbau; danach ist wieder alles
billig. Dieselbe Rolle wie `FeatureSet.Version` beim Merkmalsexport.

### Verwaiste Vektoren

Punkte in Qdrant ohne Abschnitt in SQL erzeugen **keinen Fehler**. Die Suche
findet sie, schlägt nach, findet nichts — und der Treffer verschwindet still.
Man sieht weniger Treffer, sonst nichts.

`POST /api/knowledge/aufraeumen?saeule=…` gleicht beide Seiten ab. Bei der
Umstellung entfernte er 496 Punkte aus der alten Zufallskennung; seither sind
beide Seiten deckungsgleich (6.569 zu 6.569 im Wissen, 568 zu 568 in der
Semantik).

## Zurück zur Fundstelle

Ein Treffer ohne Weg zur Quelle ist eine halbe Antwort. Jeder Abschnitt trägt
deshalb seine Herkunft:

| Feld | Bedeutung |
| --- | --- |
| `page_from` / `page_to` | Seite, bei PDFs aus der Lage der Wortkästen |
| `char_from` / `char_to` | Zeichenposition im extrahierten Text |
| `anchor` | die ersten acht Worte, für den Textanker im Browser |

Die Antwort liefert daraus `fundstelle.beschreibung` („Seite 26–27", „ungefähr
Seite 38") und `verweis`:

```
PDF          /api/knowledge/datei/1#page=26
Web / Text   https://…/pg75570.txt#:~:text=Some%20cautious%20investors%20begin
```

**Warum beides.** Die Zeichenposition ist genau, aber zerbrechlich: Ändert sich
die Quelle, verschiebt sich jede Zahl. Der Wortlaut überlebt das meist. Deshalb
steht die Position für uns in der Datenbank und der Anker im Verweis.

**Der Anker muss am Satzanfang beginnen.** Durch die Überlappung fängt ein
Abschnitt oft mit dem Ende des vorigen an — mit einem Punkt oder halben Wort.
Der Browser vergleicht wörtlich; `#:~:text=. Some cautious` trifft nichts.

**Hochgeladene Dateien brauchen einen eigenen Endpunkt.** In der Datenbank steht
ein Pfad auf `D:`, den kein Browser öffnen kann.
`GET /api/knowledge/datei/{id}` liefert ausschließlich aus dem
Ablageverzeichnis — der Pfad kommt zwar aus der eigenen Datenbank, aber ein
Endpunkt, der jede beliebige Datei herausgibt, sobald einer falsch hineingerät,
ist es nicht wert.

## Der Zeitplan

Die Feeds hängen am **Stundenlauf**, nicht am Tageslauf: Meldungen sind schnell
alt, und eine Zinsentscheidung erst am nächsten Morgen einzulesen wäre für eine
Prognose wertlos.

Fehler bleiben in der Säule. Eine nicht erreichbare Adresse oder ein nicht
gestartetes Qdrant darf den Stundenlauf nicht mitreißen — an dem hängen auch
Kursabruf und Prognose.

---

## Aufrufe

```
POST /api/knowledge/kuratiert?saeule=semantic|knowledge
POST /api/knowledge/feeds?saeule=semantic&proFeed=25
POST /api/knowledge/index/{id}?neu=true
GET  /api/knowledge/search?frage=…&saeule=…&limit=8
```

## Was das jetzt beantwortet

```
Frage: „Was wird aktuell über Ripple XRP geschrieben?"

[0,629] 2026-08-22 — After Exploding by 18% in One Day, Is XRP a Buy?
   „…XRP was brought along for the ride upward on general optimism stemming
    from the president's comments, not because of anything direct or material…"

[0,608] 2026-08-22 — dieselbe Quelle, anderer Abschnitt
   „…network collected just $314 in fees — practically nothing. That means
    holders are barely compensated for putting their capital at risk…"
```

## Vorgemerkt

- [ ] **Bezahlschranken.** Von 191 geholten Artikeln ergaben 132 verwertbaren
      Text. Der Rest sind Seiten, die ohne Anmeldung nur den Anriss zeigen.
      Deren Zusammenfassung aus dem Feed wäre besser als nichts.
- [ ] **Zuordnung Artikel zu Wert.** Ein Artikel über Ripple ist derzeit nicht
      mit dem Kurs XRP-USD verknüpft. Ohne diese Brücke bleibt die Säule eine
      Suche und wird kein Prognosebeitrag — siehe
      [BRUECKE-TEXT-ZU-PROGNOSE.md](BRUECKE-TEXT-ZU-PROGNOSE.md).
- [ ] **Historische Nachrichten.** Was heute eingesammelt wird, beginnt heute.
      Ohne Text ab 2015 gibt es keinen Sperrbereich und damit keine Validierung.
      GDELT wäre die Antwort.
