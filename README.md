# CRSOFT.StockCrawler.AI

**Ein offenes Labor für Marktanalyse und Prognose — samt seiner
Negativbefunde.**

Es sammelt Kurse, Nachrichten und Fachliteratur in *ein* Datenmodell, untersucht
die Wechselwirkungen darin, erzeugt daraus selbstlernende Prognosen — und misst
schonungslos nach, was davon trägt.

Der letzte Halbsatz ist der Unterschied. Alles hier ist gegen einen Sperrbereich
geprüft, und wo ein Verfahren nichts leistet, steht das in der Oberfläche.

---

## Die Zahlen

| | |
| ---: | --- |
| **641** | verfolgte Werte — Aktien, ETFs, Krypto, Indizes, Devisen |
| **3.941.331** | Bars, Tages- und Stundenauflösung, bis 2001 zurück |
| **978.837** | Live-Prognosen über sieben Horizonte |
| **303.465** | davon gegen den eingetroffenen Kurs ausgewertet |
| **397.011** | ausgewertete Wertpaare |
| **42.219** | Abschnitte Fachliteratur, jeder mit Fundstelle |
| **11.644** | Nachrichtenartikel aus 35 Quellen weltweit |

Und die Zahl, die sonst niemand veröffentlicht:

> **Die Richtungstrefferquote liegt bei 0,5109.** Nötig wären **0,598**, damit
> ein Geschäft die Gebühren deckt. Kein Verfahren dieser Anwendung schlägt im
> Sperrbereich die blosse Drift — die mittlere Rendite des Trainingszeitraums,
> eine einzige Zahl.

Das steht nicht im Kleingedruckten, sondern in der Oberfläche, an genau der
Stelle, an der jemand sonst eine Zahl für bare Münze nähme.

**Das ist kein Eingeständnis. Das ist das Produkt.** Wer Verfahren im
Finanzbereich prüft, kämpft gegen Backtests, die zu gut aussehen, und gegen
Veröffentlichungen, die nur zeigen, was funktioniert hat. Hier liegt beides
offen.

---

## Was es kann

- **Kurse** aus mehreren Anbietern in einem einheitlichen Modell, weltweit,
  mit sauberer Behandlung unterschiedlicher Handelskalender und Zeitzonen
- **Nachrichten sprachunabhängig** — das Einbettungsmodell ist mehrsprachig,
  eine deutsche Frage findet eine japanische Meldung, ohne Übersetzung
- **Fachliteratur** in überlappenden Abschnitten mit Quelle, Seite und Anker
- **Acht Säulen**, gewichtet mit *Regler × gemessenem Verdienst* — eine Säule
  ohne Nachweis bewegt nichts, auch bei Regler auf hundert
- **Ein Reasoning-Modell, das keine Zahl erfinden darf**: jede Zahl stammt aus
  einem Werkzeugaufruf, und die Aufrufe stehen unter jeder Antwort
- **Virtuelles Depot** mit vier Autopilot-Strategien, darunter eine
  Gegenkontrolle, die das Signal absichtlich umdreht
- **Oberfläche in Deutsch und Englisch** — eine weitere Sprache ist eine
  XML-Datei und sonst nichts

Vollständig: [UEBERBLICK.md](UEBERBLICK.md)

---

## Loslegen

```
claude "/startup"
```

Claude Code arbeitet [`startUp.md`](startUp.md) ab: Voraussetzungen prüfen,
.NET, SQL Server, Schema, Ollama mit `bge-m3`, Qdrant, erster Verwalter,
Kursdaten. Ohne Claude Code steht jeder Schritt auch als Befehl da.

Läuft komplett lokal — keine Cloud nötig.

---

## Was es **nicht** ist

**Kein Handelssystem und keine Anlageberatung.** Die Anwendung sagt an keiner
Stelle, was zu kaufen wäre. Sie beantwortet die Frage davor: Was lässt sich
überhaupt messen, und trägt es die Kosten?

---

## Mitarbeiten

**Der wertvollste Pull Request ist eine Widerlegung.** Wenn eine der gemessenen
Aussagen nicht hält, ist das der beste Beitrag, den dieses Projekt bekommen
kann.

[CONTRIBUTING.md](CONTRIBUTING.md) — Hausstil und Rechteeinräumung. Bitte vorher
lesen: Der Projektstil ist ungewöhnlich (alles auf Deutsch, keine Zahl ohne
Messung) und ausdrücklich gewollt.

---

## Lizenz

**AGPL-3.0.** Benutzen, ändern, weitergeben und betreiben ist frei, auch
geschäftlich — wer die Software weitergibt **oder als Netzdienst anbietet**,
muss den Quelltext seiner Fassung offenlegen (§ 13 schliesst die Lücke, die die
gewöhnliche GPL bei Webanwendungen lässt).

Wer eine Fassung **ohne** diese Offenlegungspflicht braucht, wendet sich wegen
einer kommerziellen Lizenz an CRSOFT: [LICENSING.md](LICENSING.md).

© 2026 CRSOFT — Chris Reiter
