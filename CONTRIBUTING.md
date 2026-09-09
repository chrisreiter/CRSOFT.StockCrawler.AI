# Mitarbeiten · Contributing

*Deutsch zuerst, English below.*

---

## Die Rechteeinräumung, und warum es sie gibt

Dieses Projekt ist doppelt lizenziert: **AGPL-3.0 für alle**, dazu eine
kommerzielle Lizenz von CRSOFT für den, der nicht offenlegen will
(siehe [LICENSING.md](LICENSING.md)).

Das funktioniert nur, solange CRSOFT die Rechte an **allem** Code hält. **Ein
einziger angenommener Pull Request ohne Vereinbarung legt das Projekt dauerhaft
auf AGPL-only fest** — CRSOFT dürfte diesen Teil dann nicht mehr kommerziell
weiterlizenzieren.

Mit dem Absenden eines Pull Requests erklären Sie deshalb:

> Ich bin berechtigt, diesen Beitrag einzubringen. Ich stelle ihn unter der
> AGPL-3.0 zur Verfügung und räume CRSOFT (Chris Reiter) zusätzlich das nicht
> ausschließliche, unwiderrufliche, weltweite Recht ein, ihn auch unter anderen
> Bedingungen zu lizenzieren, einschließlich kommerzieller Lizenzen.
>
> Mein Urheberrecht bleibt bei mir. Ich darf meinen Beitrag weiterhin beliebig
> selbst verwenden.

Bestätigen Sie das, indem Sie jeden Commit mit `-s` signieren
(`git commit -s`) — das setzt die Zeile `Signed-off-by:` und gilt hier als
Zustimmung zu den obigen Sätzen.

Wenn Sie **im Auftrag eines Arbeitgebers** beitragen, klären Sie bitte vorher,
dass Sie dazu berechtigt sind.

---

## Wie hier gearbeitet wird

Der Projektstil ist ungewöhnlich und ausdrücklich gewollt. Wer ihn nicht
mitträgt, wird sich über die Anmerkungen zu seinem Pull Request ärgern —
deshalb steht er hier.

**Alles auf Deutsch.** Quelltextkommentare, Oberflächentexte, Dokumentation,
Commit-Nachrichten. Bezeichner im Code ebenso, soweit sie fachlich sind
(`Erwartungswert`, `Trefferquote`, `Nulllinie`). Die englischen Namen aus dem
Rahmenwerk bleiben, wie sie sind.

**Ein Kommentar erklärt das WARUM, nicht das WAS.** Was der Code tut, steht im
Code. Wertvoll ist die Überlegung dahinter — besonders die verworfene
Alternative und der Fehler, der zur jetzigen Lösung geführt hat. Fast jeder
längere Kommentar in diesem Bestand ist die Notiz zu einem Fehlschlag.

**Keine Zahl ohne Messung.** Der Satz „gemessen“ verpflichtet. Wer eine Zahl in
Kommentar, Dokumentation oder Oberfläche schreibt, muss sagen können, woher sie
kommt. Eine plausible Vermutung wird als Vermutung gekennzeichnet oder
weggelassen. Siehe `CLAUDE.md`, Abschnitt *„Nicht als gemessen aufschreiben,
was nur vermutet ist.“*

**Ein Negativergebnis ist ein Ergebnis.** Dieses Projekt hat bislang **kein**
Verfahren hervorgebracht, das im Sperrbereich die blosse Drift schlägt. Das
steht so in der Oberfläche und soll dort stehenbleiben. Beiträge, die eine
Prognosegüte behaupten, ohne sie im Sperrbereich zu belegen, werden nicht
übernommen — auch dann nicht, wenn die Rückrechnung gut aussieht.

**Oberflächentexte gehören in die Sprachdateien.** `infra/loc/loc.res.de.xml`
ist der Katalog; wer eine deutsche Formulierung ändert, ändert sie dort. Wer
Text in `index.html` oder `app.js` ändert, verliert dessen Übersetzung still.
Einzelheiten in [docs/SPRACHEN.md](CRS.StockCrawler/docs/SPRACHEN.md).

**Lesen Sie `CLAUDE.md`, bevor Sie etwas ändern.** Die Datei ist lang und keine
Formalität: Sie enthält rund hundert Regeln, die alle aus einem konkreten
Fehlschlag stammen — vom Handelskalender, der den Markt vortäuscht, bis zum
XML-Kommentar, der kein `--` enthalten darf. Wer sie überspringt, findet die
Fallen ein zweites Mal.

---

## Bauen und laufen lassen

```bash
cd CRS.StockCrawler
dotnet build
powershell -File infra/apply-sql.ps1     # Schema einspielen, idempotent
cd src/Ingest.Api && dotnet run          # http://localhost:5011
```

Voraussetzungen: .NET 8 SDK, SQL Server. Für die Wissens- und Semantiksäule
zusätzlich Ollama mit `bge-m3` und Qdrant — ohne sie bleiben Day Trading und
Tagesjournal inhaltsleer, aber der Rest läuft.

---

## Was besonders willkommen ist

- **Widerlegungen.** Wenn eine der gemessenen Aussagen nicht hält, ist das der
  wertvollste Beitrag überhaupt. Bitte mit der Gegenrechnung.
- **Tests für `Ingest.Core/Analysis`.** Die Mathematik ist bewusst
  abhängigkeitsfrei und gut testbar, hat aber kaum Abdeckung.
- **Weitere Sprachdateien.** `loc.res.de.xml` kopieren, Kürzel im Dateinamen
  ändern, Werte übersetzen, Schlüssel unangetastet lassen.
- **Datenanbieter.** `IMarketDataProvider` bzw. `IUniverseProvider`
  implementieren und in `DependencyInjection` registrieren.

---
---

# English

## The grant of rights, and why it exists

This project is dual-licensed: **AGPL-3.0 for everyone**, plus a commercial
licence from CRSOFT for those who do not want to publish their source (see
[LICENSING.md](LICENSING.md)).

That only works while CRSOFT holds rights to **all** of the code. **A single
merged pull request without an agreement fixes the project on AGPL-only for
good.**

By submitting a pull request you therefore state:

> I am entitled to submit this contribution. I provide it under AGPL-3.0 and
> additionally grant CRSOFT (Chris Reiter) the non-exclusive, irrevocable,
> worldwide right to license it under other terms as well, including commercial
> licences.
>
> I retain my copyright and may continue to use my own contribution freely.

Confirm this by signing off each commit (`git commit -s`), which adds the
`Signed-off-by:` line and counts here as agreement to the above.

If you contribute **on behalf of an employer**, please make sure beforehand that
you are entitled to do so.

## House style

**Everything is in German** — comments, UI text, documentation, commit
messages, and domain identifiers. Framework names stay as they are. This is
deliberate; please do not translate the codebase.

**Comments explain WHY, not WHAT** — especially the alternative that was
rejected and the failure that led to the current solution.

**No number without a measurement.** If you write a figure into a comment, the
docs or the UI, you must be able to say where it came from.

**A negative result is a result.** This project has produced no method that
beats the mere drift in the holdout. That statement stands in the UI and should
stay there. Contributions claiming forecast skill without holdout evidence are
not merged.

**UI strings belong in the language files** (`infra/loc/loc.res.de.xml`).

**Read `CLAUDE.md` first.** It is long and not a formality: roughly a hundred
rules, each derived from a concrete failure.
