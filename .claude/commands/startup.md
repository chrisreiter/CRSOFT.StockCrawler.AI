---
description: Richtet CRSOFT.StockCrawler auf diesem Rechner ein — Werkzeuge, Datenbank, Modelle, erster Start
---

Richte diese Anwendung auf dem Rechner des Nutzers ein.

Lies dazu **`startUp.md`** im Wurzelverzeichnis dieses Repositorys und arbeite
die Abschnitte 0 bis 10 der Reihe nach ab. Die Datei enthält die verbindlichen
Anweisungen; halte dich an sie und nicht an dein Vorwissen über typische
Einrichtungen.

Vier Dinge, die dort stehen und leicht übergangen werden:

- **Prüfe vor jeder Installation**, ob das Werkzeug schon da ist. Was läuft,
  wird nicht angefasst.
- **Frage vor jedem Download über 2 GB.** `nemotron3:33b` sind 27,6 GB und
  brauchen ohne GPU 32 GB Arbeitsspeicher — das holst du nicht ungefragt. Die
  Anwendung läuft ohne dieses Modell vollständig.
- **Erfinde ein neues Datenbankkennwort** und frage den Nutzer, ob er es so
  will. Übernimm **nicht** die Voreinstellung aus den Skripten; sie stammt aus
  der Zeit, in der dieses Repository privat war.
- **Weise jeden Schritt nach**, bevor du den nächsten beginnst. Die Prüfbefehle
  stehen in `startUp.md`. Bei einem Fehlschlag hältst du an und berichtest,
  statt weiterzumachen — der nächste Schritt verdeckt sonst die Ursache.

Am Ende gib eine Übersicht in dieser Form:

| Bestandteil | Zustand | Anmerkung |
| --- | --- | --- |
| .NET SDK | … | |
| SQL Server | … | |
| Schema | … | bis 043 |
| Ollama · bge-m3 | … | Laufzeitbedarf, nicht Zubehör |
| Ollama · nemotron3:33b | … | optional |
| Qdrant | … | |
| Anwendung | … | http://localhost:5011 |
| Erster Verwalter | … | |
| Kursdaten | … | |

Nenne dabei ausdrücklich, was **fehlt und optional ist** — und was fehlt und
**nicht** optional ist. Ohne `bge-m3` bleiben Day Trading und Tagesjournal
inhaltsleer, ohne dass etwas abstürzt; das muss der Nutzer wissen, sonst sucht
er später einen Fehler, der keiner ist.
