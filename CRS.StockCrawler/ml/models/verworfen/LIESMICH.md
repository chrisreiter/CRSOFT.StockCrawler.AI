# Verworfene Modelle

Hier liegen Modelle, die gerechnet, gemessen und **nicht übernommen** wurden.
Sie stehen bewusst außerhalb von `ml/models/`, denn `DeepForecastService`
durchsucht dieses Verzeichnis nach `deep_*.json` und würde ein zweites Modell
für dasselbe Band bei jedem Start mit einer Warnung übergehen.

Gelöscht wird nichts: Ein Negativergebnis ist ein Ergebnis, und ohne die Datei
ist die Messung nicht nachvollziehbar.

## `deep_mid_nd` — entdriftetes mittleres Band

Gerechnet am 22.08.2026:

```
python ml/train_deep.py --csv D:/_data/stockcrawler/features_1d.csv --out ml/models \
    --name deep_mid_nd --horizons 10,20,60 --seq 96 --epochs 12 --patience 3 \
    --threads 8 --detrend
```

Die Drift des Trainingszeitraums wird vor dem Lernen von der Zielgröße
abgezogen und bei der Bewertung wieder hinzugefügt. Damit kann das Netz nicht
mehr scheinbar gewinnen, indem es „aufwärts“ sagt.

In Epoche 1 durchgängig besser als das rohe Modell (1,0354 / 1,0417 / 1,0757
gegen 1,0454 / 1,0632 / 1,0935), danach Überanpassung.

Im Sperrbereich:

| Horizont | Modell | Richtung | blosse Drift | Richtung |
| ---: | ---: | ---: | ---: | ---: |
| 10 | 1,0567 | 52,2 % | **0,9960** | **55,5 %** |
| 20 | 1,0531 | 52,1 % | **0,9927** | **57,5 %** |
| 60 | 1,0058 | 58,9 % | **0,9757** | **63,4 %** |

Die Drift schlägt es in jedem Horizont, auf beiden Maßen. `Carries()` würde es
also ohnehin sperren — es trägt nichts bei, und geladen zu werden hätte nur
eine Warnung erzeugt.
