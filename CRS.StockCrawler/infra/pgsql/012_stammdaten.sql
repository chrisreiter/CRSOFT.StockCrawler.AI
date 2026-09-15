-- CRSOFT.StockCrawler -- Stammdaten, die die SQL-Server-Migrationen nebenbei
-- anlegen (028, 037, 038, 043). Ohne sie hat die Oberflaeche nichts zum
-- Einstellen, und die erste Buchung scheiterte an einem fehlenden Konto.
-- Idempotent: vorhandene Zeilen bleiben, wie sie sind (ON CONFLICT DO NOTHING).
--
-- Mehr Seeding braucht es nicht -- Werte, Kurse, Nachrichten und Literatur holt
-- die Anwendung selbst (startUp.md, Schritt 9).

SET search_path = dbo, public;

-- Vorgabegewichte der Saeulen (028). Begruendungen stehen dort.
INSERT INTO dbo.pillar_weight (pillar, weight, begruendung) VALUES
  ('learning',  50, 'Einziges Verfahren mit gemessener Trefferquote über dem Münzwurf.'),
  ('math',      20, 'Beschreibt zuverlässig, prognostiziert nicht — Gewicht wirkt auf die Befundauswahl.'),
  ('deep',      10, 'Kein Band schlägt die Drift; die Mischung setzt den Verdienst deshalb auf null.'),
  ('flow',      10, 'Kein eigener Zahlenbeitrag zur Prognose.'),
  ('knowledge',  5, 'Kein eigener Zahlenbeitrag zur Prognose.'),
  ('semantic',   5, 'Kein eigener Zahlenbeitrag zur Prognose.')
ON CONFLICT (pillar) DO NOTHING;

-- Die vier Autopilot-Strategien, AUSGESCHALTET angelegt (038, 043): Eine
-- Strategie, die von selbst zu handeln beginnt, weil jemand ein Schema
-- eingespielt hat, waere eine unangenehme Ueberraschung auf einem Konto.
INSERT INTO dbo.autopilot_einstellung (depot) VALUES
  ('streng'), ('aktiv'), ('halten'), ('invers')
ON CONFLICT (depot) DO NOTHING;

-- Je Depot und Waehrung ein Verrechnungskonto (037, 038, 043). Voreinstellung
-- 0,15 % je Vorgang -- die Haelfte des Rundlaufs von 0,3 %.
INSERT INTO dbo.invest_konto (depot, waehrung)
SELECT d.depot, w.waehrung
  FROM (VALUES ('manuell'), ('streng'), ('aktiv'), ('halten'), ('invers')) AS d(depot)
 CROSS JOIN (VALUES ('EUR'), ('USD')) AS w(waehrung)
ON CONFLICT (depot, waehrung) DO NOTHING;
