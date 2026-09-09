# Glossary — German → English

This codebase is written in German: comments, documentation, domain identifiers
and database columns. That is deliberate and explained in
[CONTRIBUTING.md](../../CONTRIBUTING.md).

**This page is the bridge.** With it you can read the code without speaking
German. Roughly sixty terms carry almost all of it — measured: 604 distinct
German identifiers appear in the C# sources, but the fifty below account for
the great majority of the 2,705 occurrences.

If your editor has Claude in it, you have a second bridge: it reads German
fluently and will explain any file to you in English. Ask it *“what does this
class do?”* and you will get an English answer about German code.

---

## The concepts that carry the project

These are not vocabulary — they are the ideas the whole system is built on. If
you learn only ten terms, learn these.

| German | English | What it means here |
| --- | --- | --- |
| **Sperrbereich** | holdout | Data the training never saw. Every claim of forecast skill must be measured here — a good backtest counts for nothing |
| **Trefferquote** | hit rate | Share of forecasts that got the *direction* right. Currently **0.5109** |
| **Nötige Trefferquote** | required hit rate | What it would take to cover fees: `0.5·(round trip / E\|r\| + 1)` = **0.598** |
| **Erwartungswert** | expected value | `(2p − 1)·E\|r\| − round trip`. The number that decides, not the hit rate |
| **Rundlauf** | round trip | Both legs of a trade. Fees and slippage fall due twice — 0.30 % by default |
| **Drift** | drift | The mean return of the training period. **A single number that beats every model here** — the second and harder bar after standing still |
| **Stillstand** | standing still | The naive “price stays where it is”. The first bar |
| **Fehlerverhältnis** | error ratio | Model error ÷ error of standing still. Below 1 means better than doing nothing |
| **Verdienst** | merit | Measured edge of a pillar in the holdout, 0…1. Zero means it contributes nothing — regardless of its slider |
| **Rückhalt** | backing | Evidence outside the fitting period. Without it, a finding is an observation, not a signal |
| **Säule** | pillar | One of the eight methods feeding the forecast |
| **Nulllinie** | break-even | Capital still invested per unit. *Not* the entry price — it changes on a sale |

---

## Domain vocabulary

| German | English |
| --- | --- |
| Anteile | units, shares held |
| Anwärter | candidate (for the target basket) |
| Aufschlag | surcharge (pillar that adds *on top of* a base estimate) |
| Bewährung | track record |
| Beschluss | decision (of the autopilot) |
| Buchung | booking, transaction |
| Depot | portfolio |
| Einsatz | stake |
| Gebühr / Gebühren | fee / fees |
| Geschäft | trade |
| Grundlage | foundation (pillar that estimates the return itself) |
| Grundlinie | baseline (buy and hold) |
| Handelskosten | trading costs |
| Hysterese | hysteresis (the margin a candidate must beat to displace a holding) |
| Konto / Kontostand | account / account balance |
| Kreuzung | crossing (two normalised curves swapping places) |
| Kurs / Kurse | price / prices |
| Kurvendiskussion | curve discussion (highs, lows, inflection and saddle points) |
| Lauf | run |
| Leitstrategie | leading strategy (the one that counts towards total wealth) |
| Neuzugang | new listing |
| Quelle | source |
| Rangfolge | ranking |
| Säulenmischung | pillar mixture |
| Schätzung | estimate |
| Startkapital | starting capital |
| Stimmung | sentiment |
| Umschichtung | reallocation |
| Vermögen | wealth |
| Vorlauf | lead (in time) |
| Währung | currency |
| Zielkorb | target basket |
| zählt | counts (towards total wealth) |

---

## The four autopilot strategies

| | |
| --- | --- |
| `streng` | *strict* — trades only with demonstrated edge. Usually does nothing, and **that is the result**, not a fault |
| `aktiv` | *active* — follows the model's expectation even without proof |
| `halten` | *hold* — the baseline. Bought once, never touched. Pays the same fees, because a fee-free baseline would be unbeatable and therefore worthless |
| `invers` | *inverse* — the counter-control. Holds the **worst**-rated values, so that `aktiv` − `invers` measures the information content of the signal, free of market drift and costs |

---

## Database tables

Table names are English; several columns are German.

| Table | What it holds |
| --- | --- |
| `asset`, `price_bar`, `corp_action` | instruments and their bars |
| `pair_stat`, `crossing` | pairwise statistics and crossings |
| `forecast`, `forecast_score`, `forecast_component` | forecasts and their scoring |
| `curve_event`, `curve_link`, `curve_run` | curve discussion |
| `knowledge_source`, `knowledge_chunk` | sources and embedded sections |
| `pillar_weight`, `pillar_skill` | slider and measured merit per pillar |
| `invest_buchung`, `invest_konto`, `invest_kontobewegung` | the virtual portfolio |
| `autopilot_lauf`, `autopilot_entscheidung`, `autopilot_einstellung` | runs, decisions, settings |
| `neuzugang`, `neuzugang_lauf` | new listings |
| `bot_trigger_stat`, `reversal_pair`, `timezone_lead_stat` | bot herd, reverse conclusion, time-zone lead |

Recurring German columns: `depot`, `waehrung` (currency), `anteile` (units),
`gebuehr` (fee), `betrag` (amount), `kurs` (price), `notiz` (note), `takt`
(interval), `werte` (count of values), `zaehlt` (counts), `beschluss`
(decision), `grund` (reason), `quelle` (source), `titel` (title).

---

## Two false friends

**`Kurs` is a price, not a course.** `Kursverlauf` is a price history.

**`Werte` means *instruments*, not *values* in the numeric sense** — `641
verfolgte Werte` is “641 tracked instruments”. Where a number is meant, the
code says `Wert` in context or uses an explicit name.

---

## Why the code was not translated

Measured before deciding: translating the comments and documentation would be
**1,027,487 characters — about 143,500 words**, roughly one and a half novels
of dense technical prose. And it would not even solve the problem, because 604
German identifiers and 264 German database columns would remain. Renaming those
is not a translation but a refactor through the whole stack — a schema
migration, changed JSON field names, changed endpoint paths, and `app.js` reads
exactly those field names.

The cost is real and the benefit is small when a language model sits in the
editor. This glossary costs an hour and gets you most of the way.

What **is** translated: the user interface, fully, in German and English —
2,272 strings each. See [SPRACHEN.md](SPRACHEN.md).
