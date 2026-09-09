# Quelle: „Trading Skills" (Masterarbeit, JKU Linz 2024)

Susanne Pichler, Institut für betriebliche Finanzwirtschaft — Abteilung für Asset
Management, Betreuer Assoz. Univ.-Prof. Dr. Johann Burgstaller, Oktober 2024.
189 Seiten, 447.000 Zeichen, Literaturverzeichnis rund 60.000 Zeichen.

## Was die Arbeit ist — und was nicht

Eine **Literaturübersicht über Erfolgsfaktoren von Privatanlegern**. Sie
beantwortet die Frage, *wer* beim Handeln erfolgreich ist und *warum* —
demografische Merkmale, kognitive Fähigkeiten, Risikoneigung, Handelshäufigkeit,
Diversifikation, Brokerwahl, Social Trading, Verhaltensverzerrungen.

**Sie enthält keine Prognoseverfahren.** Kein Kapitel behandelt die Vorhersage
von Kursen, es kommen keine Modelle, keine Schätzverfahren und keine
Zeitreihenanalyse vor. Wer hier nach Methoden für die Prognosesäulen sucht,
findet sie nicht — und das ist keine Schwäche der Arbeit, sondern ihr Thema.

Verwertbar ist etwas anderes, und zwar in einer Weise, die für dieses System
ungewöhnlich gut passt: Die beschriebenen Verhaltensverzerrungen sind
**messbare Marktphänomene mit einer Signatur in Kurs und Volumen**. Sie sind
keine Prognoseverfahren, aber sie sind Merkmale, die sich aus den vorhandenen
Daten berechnen und wie jedes andere Merkmal auf Tragfähigkeit prüfen lassen.

## Verwertbar: vier messbare Effekte

### 1. Dispositionseffekt

Anleger realisieren Gewinne zu früh und halten Verluste zu lange (Shefrin/
Statman 1985, bestätigt von Odean an NYSE-, AMEX- und Nasdaq-Kursdaten 1993).
Der Bezugspunkt ist der Kaufpreis.

**Was daraus für uns folgt:** Über einem Kursniveau, an dem viele Anleger im
Gewinn sind, entsteht Verkaufsdruck; unter einem Niveau mit vielen
Buchverlusten entsteht Zurückhaltung. Der Kaufpreis der Marktteilnehmer ist
unbekannt, aber näherungsweise rekonstruierbar: **volumengewichtete
Kursverteilung der zurückliegenden Monate**. Daraus lässt sich je Bar berechnen,
welcher Anteil des umgesetzten Volumens derzeit im Gewinn liegt — in der
Literatur als „Capital Gains Overhang" bekannt.

Das ist aus `price_bar` unmittelbar berechenbar. Ein Merkmal, kein Modell.

**Zweiter, prüfbarer Nebenbefund:** Der Effekt schwächt sich **im Dezember** ab
(steuerlich motiviert, Odean). Damit gibt es eine Kalenderabhängigkeit, die sich
an unseren 25 Jahren Historie unmittelbar nachrechnen lässt.

### 2. Übermäßiges Selbstvertrauen → Handelsvolumen

Barber/Odean (2000, 2001, 2002) führen erhöhtes Handelsvolumen ursächlich auf
Selbstüberschätzung zurück, mit der Folge schlechterer Renditen. Verstärkt beim
Wechsel von telefonischem zu Online-Handel.

**Was daraus für uns folgt:** Ungewöhnlich hoher Umsatz ohne entsprechenden
Nachrichtenanlass ist ein Anzeichen für Kleinanlegeraktivität — und die ist
laut dieser Literatur systematisch renditeschwach. Das ist ein **gegenläufiges**
Merkmal, das sich mit unserer Umsatzauswertung und der künftigen
Nachrichtensäule zusammenführen lässt: Umsatzausschlag **ohne** Nachrichtenlage
ist etwas anderes als Umsatzausschlag **mit**.

### 3. Herdenverhalten

Anleger streben kurzfristige Gewinne durch Nachahmung an, statt fundiert zu
investieren (Liu et al. 2019). Im Social Trading und Copy-Trading verstärkt.

**Was daraus für uns folgt:** Herdenverhalten erzeugt gleichgerichtete Bewegung
über viele Werte hinweg — genau das, was die Modenanalyse bereits misst. Der
gefundene Marktfaktor mit 53 Prozent erklärter Bewegung und
Phasenspreizung 2,8 Bars ist die Signatur dieses Verhaltens. Die Arbeit liefert
dafür die Begründung, warum er so groß ist.

### 4. Konzentration statt Streuung

Selbstüberschätzte Anleger konzentrieren ihre Bestände stärker
(Goetzmann/Kumar 2004, 2008 — mit 25 Zitierungen die zweithäufigste Quelle).

**Was daraus für uns folgt:** Ein Querschnittsmerkmal, keine Zeitreihe. Für uns
nur mittelbar nutzbar — es sagt etwas über Aufmerksamkeitskonzentration im
Markt, die sich an der Streuung der Umsätze über die Werte messen lässt.

## Die eigentliche Ausbeute: die Primärquellen

Die Arbeit ist eine Übersicht; ihr Wert für uns liegt in ihrem
Literaturverzeichnis. Die am häufigsten herangezogenen Quellen, nach
Zitierhäufigkeit:

| Quelle | Zitate | Wofür |
| --- | ---: | --- |
| **Barber/Odean** (2000, 2001, 2002) | 36 | Handelshäufigkeit und Rendite, Aufmerksamkeitseffekte |
| **Goetzmann/Kumar** (2004, 2008) | 25 | Portfoliokonzentration, Diversifikationsverhalten |
| **Odean** (1998a, 1998b) | 14 | Dispositionseffekt an Kursdaten, Handelsvolumen |
| **Korniotis/Kumar** | 14 | kognitive Fähigkeiten und Anlageerfolg |
| **Talpsepp** | 12 | Dispositionseffekt international |
| **Hoffmann/Shefrin** (2014) | 9 | technische Analyse und Selbstüberschätzung |
| **Grinblatt/Keloharju** (2009) | – | Handelsaktivität, finnische Registerdaten |
| **Kahneman/Tversky** (1979) | – | Prospect-Theorie, Grundlage des Ganzen |
| **Shefrin/Statman** (1985) | – | Erstbeschreibung des Dispositionseffekts |

Barber und Odean sind für uns die ergiebigste Spur. Ihre Arbeiten enthalten —
anders als diese Übersicht — **an Kursdaten geprüfte Effekte mit
Kennzahlendefinition**, insbesondere der Aufmerksamkeitseffekt: Privatanleger
kaufen bevorzugt Werte, die durch ungewöhnliches Volumen, extreme Tagesrenditen
oder Nachrichten auffallen. Das ist unmittelbar an unseren Daten prüfbar und
verbindet sich mit der geplanten Nachrichtensäule.

## Einschätzung als Quelle für die Wissenssäule

**Als Prognosequelle: schwach.** Sie liefert kein Verfahren.

**Als Merkmalsquelle: brauchbar**, mit einem konkreten, sofort umsetzbaren
Vorschlag — dem Anteil des Volumens im Gewinn (Capital Gains Overhang).

**Als Quellenverzeichnis: wertvoll.** Sie erspart die Suche nach den
Primärarbeiten, in denen die tatsächlich messbaren Effekte definiert sind.

**Als Einordnung: nützlich.** Sie erklärt, warum der Marktfaktor so dominant
ist und warum Aufmerksamkeit und Umsatz zusammenhängen — Zusammenhänge, die
wir gemessen, aber bislang nicht begründet haben.

## Nächster prüfbarer Schritt

Den Volumensanteil im Gewinn je Wert und Bar berechnen und mit derselben
Rückhalteprüfung testen wie alles andere: Sagt er etwas über die
marktbereinigte Bewegung danach? Die Daten dafür liegen vollständig vor —
`price_bar` mit Kurs und Volumen über 25 Jahre. Es braucht kein neues Modell,
nur ein neues Merkmal.
