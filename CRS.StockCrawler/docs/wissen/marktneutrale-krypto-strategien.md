# Marktneutrale Strategien im Kryptomarkt — Landkarte und Kostenrechnung

Eine Übersicht der strukturierten Strategien, die neben dem einfachen
Richtungswetten existieren, jeweils mit dem, was die Rendite auffrisst.

## Warum das Bild vom Gezeitenkraftwerk nicht trägt

Beim Gezeitenkraftwerk kommt die Energie von außen — vom Mond. Sie ist
physikalisch, unerschöpflich und niemand kann sie wegnehmen.

Bei marktneutralen Strategien kommt die Energie von anderen
Marktteilnehmern. Die passen sich an. **Der Vorsprung ist kompetitiv, nicht
physikalisch.** Er existiert genau so lange, bis genug Kapital ihn abgreift.

Das ist der wichtigste Satz dieses Dokuments, und er gilt für jede der unten
beschriebenen Strategien.

## Die Arbitrage-Familie

### Cross-Exchange

Gleiches Papier, zwei Börsen, Preisunterschied. Sieht aus wie eine sichere
Wette, ist aber eine andere Sache: Bei einer echten Sicherheitswette sind
beide Beine gleichzeitig festgeschrieben. Hier müssen Coins transferiert
werden — und in den Minuten der Bestätigung auf der Blockchain bewegt sich
der Preis.

Die berufsmäßige Fassung arbeitet deshalb mit vorpositioniertem Kapital auf
beiden Börsen und gleicht nur periodisch aus. Der Preis für den
„risikolosen" Aufschlag ist damit: Das Kapital liegt auf mehreren Börsen und
damit im Gegenparteirisiko. Der Zusammenbruch von FTX ist die Erinnerung
daran, dass dieses Risiko nicht theoretisch ist.

### Dreiecksarbitrage

BTC → ETH → USDT → BTC innerhalb einer Börse. Kein Transferrisiko, dafür
lebt der Vorsprung im Millisekundenbereich. Die Gegenspieler sind Programme,
die im selben Rechenzentrum stehen wie die Börse.

### CEX gegen DEX

Preisunterschied zwischen zentraler Börse und automatischem Market Maker.
Die Konkurrenz ist hier keine Privatperson mit einem Skript, sondern
spezialisierte Infrastruktur.

## Die anderen Familien

### Basisgeschäft (Cash and Carry)

Der wirtschaftlich sauberste Klassiker: Kassaware kaufen, Terminkontrakt
gleicher Laufzeit verkaufen. Der Terminkontrakt läuft bei Verfall zwingend
auf den Kassakurs zu; der Aufschlag ist der Ertrag. Preisneutral, mit klar
umrissener Rendite.

Das Risiko sitzt im Nachschuss auf dem Verkaufsbein, wenn der Preis stark
steigt.

### Finanzierungsrate abernten

Die Fassung für unbefristete Terminkontrakte. Weil sie nicht verfallen,
zahlen alle acht Stunden die Käufer an die Verkäufer oder umgekehrt, damit
der Preis am Kassakurs klebt. In Aufwärtsphasen ist diese Rate meist positiv:
Kassa kaufen und unbefristeten Kontrakt verkaufen kassiert die Zahlungen.

Das ist im Kern **keine Arbitrage, sondern eine Risikoprämie**. Man wird
dafür bezahlt, dass man dem gehebelten Publikum die Gegenseite stellt. Die
Rate kann kippen — und sie kippt genau dann, wenn alle dieselbe Position
halten.

### Market Making

Man stellt Geld- und Briefkurs und verdient an der Spanne. Der strukturelle
Feind heißt **negative Auslese**: Die eigene Order wird bevorzugt dann
bedient, wenn die Gegenseite mehr weiß. Ohne Bestandsmodell und schnelles
Zurückziehen der Kurse verliert man am Rand der Verteilung mehr, als man an
der Spanne verdient.

### Staking

Echter Zahlungsstrom aus der Ausgabe neuer Einheiten, bei Ethereum etwa drei
bis fünf Prozent. Kein Handel, sondern Bezahlung für Konsensarbeit.

Die Risiken: Strafabzüge, Bindungsfristen — und vor allem, dass die Rendite
**in der Münze selbst gerechnet** ist. Vier Prozent auf etwas, das vierzig
Prozent fällt, sind keine vier Prozent.

### Liquidität stellen

Sieht aus wie Zinsen, ist aber der Verkauf einer Schwankungsposition. Das
Stichwort ist der unbeständige Verlust, genauer: der Verlust gegenüber dem
bloßen Umschichten. Man ist strukturell auf der kurzen Seite der Krümmung,
und die Gebühren sind die Prämie dafür. Bei stark bewegten Paaren verliert
man gegen einfaches Halten, auch wenn die angezeigte Jahresrendite gut
aussieht.

### MEV

Sandwich-Angriffe, Liquidationen, Nachlaufen. Der wirklich einträgliche Teil
des Kryptomarkts — und ein reines Wettrennen um Infrastruktur mit eigener
Marktstruktur. Für eine einzelne Person praktisch nicht mehr zugänglich.

## Was den Vorsprung auffrisst

Diese Liste entscheidet, ob eine Strategie überhaupt in Frage kommt:

| Posten | Größenordnung |
| --- | --- |
| Gebühr des Nehmers, je Bein | 0,05 – 0,10 % |
| Schlupf — der angezeigte Kurs ist die Spitze des Buches, nicht der eigene Abschluss | 0,02 – 0,10 % |
| Auszahlungsgebühren und Bestätigungszeiten | je Transfer |
| Kapitalbindung auf mehreren Plattformen | Opportunitätskosten |
| Gegenparteirisiko der Börse | selten, dann total |

**Bei 0,1 Prozent je Seite ist eine Spanne von 0,3 Prozent schon zur Hälfte
weg.** Ein Tausch von einem Papier in ein anderes hat zwei Beine — verkaufen
und kaufen — also fällt die Gebühr zweimal an, der Schlupf ebenso.

Was übrig bleibt, ist meist eine niedrige einstellige Rendite mit
gelegentlichen großen Verlusten am Rand. **Die Verteilung ist schief: viele
kleine Gewinne, selten ein großer Verlust** — genau das Profil, das in einer
Rückrechnung hervorragend aussieht und im Betrieb nicht hält.

## Die Realitätsprüfung

Nichts davon ist Geheimwissen. Dieselben Aufbauten laufen bei Dutzenden
Firmen mit besserer Infrastruktur, niedrigeren Gebührenstufen — Rückvergütung
für den Steller statt Gebühr für den Nehmer — und Rechnern im selben Gebäude
wie die Börse.

**Steht eine Spanne lange genug offen, dass man sie von Hand ausführen kann,
ist das meistens ein Signal und kein Geschenk:** Auszahlungen gesperrt,
Liquidität nur auf dem Papier vorhanden, oder das Papier auf einer Seite
nicht handelbar.

## Was daraus für dieses System folgt

Drei Punkte, die unmittelbar auf die Kreuzungs- und Tauschansicht wirken:

**Erstens: Eine Kostenschwelle gehört in jede Bewertung.** Ein Signal, dessen
durchschnittlicher Ertrag unter dem Rundlauf aus Gebühren und Schlupf liegt,
ist nicht schwach — es ist wertlos, egal wie hoch die Trefferquote ist. Bei
zwei Beinen und 0,1 Prozent Gebühr plus 0,05 Prozent Schlupf je Seite sind
das rund **0,3 Prozent je Tausch**.

**Zweitens: Die Trefferquote allein genügt nicht.** Eine schiefe Verteilung
kann bei siebzig Prozent Treffern trotzdem Geld verlieren, wenn die dreißig
Prozent Fehlschläge größer ausfallen. Neben der Trefferquote muss deshalb
immer der durchschnittliche Ertrag stehen — und beide zusammen erst
rechtfertigen ein Urteil.

**Drittens: Der Vorsprung verschwindet.** Was in einer Rückrechnung über zwei
Jahre trug, muss heute nicht mehr tragen. Ein Maß, das über den ganzen
Zeitraum mittelt, verdeckt genau das.

## Steuerliche Einordnung, Österreich

Kryptoerträge unterliegen seit der ökosozialen Steuerreform grundsätzlich dem
Sondersteuersatz von 27,5 Prozent. Gewerbliches, systematisches Handeln kann
jedoch als betriebliche Einkunft eingestuft werden. Das ist eine Frage, die
vorab zum Steuerberater gehört, nicht nachträglich.

Dieses Dokument ist keine Anlageberatung.
