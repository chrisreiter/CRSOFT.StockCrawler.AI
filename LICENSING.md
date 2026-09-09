# Lizenzierung · Licensing

*Deutsch zuerst, English below.*

---

## Kurz

CRSOFT.StockCrawler steht unter der **GNU Affero General Public License,
Version 3** (`LICENSE`). Wer damit ein **proprietäres, nicht quelloffenes**
Produkt bauen will, braucht stattdessen eine **kommerzielle Lizenz von CRSOFT**.

Beides zusammen heißt *Doppellizenzierung*: dieselbe Software, zwei
Bedingungen, und der Nutzer wählt.

---

## Was die AGPL erlaubt und verlangt

**Erlaubt:** benutzen, studieren, ändern, weitergeben, betreiben — auch
geschäftlich, auch ohne zu fragen.

**Verlangt:** Wer die Software weitergibt **oder als Netzdienst betreibt**, muss
den vollständigen Quelltext seiner Fassung unter derselben Lizenz zugänglich
machen — einschließlich der eigenen Änderungen.

**Warum AGPL und nicht GPL.** Dies ist eine Webanwendung. Die gewöhnliche GPL
verlangt die Offenlegung erst bei der *Weitergabe* der Software; wer sie nur als
Dienst über das Netz anbietet, gibt sie nie weiter und muss nach GPL nichts
offenlegen. Genau diese Lücke schließt **§ 13 der AGPL** („Remote Network
Interaction“): Wer den Dienst anbietet, muss den Quelltext anbieten. Für ein
Projekt, dessen übliche Nutzungsform ein Server ist, ist die GPL deshalb die
falsche Wahl.

---

## Was die AGPL **nicht** leistet — und das ist wichtig

**Die AGPL verbietet kommerzielle Nutzung nicht.** Wer den StockCrawler
weiterentwickelt, betreibt und dafür Geld nimmt, darf das — er muss nur seinen
Quelltext unter AGPL offenlegen. Er muss CRSOFT dafür **nicht** fragen.

Die Abstimmungspflicht greift dort, wo jemand **nicht offenlegen will**: in
einem geschlossenen Produkt, in einem Dienst ohne Quelltextherausgabe, in einer
Fassung, die mit unfreiem Code verbunden ist. Das erlaubt die AGPL nicht, und
dann führt der einzige Weg über eine kommerzielle Lizenz — also über CRSOFT.

Wer **jede** geschäftliche Nutzung von seiner Zustimmung abhängig machen will,
braucht eine Lizenz, die *keine* Open-Source-Lizenz mehr ist (etwa PolyForm
Noncommercial oder die Business Source License). Das ist eine legitime Wahl,
aber sie kostet genau das, was hier gewollt ist: Andere Entwickler arbeiten an
solchen Projekten deutlich seltener mit, und auf den einschlägigen Listen
gelten sie nicht als Open Source.

---

## Kommerzielle Lizenz

Wer eine Fassung ohne Offenlegungspflicht braucht, wendet sich an:

> **CRSOFT** · Chris Reiter · <crsoft.eu@gmail.com>

Konditionen werden im Einzelfall vereinbart. Ohne eine solche Vereinbarung gilt
ausschließlich die AGPL-3.0.

---

## Woran das hängt: die Beiträge anderer

Eine kommerzielle Lizenz kann nur vergeben, wer die Rechte an **allem** Code
hält. Sobald jemand einen Beitrag leistet, gehören dessen Rechte zunächst ihm —
und CRSOFT könnte diesen Teil nicht mehr kommerziell weiterlizenzieren, ohne ihn
zu fragen. **Ein einziger angenommener Pull Request ohne Vereinbarung legt das
Projekt dauerhaft auf AGPL-only fest.**

Deshalb verlangt `CONTRIBUTING.md` von jedem Beitragenden eine
Rechteeinräumung (CLA). Sie nimmt niemandem etwas: Der Beitragende behält sein
Urheberrecht und darf seinen Beitrag beliebig weiterverwenden. Er räumt CRSOFT
lediglich zusätzlich das Recht ein, ihn auch unter anderen Bedingungen zu
lizenzieren.

---

## Fremde Texte: Verweise ja, Inhalte nein

Die Anwendung liest Fachliteratur und Nachrichten ein und bettet sie ein. Das
wirft eine Urheberrechtsfrage auf, und die Antwort steckt in der Trennung
zwischen **Verweis** und **Inhalt**.

**Im Repository liegen ausschliesslich Verweise.** `infra/quellen.json` führt
35 Nachrichtenfeeds und 16 Fachquellen mit Titel, Adresse und **Rechtslage je
Eintrag**:

| | |
| ---: | --- |
| 12 | `gemeinfrei` — Klassiker aus dem Project Gutenberg (Lefèvre 1923, Selden 1912, Harper 1926 …) |
| 2 | `arXiv, offener Zugang` |
| 1 | `offener Zugang` — Dissertation zu Marktmikrostruktur |
| 1 | `BIZ, frei` — Arbeitspapier zum Hochfrequenzhandel |

Eine Literaturliste ist keine Vervielfältigung. Sie darf bleiben, und sie soll
bleiben: Ohne sie wäre nicht nachvollziehbar, worauf sich die Wissenssäule
stützt. Dass die Bücher hundert Jahre alt sind, ist übrigens keine Notlösung —
gemeinfrei ist, was neuere Literatur nicht ist.

**Der eingelesene Text liegt nicht im Repository.** Er entsteht erst auf dem
Rechner des Betreibers, wenn dieser die Quellen abruft, und landet in seiner
Datenbank und seinem Qdrant. Wer den StockCrawler klont, bekommt die Liste und
holt sich die Texte selbst — bei den gemeinfreien Werken ohne jede
Einschränkung.

**Daraus folgt eine Grenze für das Auslieferungsbündel.** `deploy/` erzeugt ein
Paket mit vollständigem Datenbank-Backup und beiden Qdrant-Sammlungen. Darin
**sind** die eingebetteten Texte enthalten. Dieses Bündel ist für den eigenen
Server gedacht und gehört nicht veröffentlicht — nicht wegen der Klassiker,
sondern wegen der Nachrichtenartikel, die keineswegs gemeinfrei sind.

**Zwei Dokumente aus dem Bestand des Autors sind deshalb nicht dabei.** Die
Masterarbeit *„Trading Skills"* (Susanne Pichler, JKU Linz 2024) steht unter
fremdem Urheberrecht — `docs/QUELLE-TRADING-SKILLS.md` beschreibt sie und
begründet ihre Aufnahme, mehr geht nicht. *„Marktneutrale Strategien im
Kryptomarkt"* ist dagegen eigener Text und liegt unter
`docs/wissen/` im Repository.

**Wer eigene Literatur einbettet, prüft ihre Rechtslage selbst.** Die Anwendung
nimmt jedes PDF an, das man ihr gibt. Sie kann nicht wissen, ob man es
weitergeben darf, und behauptet es auch nicht.

---

## Fremdbestandteile

| | |
| --- | --- |
| uPlot (`wwwroot/vendor/uplot`) | MIT |
| Dapper, Serilog, Cronos, SSH.NET, PdfPig, Swashbuckle, xunit | MIT bzw. Apache-2.0 |
| Microsoft.Data.SqlClient, Microsoft.Extensions.*, ONNX Runtime | MIT |

Alle sind permissiv lizenziert und mit der AGPL verträglich. Ihre Lizenztexte
bleiben unberührt; die AGPL gilt für das Werk als Ganzes und für den Code von
CRSOFT.

---
---

# English

## Short version

CRSOFT.StockCrawler is licensed under the **GNU Affero General Public License,
version 3** (see `LICENSE`). Anyone who wants to build a **proprietary,
closed-source** product from it needs a **commercial licence from CRSOFT**
instead.

## What the AGPL grants and requires

You may use, study, modify, redistribute and operate the software — including
commercially, and without asking. In return, anyone who distributes it **or runs
it as a network service** must make the complete source of their version
available under the same licence.

**Why AGPL rather than GPL:** this is a web application. Plain GPL triggers on
*distribution*; someone who only offers the software as a hosted service never
distributes it and therefore owes nothing. **AGPL § 13 (“Remote Network
Interaction”)** closes exactly that gap.

## What the AGPL does *not* do

**It does not prohibit commercial use.** Selling a service built on this code is
allowed, provided the source stays open under AGPL. No permission from CRSOFT is
needed for that.

CRSOFT's agreement becomes necessary where someone does **not** want to publish
their source — a closed product, a hosted service without source, or a
combination with non-free code. The AGPL does not permit that, so the only route
is a commercial licence.

If you want *every* commercial use to require the owner's consent, you need a
licence that is no longer an open-source licence (PolyForm Noncommercial, BSL).
That is a legitimate choice, but it costs the thing intended here: outside
contributors are markedly rarer on such projects.

## Commercial licence

> **CRSOFT** · Chris Reiter · <crsoft.eu@gmail.com>

Terms are agreed case by case. Without such an agreement, AGPL-3.0 applies
exclusively.

## Why contributions need an agreement

A commercial licence can only be granted by someone holding rights to **all** of
the code. Rights in a contribution initially belong to its author, so CRSOFT
could not re-license that part commercially without asking. **A single merged
pull request without an agreement fixes the project on AGPL-only for good.**

`CONTRIBUTING.md` therefore asks every contributor for a grant of rights (CLA).
It takes nothing away: contributors keep their copyright and may reuse their own
work freely. They merely additionally grant CRSOFT the right to license it under
other terms as well.
