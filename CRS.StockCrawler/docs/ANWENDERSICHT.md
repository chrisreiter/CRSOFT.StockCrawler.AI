# CRSOFT.StockCrawler — aus Anwendersicht

**Stand 18.09.2026.** Grundlage für Beiträge (Blog, LinkedIn): kurz, ohne
Jargon, jede Aussage ist gemessen. Die Zahlen unten sind der Stand des
Datums oben — vor einem Beitrag gegen die Startseite „Heute" prüfen.

---

## In einem Satz

Ein lokal laufendes Werkzeug, das Kurse von rund 650 Aktien, ETFs,
Kryptowährungen und Devisen sammelt, ihr Verhalten misst und daraus
Prognosen bildet — und das bei jeder Zahl dazusagt, wie belastbar sie ist.

Es verspricht keine Gewinne. Es zeigt, was der Markt tut, was Modelle daraus
machen, und wie oft sie damit richtig lagen.

---

## Was man damit tut

**Heute** — die Startseite. Marktlage, größte Bewegungen, aktuelle
Meldungen, Depotstand, Prognosegüte, anstehende Neuemissionen und
Systemzustand auf einen Blick; von jeder Kachel in die Funktion dahinter.

**Kurse** — beliebig viele Werte übereinander: Linien, Kerzen, Volumen,
Korrelation, Kapitalfluss. Dazu **Investings**: ein virtuelles Depot mit
Konto, Gebühren, Vermögensverlauf in EUR oder USD.

**Prognose** — je Wert und Horizont (1 Tag bis 1 Jahr) eine Schätzung aus
sieben „Säulen": lernende Kursmodelle, Spektralanalyse, Kapitalfluss, Deep
Learning, Fachliteratur, Nachrichtenstimmung, KI-Urteil. Jede Säule ist
zuschaltbar und gewichtet — aber Gewicht bekommt nur, was sich in der
Nachprüfung bewährt hat. Der Regler sagt, *wie viel* von etwas Brauchbarem
einfließt; *ob* etwas brauchbar ist, entscheidet die Messung.

**Autopilot** — vier Strategien handeln das virtuelle Depot wöchentlich:
*streng* (nur mit Nachweis), *aktiv*, *halten* (Grundlinie: einmal kaufen,
liegen lassen) und *invers* (Gegenkontrolle mit gedrehtem Vorzeichen). Jede
Entscheidung ist protokolliert — auch die verworfenen, mit der Zahl, an der
sie scheiterten.

**Analyse** — welche Werte sich gemeinsam bewegen, wo sich Kurse kreuzen,
wer wem vorausläuft (und wer nur so aussieht).

**Grundschwingungen** — Fourier-Zerlegung jedes Kursverlaufs: ein Katalog
der Zyklen, wer welches Muster teilt, mit Phasenversatz.

**Bot-Herde** — was die öffentlich bekannten Handelssignale (RSI, Bollinger,
Goldenes Kreuz) am Kurs wirklich hinterlassen. Gemessen, nicht geglaubt.

**Langfrist** — Rendite und Streuung über Jahre, für einzelne Werte und
Körbe. Die ehrlichste Seite der Anwendung.

**Day Trading, Tausch** — ob der Markt heute einen Handel innerhalb des
Tages trägt (nach Gebühren: fast nie), und welche Umschichtungen im Bestand
sinnvoll wären.

**Neuzugänge** — Erstnotizen und neue Krypto-Listings, eingetragen bei der
Ankündigung, nicht wenn etwas auffällt. Die vollständige Kohorte,
Fehlschläge eingeschlossen.

**Wissen & Nachrichten** — 55 Fachtexte (arXiv-Arbeiten, gemeinfreie
Klassiker, eine Dissertation, ein BIZ-Papier) und 35 Nachrichtenfeeds,
durchsuchbar mit Fundstelle.

**Reasoning** — ein KI-Agent (lokal, Nemotron) beantwortet Fragen zu allem,
was das System weiß, und belegt jede Zahl mit dem Werkzeugaufruf, aus dem
sie stammt. Er darf keine Zahl erfinden. Seit September gibt er je Wert ein
Urteil ab, das nach fünf Handelstagen gegen den Kurs geprüft wird.

**Tagesjournal** — der Markttag als Text, ohne Sprachmodell erzeugt: jede
Zahl stammt aus einer Abfrage, der Satz drumherum ist Vorlage. Als Markdown
zum Weiterverwenden.

---

## Wem es was bringt

**Dem privaten Anleger** — eine Zweitmeinung, die nichts verkauft. Er sieht,
dass die meisten Signale nach Kosten nichts einbringen, bevor er es mit
eigenem Geld lernt. Langfrist-Seite und Depot zeigen, was eine ruhige Hand
gebracht hätte.

**Dem Quant und Entwickler** — ein Messstand mit ehrlicher Methodik:
Sperrbereich, die blosse Drift als Latte, keine Rückrechnung als
Live-Ergebnis. Jede Säule lässt sich einzeln prüfen und erweitern. .NET 8,
SQL Server oder PostgreSQL, alles offen.

**Dem Forschenden und Studierenden** — Fourier-Zerlegung, Kurvendiskussion,
Querschnittsanalyse, Vorlaufmessung zwischen Zeitzonen — mit den
Negativbefunden, die in der Literatur selten stehen.

**Dem Besucher** — ein Gastzugang ohne Kennwort: eine offene Demonstration
mit echten Daten, ohne etwas anstoßen zu können.

---

## Was es bewusst nicht ist

Keine Kaufempfehlung. Kein Fair-Value-Rechner, kein Fundamentaldaten-
Terminal. Kein Signaldienst.

Der zentrale gemessene Befund lautet: **Kurse bewegen sich gemeinsam, nicht
nacheinander.** Die Anwendung ist so gebaut, dass sie diesen Befund zeigt,
statt ihn zu verstecken.

---

## Zahlen, die man zitieren kann (Stand 18.09.2026)

| | |
| --- | --- |
| Verfolgte Werte | ~650 (Aktien, ETFs, Krypto, Devisen; US, Europa, Asien) |
| Kurshistorie | 2,9 Mio Tagesbars ab 2001, 1,1 Mio Stundenbars |
| Prognosen | 1,19 Mio gestellt, 408.000 bewertet |
| Trefferquote der Richtung, letzte 30 Tage | 50,7 % — die Münzwurf-Schwelle liegt bei 52,3 % |
| Deep-Learning-Bänder | keines schlägt die blosse Drift |
| Nachrichtenstimmung als Prognose | Korrelation 0,04 bei Schwelle 0,08 — trägt nicht |
| Bot-Signale | 3 von 7 schlagen die Gebühren; die bärischen treffen unter 50 % |
| Fachtexte / Nachrichtenfeeds | 55 / 35, 44.000 eingebettete Abschnitte |
| Grundschwingungen | 334 Klassen, 7 Paare, davon 0 beständig |

Jede dieser Zahlen steht in der Anwendung mit ihrer Herkunft.

---

## Bausteine für Beiträge

Sätze, die stimmen und die man verwenden darf:

- *„Das Werkzeug hat in einem Jahr Messung kein Verfahren gefunden, das den
  Stillstand zuverlässig schlägt. Das ist kein Scheitern — das ist das
  Ergebnis."*
- *„Jede Prognose kommt mit der Zahl, wie oft sie in der Vergangenheit
  richtig lag. Ohne diese Zahl wäre sie eine Behauptung."*
- *„Der KI-Agent darf keine Zahl erfinden. Jede stammt aus einem
  Werkzeugaufruf, der unter der Antwort steht."*
- *„Vier Strategien handeln dasselbe Geld auf vier Arten. Eine davon macht
  absichtlich das Gegenteil — sonst wüsste man nicht, ob das Signal etwas
  weiß oder der Markt nur gestiegen ist."*
- *„Nachrichten hängen mit heute zusammen, nicht mit morgen. Gemessen über
  648 Handelstage."*

Sätze, die man **nicht** verwenden darf: alles mit „schlägt den Markt",
„Rendite von X % p.a.", „empfiehlt", „Kaufsignal".

---

## Technik in einem Absatz (für die Fußnote)

.NET 8, Minimal-API, Dapper; SQL Server oder PostgreSQL, umschaltbar. Kurse
von Yahoo, TwelveData, CoinGecko; Nachrichten über RSS-Feeds. Einbettung mit
bge-m3, Vektorsuche mit Qdrant, Reasoning mit Nemotron 3 (33B) über Ollama —
alles lokal, keine Cloud. Läuft auf einem Arbeitsrechner mit 60 GB RAM; für
den Live-Betrieb gibt es einen Lesemodus auf einem replizierten
Datenbankserver ohne GPU.
