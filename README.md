# CRSOFT.StockCrawler.AI

**An open laboratory for market analysis and forecasting — published together
with its negative results.**

It collects prices, news and specialist literature into *one* data model,
studies the interactions inside it, produces self-learning forecasts — and then
measures, without flattery, how much of that actually carries.

That last clause is the difference. Everything here is tested against a
holdout, and where a method achieves nothing, the interface says so.

---

## The numbers

| | |
| ---: | --- |
| **641** | tracked instruments — equities, ETFs, crypto, indices, FX |
| **3,941,331** | bars, daily and hourly, back to 2001 |
| **978,837** | live forecasts across seven horizons |
| **303,465** | of them already scored against the price that arrived |
| **397,011** | evaluated instrument pairs |
| **42,219** | sections of specialist literature, each with its source location |
| **11,644** | news articles from 35 sources worldwide |

And the number nobody else publishes:

> **The direction hit rate is 0.5109.** It would need to be **0.598** for a
> trade to cover its fees. **No method in this application beats the mere
> drift** in the holdout — the mean return of the training period, a single
> number.

That does not sit in the small print. It sits in the interface, at exactly the
spot where someone would otherwise take a figure at face value.

**This is not an admission. It is the product.** Anyone evaluating methods in
finance fights backtests that look too good and papers that only show what
worked. Here the whole instrument is open, including the parts that failed.

---

## What it does

- **Prices** from several providers in one unified model, worldwide, with
  honest handling of differing trading calendars and time zones
- **News, language-independently** — the embedding model is multilingual, so a
  German question finds a Japanese report with nothing translated anywhere
- **Specialist literature** in overlapping sections, every hit carrying source,
  page and a text anchor
- **Eight pillars**, weighted by *slider × measured merit* — a pillar without
  evidence moves nothing, even at a slider of one hundred
- **A reasoning model that may not invent a number**: every figure comes from a
  tool call, and the calls are printed beneath every answer
- **A virtual portfolio** with four autopilot strategies, including a
  counter-control that deliberately inverts the signal
- **Interface in German and English**, 2,272 strings each — a further language
  is one XML file and nothing else

Full detail: [UEBERBLICK.md](UEBERBLICK.md) *(German)*

---

## Getting started

```
claude "/startup"
```

Claude Code works through [`startUp.md`](startUp.md): check prerequisites,
.NET, SQL Server, schema, Ollama with `bge-m3`, Qdrant, first administrator,
price data. Without Claude Code, every step is also written out as a command.

Runs entirely locally — no cloud required. Prerequisites: .NET 8 SDK, SQL
Server, and for the knowledge and semantics pillars Ollama plus Qdrant.

### Signing in the first time

On a **development** run (`dotnet run`, which sets
`ASPNETCORE_ENVIRONMENT=Development`) the application creates an administrator
for you if the user table is still empty:

> **admin / admin**

The startup log says so, loudly. Change it under *System → Benutzer* before the
instance is reachable by anyone else, or delete the user — this repository is
public, so that password is public too.

**On a deployed instance this does not happen.** `2-install-target.ps1` sets
`ASPNETCORE_ENVIRONMENT=Production`, and there the original mechanism applies:
the start writes a one-time **setup word** into the log, and whoever can read
the log creates the first administrator with it. That is deliberate — “whoever
signs in first becomes administrator” is exactly the hole the sign-in is meant
to close.

---

## A note on language

**The codebase is German** — comments, documentation, domain identifiers and
several database columns. That is deliberate, and it is not going to change:
translating it was measured at about 143,500 words, and it would still leave
604 German identifiers and 264 German database columns behind. A half-translated
codebase is worse than an honest one.

**Two bridges make that workable:**

1. [`docs/GLOSSARY.md`](CRS.StockCrawler/docs/GLOSSARY.md) — the sixty terms
   that carry almost everything. `Sperrbereich` = holdout, `Trefferquote` = hit
   rate, `Erwartungswert` = expected value, `Nulllinie` = break-even.
2. **Claude Code reads German fluently and will talk to you in English.** Open
   any file and ask *“what does this do and why?”* — you get an English answer
   about German code, including the reasoning in the comments, which is where
   most of the value of this repository sits. The comments here are unusual:
   nearly every long one is the note on a failure that led to the current
   solution.

The user interface itself is fully localised and switches with one click.

---

## What it is **not**

**Not a trading system and not investment advice.** The application never says
what to buy. It answers the question before that one: what can be measured at
all, and does it cover its costs?

---

## Contributing

**The most valuable pull request is a refutation.** If one of the measured
statements does not hold, that is the best contribution this project can
receive — and it is meant seriously, not as a figure of speech.

Also very welcome: tests for `Ingest.Core/Analysis` (the mathematics is
deliberately dependency-free and easy to test, but barely covered), further
language files, and additional data providers.

Please read [CONTRIBUTING.md](CONTRIBUTING.md) first. The house style is
unusual and deliberate: everything in German, comments explain *why* rather
than *what*, and **no number without a measurement behind it.**

Contributions require a sign-off (`git commit -s`); a check enforces it. Why,
and what it means for your rights, is in [LICENSING.md](LICENSING.md).

---

## Licence

**AGPL-3.0.** Use, modify, redistribute and operate it freely, including
commercially — but anyone who distributes it **or offers it as a network
service** must publish the source of their version. (§ 13 closes the gap plain
GPL leaves open for web applications.)

If you need a version **without** that obligation — for a closed product —
contact CRSOFT about a commercial licence: [LICENSING.md](LICENSING.md).

© 2026 CRSOFT — Chris Reiter

---
---

# Auf Deutsch

Ein offenes Labor für Marktanalyse und Prognose — samt seiner Negativbefunde.
Es sammelt Kurse, Nachrichten und Fachliteratur in *ein* Datenmodell, untersucht
die Wechselwirkungen darin, erzeugt daraus selbstlernende Prognosen und misst
schonungslos nach, was davon trägt.

Der zentrale Befund steht in der Oberfläche und nicht im Kleingedruckten: Die
Richtungstrefferquote liegt bei **0,5109**, nötig wären **0,598**. Kein
Verfahren dieser Anwendung schlägt im Sperrbereich die blosse Drift.

| | |
| --- | --- |
| Vollständige Beschreibung | [UEBERBLICK.md](UEBERBLICK.md) |
| Einrichtung mit einem Befehl | [startUp.md](startUp.md) · `/startup` |
| Mitarbeiten und Hausstil | [CONTRIBUTING.md](CONTRIBUTING.md) |
| Lizenz und kommerzielle Ausnahme | [LICENSING.md](LICENSING.md) |
| Die rund hundert Regeln aus Fehlschlägen | `CRS.StockCrawler/CLAUDE.md` |

**Der wertvollste Pull Request ist eine Widerlegung.**
