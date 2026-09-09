# Abgelöst

`001_init.sql` / `002_stored_procedures.sql` gehören zum ursprünglichen Modell
mit getrennter `price_daily`-Tabelle (nur Tagesbars, EOD/CoinGecko fest verdrahtet).

Ersetzt durch `../010_unified_schema.sql` ff.: eine `price_bar`-Tabelle für
Aktien, ETFs und Krypto in 1h- und 1d-Auflösung, plus Analyse-/Prognose-Tabellen.
Nur noch als Referenz aufbewahrt — nicht mehr ausführen.
