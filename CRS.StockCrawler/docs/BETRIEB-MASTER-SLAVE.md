# Betrieb: Master, Slave und Gastzugang

**CRSOFT.StockCrawler**, Stand 17.09.2026

Zwei Instanzen, eine Datenbank in Replikation: Die **Hauptinstanz (Master)**
steht dort, wo Rechenleistung ist — sie holt Kurse, rechnet Prognosen,
befragt die Modelle und schreibt. Der **Live-Server (Slave)** läuft gegen ein
Replikat der Datenbank, hat keine GPU und zeigt nur, was ankommt. Dazu ein
**Gastzugang** ohne Kennwort für Besucher, auf beiden Instanzen möglich.

---

## Konfiguration

`appsettings.json`, Block `Betrieb`:

```jsonc
"Betrieb": {
  "Rolle": "master",        // master | slave
  "GastZugang": true,       // Anmeldung als Gast ohne Kennwort
  "GastHinweis": "…",       // optional: Text auf Anmeldeseite und im Band
  "GastKurz": "…",          // optional: Kurzform im Band
  "SlaveKurz": "…"          // optional: Kurzform im Band eines Slave
}
```

Auf dem Live-Server per Umgebungsvariable, damit dieselbe Auslieferung auf
beiden Rechnern läuft:

```
Betrieb__Rolle=slave
Betrieb__GastZugang=true
ConnectionStrings__Postgres=Host=…;Database=stockcrawler;Username=…;Password=…   # das Replikat
```

Unbekannte Werte für `Rolle` gelten als `master` — eine vertippte
Konfiguration macht den Live-Server also **nicht** stillschweigend zum Leser,
sondern er versucht zu schreiben und scheitert laut am Replikat. Das ist
gewollt: Ein Slave, der aus Versehen einer ist, wäre schwerer zu finden als
ein Master, der aus Versehen einer ist.

---

## Was der Slave tut und lässt

| | Master | Slave |
| --- | --- | --- |
| Zeitplan (Kurse, Prognose, Bewertung, Analyse, Urteile) | läuft | **aus** — `CronScheduler` beendet sich beim Start mit Protokollzeile |
| GET-Endpunkte | alle | alle ausser Modellaufrufen |
| POST/PUT/DELETE | Verwalter | **niemand** — auch nicht der Verwalter, Antwort 403 „Diese Instanz ist ein Replikat" |
| Modellaufrufe (`/api/reasoning/ask`, `/api/knowledge/search`, `/api/reasoning/log`, `/api/reasoning/urteile/lauf`, `/api/vlm`) | Verwalter/Nutzer | **gesperrt**, 403 „keine Modelle bereit" |
| Sitzungen | `app_session` | **im Speicher** — das Replikat nimmt keine Schreibvorgänge; Neustart meldet alle ab |
| Fehlversuche zählen / Sperre setzen | ja | nein — die Sperre des Masters wird mitrepliziert und gilt |
| Oberflächenzustand (`/api/state`) | gespeichert | nur in der Sitzung |
| Kopfzeile | – | Band „Replikat · nur lesend, keine Modelle", Kennung neben dem Namen |

**Warum die Regel an der Instanz hängt und nicht an der Rolle.** Ein
Verwalter, der sich am Slave anmeldet, ist dort trotzdem nur Leser. Die
Datenbank gibt es nicht anders her, und ein 403 mit Erklärung ist die
bessere Antwort als ein Datenbankfehler mitten in einem Lauf.

**Das Tagesjournal bleibt auf dem Slave lesbar**, obwohl es eine Wissenssuche
enthält: Fehlt bge-m3, liefert die Suche nichts, und das Journal steht ohne
den Abschnitt da — dieselbe Degradation wie überall, wenn Ollama fehlt.

---

## Gastzugang

Anmeldeseite: unter dem Anmeldefeld „Als Gast ansehen" mit dem Hinweistext
der Instanz. Der Server erlaubt es nur bei `GastZugang: true`; ein Gast, der
sich mit Kennwort anmelden will, bekommt die Meldung, dass keines nötig ist.
Ist der Zugang aus, unterscheidet sich die Antwort nicht von einem falschen
Kennwort — aus der Meldung soll sich nicht ablesen lassen, ob es den Weg gibt.

**Was der Gast ist:** eine Sitzung im Speicher ohne Zeile in `app_user`
(`Angemeldet.Gast`, Rolle `guest`). Er hinterlässt keine Spur in der
Datenbank — kein Zustand, kein Kennwort, kein Protokoll.

**Was er darf:** alles lesen, was ein Nutzer liest — ausser den Modellaufrufen
(Liste oben) und dem Antwortprotokoll des Agenten, das die Fragen des
Verwalters enthält. Alles andere: 403 mit dem Hinweistext.

**Was er sieht:** dauerhaft das Band „Open Lab Demo · Gastzugang, nur lesend"
über der Kopfzeile, die Kennung neben dem Namen, und alle Aktionsknöpfe
gesperrt. Die Sperre im Browser ist Bequemlichkeit; die Grenze ist der
Server.

---

## Die Durchsetzung — eine Stelle

`Anmeldepflicht.cs`, in dieser Reihenfolge:

1. Ohne Sitzung: nur `anmeldung.html`, `/api/auth/status|login|einrichten`, `/api/health`.
2. **Slave:** nur GET/HEAD/OPTIONS, keine Modellaufrufe, Abmelden erlaubt.
3. **Gast:** nur GET/HEAD/OPTIONS, keine Modellaufrufe, Abmelden erlaubt — kein `/api/state`, kein `/api/auth/passwort`.
4. **Nutzer:** wie bisher — GET plus `/api/state`, eigenes Kennwort, Abmelden.
5. Benutzerverwaltung auch lesend nur für Verwalter.

Die Modellaufruf-Liste ist die einzige Pfadliste und mit Absicht kurz — sie
ist die Ausnahme von der Methodenregel, und Ausnahmen muss man pflegen.

---

## Deployment mit Replikation

1. Master wie bisher (`startUp.md`), Datenbank als Primär.
2. Replikat einrichten — PostgreSQL: Streaming Replication mit Hot Standby
   (`hot_standby = on`), SQL Server: Always-On-Lesereplikat oder
   Log-Versand mit Standby-Modus. Die Anwendung stellt an das Replikat keine
   Anforderung ausser: lesbar.
3. Slave: dieselbe Auslieferung, `Betrieb__Rolle=slave`,
   Verbindungszeichenfolge auf das Replikat, **kein Ollama, kein Qdrant
   nötig** — Wissen und Semantik zeigen die Listen aus SQL; nur die
   Ähnlichkeitssuche fehlt (sie ist ohnehin gesperrt).
4. Prüfung nach dem Start: Protokollzeile „Betrieb als Slave: Zeitplan aus",
   `/api/auth/status` → `betrieb.istSlave: true`, ein POST als Verwalter → 403.

**Gemessen am 17.09.2026** (lokal, `Betrieb__Rolle=slave`): Anmeldung des
Verwalters 200 mit Sitzung im Speicher, `GET /api/start/` 200,
`POST /api/autopilot/lauf` 403, `POST /api/forecast/run` 403,
`GET /api/knowledge/search` 403 „keine Modelle", `POST /api/state` 403,
Zeitplan aus. Gast auf dem Master: 12 Pfade geprüft, lesend 200, schreibend
und Modelle 403, nach Abmelden 401.
