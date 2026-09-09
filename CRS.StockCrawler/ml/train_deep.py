"""
Säule „Deep Learning" — ein globales Sequenzmodell über alle Werte.

Aufruf:
    python ml/train_deep.py --csv src/Ingest.Api/export/features_1d.csv --out ml/models

Vier Entwurfsentscheidungen, jede mit ihrem Grund.

1. EIN Modell für alle Werte, nicht eines je Wert.
   Je Wert stehen rund 6.000 Tagesbars zur Verfügung — für ein tiefes Modell zu
   wenig, es lernt die Reihe auswendig. Global sind es über eine Million
   Beispiele, und das Modell lernt Muster, die über Werte hinweg gelten. Die
   Eigenheiten des einzelnen Wertes trägt eine Einbettung.

2. Ziel ist die Rendite, nicht der Kurs.
   Ein Modell auf absolute Kurse lernt zu neunundneunzig Prozent das Kursniveau
   und sieht dabei großartig aus, ohne etwas vorherzusagen — dieselbe Falle, in
   die die Zerlegung mit „99,6 % erklärte Streuung" gelaufen ist. Vorhergesagt
   wird die Log-Rendite, auf ihre eigene Streuung normiert.

3. Das Modell sagt seine Unsicherheit mit.
   Ausgegeben werden Mittelwert UND Streuung, trainiert über die
   Gauß-Log-Likelihood. Eine Prognose ohne Unsicherheit ist für die Gewichtung
   in der Säulenzusammenführung wertlos: Sie kann dann nicht schweigen, wenn sie
   nichts weiß, sondern behauptet immer gleich viel.

4. Ein Faltungsnetz, kein Transformer.
   Auf dieser Maschine gibt es keine CUDA-Karte. Ein Transformer über
   256 Zeitschritte und eine Million Beispiele wäre auf 24 Kernen Tage. Ein
   gestapeltes Faltungsnetz mit wachsender Schrittweite erreicht dasselbe
   Sichtfeld bei einem Bruchteil der Rechnung und ist auf CPU brauchbar.

5. Kurze und lange Horizonte werden GETRENNT trainiert.
   Der erste Durchgang lief über alle neun Horizonte zugleich und scheiterte
   daran: Die Streuung der Zielgröße reicht von 0,032 bei einem Tag bis 0,445
   bei 250 Tagen — Faktor vierzehn. Mit einer gemeinsamen Normierung stammt
   fast der ganze Verlust aus den langen Horizonten, und die kurzen werden
   praktisch nicht trainiert. Das Ergebnis war ein Fehlerverhältnis über eins
   ab der ersten Epoche, mit steigender Tendenz.

   Der Maßstab ist dabei nur die halbe Begründung. Kurzfristig überwiegen
   Rückkehr zum Mittel und Mikrostruktur, langfristig Trend und
   Faktorbindung — inhaltlich zwei verschiedene Aufgaben. Ein Modell für beide
   schließt einen Kompromiss, den keine der beiden braucht.

   Zusätzlich wird INNERHALB jeder Gruppe je Horizont normiert: Auch von 1 auf
   10 Tage liegt noch Faktor drei dazwischen.

Der Zeitsplit ist nicht verhandelbar: älteste Daten zum Trainieren, mittlere zum
Abstimmen, JÜNGSTE unangetastet bis zur Schlussbewertung. Ein zufälliger Split
wäre hier fatal — die Zeilen desselben Zeitpunkts über alle Werte hinweg sind
hochkorreliert, ein Zeitpunkt im Training verriete denselben Zeitpunkt im Test.
"""

from __future__ import annotations

import argparse
import json
import math
from pathlib import Path

import numpy as np
import pandas as pd
import torch
import torch.nn as nn


# --------------------------------------------------------------------- Modell

class TemporalBlock(nn.Module):
    """
    Ein Faltungsblock mit Sprungverbindung.

    Die Faltung ist *kausal*: Aufgefüllt wird nur links, und der Überhang rechts
    wird abgeschnitten. Ohne das sähe jeder Zeitschritt seine eigene Zukunft —
    der Fehler wäre im Training nicht zu bemerken und machte jedes Ergebnis
    wertlos.
    """

    def __init__(self, c_in: int, c_out: int, dilation: int, k: int = 3, p: float = 0.1):
        super().__init__()
        self.pad = (k - 1) * dilation

        self.conv1 = nn.Conv1d(c_in, c_out, k, dilation=dilation)
        self.conv2 = nn.Conv1d(c_out, c_out, k, dilation=dilation)
        self.norm1 = nn.GroupNorm(8, c_out)
        self.norm2 = nn.GroupNorm(8, c_out)
        self.drop = nn.Dropout(p)
        self.act = nn.GELU()

        self.skip = nn.Conv1d(c_in, c_out, 1) if c_in != c_out else nn.Identity()

    def forward(self, x):
        y = nn.functional.pad(x, (self.pad, 0))
        y = self.act(self.norm1(self.conv1(y)))
        y = self.drop(y)

        y = nn.functional.pad(y, (self.pad, 0))
        y = self.act(self.norm2(self.conv2(y)))
        y = self.drop(y)

        return self.act(y + self.skip(x))


class DeepForecaster(nn.Module):
    """
    Sequenz aus Merkmalen plus Wert- und Horizonteinbettung, Ausgabe Mittelwert
    und Log-Varianz.

    Die Horizonteinbettung ist der Grund, warum EIN Modell alle Horizonte
    bedient. Neun getrennte Modelle könnten nicht voneinander lernen — dass eine
    Lage auf fünf Tage hinweist, sagt auch etwas über zwanzig.
    """

    def __init__(self, n_features: int, n_assets: int, n_horizons: int,
                 channels: int = 64, layers: int = 6, emb_asset: int = 16,
                 emb_horizon: int = 8, dropout: float = 0.1):
        super().__init__()

        self.inp = nn.Conv1d(n_features, channels, 1)

        blocks = []
        for i in range(layers):
            blocks.append(TemporalBlock(channels, channels, dilation=2 ** i, p=dropout))
        self.blocks = nn.Sequential(*blocks)

        self.asset_emb = nn.Embedding(n_assets, emb_asset)
        self.horizon_emb = nn.Embedding(n_horizons, emb_horizon)

        self.head = nn.Sequential(
            nn.Linear(channels + emb_asset + emb_horizon, 128),
            nn.GELU(),
            nn.Dropout(dropout),
            nn.Linear(128, 2),
        )

    def forward(self, x, asset_idx, horizon_idx):
        # x: (Stapel, Zeit, Merkmale) -> Faltung erwartet (Stapel, Kanal, Zeit)
        h = self.inp(x.transpose(1, 2))
        h = self.blocks(h)

        # Nur der letzte Zeitschritt: er allein kennt die gesamte Vergangenheit.
        h = h[:, :, -1]

        z = torch.cat([h, self.asset_emb(asset_idx), self.horizon_emb(horizon_idx)], dim=1)
        out = self.head(z)

        mean = out[:, 0]

        # Log-Varianz begrenzen: unbeschränkt lernt das Netz, sich durch
        # behauptete Unsicherheit aus jeder Verantwortung zu ziehen.
        logvar = out[:, 1].clamp(-8.0, 4.0)

        return mean, logvar


def hits_pct(v: float) -> str:
    """Trefferquote lesbar, auch wenn sie fuer einen Horizont fehlt."""
    return 'n/a' if not np.isfinite(v) else f'{v:.3%}'


def gaussian_nll(mean, logvar, target):
    """
    Negative Log-Likelihood einer Normalverteilung.

    Gegenüber dem quadratischen Fehler hat sie den entscheidenden Vorteil, dass
    das Modell für eine ehrlich eingestandene Unsicherheit belohnt wird. Es kann
    damit sagen „ich weiß es nicht", statt in jeder Lage gleich laut zu raten.
    """
    return 0.5 * (logvar + (target - mean) ** 2 / logvar.exp()).mean()


# ----------------------------------------------------------------- Datensatz

class SequenceIndex:
    """
    Hält alle Merkmale in EINER zusammenhängenden Matrix und merkt sich je
    Beispiel nur, wo seine Sequenz endet.

    Zwei Gründe, und beide sind entscheidend.

    Speicher: Der naive Weg — jede Sequenz ausgeschnitten ablegen — bräuchte bei
    neun Millionen Beispielen, 64 Zeitschritten und 29 Merkmalen über ein
    Terabyte für dieselben Zahlen, jede vielfach kopiert. So sind es unter
    zweihundert Megabyte.

    Geschwindigkeit: Eine Python-Schleife über 512 Beispiele je Stapel, die
    jeweils 64x29 Werte kopiert, ist langsamer als das Netz selbst — das
    Training wartet dann auf die Datenaufbereitung statt auf die Rechnung. Mit
    einer einzigen Indexmatrix und einem Zugriff darauf entfällt die Schleife
    vollständig.

    Die Grenzen zwischen den Werten müssen dabei zwingend beachtet werden: Eine
    Sequenz darf nie über das Ende eines Wertes hinausreichen, sonst enthielte
    sie am Anfang die letzten Tage eines fremden Kurses. Deshalb beginnt jedes
    Beispiel frühestens seq_len Schritte nach dem Beginn seines Wertes.
    """

    def __init__(self, frame: pd.DataFrame, feature_cols: list[str],
                 horizons: list[int], seq_len: int):
        self.seq_len = seq_len
        self.horizons = horizons
        self.n_features = len(feature_cols)

        blocks = []
        target_blocks = []
        stamp_blocks = []

        self.asset_ids = []
        offsets = []
        cursor = 0

        for aid, grp in frame.groupby('asset_id', sort=True):
            grp = grp.sort_values('ts_utc')

            feats = grp[feature_cols].to_numpy(dtype=np.float32)
            if len(feats) < seq_len + 1:
                continue

            ai = len(self.asset_ids)
            self.asset_ids.append(int(aid))

            blocks.append(feats)
            target_blocks.append(
                np.stack([grp[f'y_h{h}'].to_numpy(dtype=np.float32) for h in horizons], axis=1))
            stamp_blocks.append(grp['ts_utc'].to_numpy())

            offsets.append((ai, cursor, len(feats)))
            cursor += len(feats)

        if not blocks:
            self.features = np.zeros((0, self.n_features), np.float32)
            self.samples = np.zeros((0, 3), np.int64)
            self.stamps = np.zeros(0, dtype='datetime64[s]')
            return

        self.features = np.concatenate(blocks, axis=0)
        targets = np.concatenate(target_blocks, axis=0)
        stamps = np.concatenate(stamp_blocks, axis=0)

        # Beispiele: absolute Endposition, Wertindex, Horizontindex.
        rows = []

        for ai, base, length in offsets:
            # Erst ab seq_len-1 innerhalb DIESES Wertes -- siehe Klassenkommentar.
            local = np.arange(seq_len - 1, length)
            absolute = base + local

            for hi in range(len(horizons)):
                ok = np.isfinite(targets[absolute, hi])
                sel = absolute[ok]

                if len(sel) == 0:
                    continue

                rows.append(np.stack([
                    sel,
                    np.full(len(sel), ai, dtype=np.int64),
                    np.full(len(sel), hi, dtype=np.int64)
                ], axis=1))

        self.samples = np.concatenate(rows, axis=0) if rows else np.zeros((0, 3), np.int64)
        self.targets = targets
        self.stamps = stamps.astype('datetime64[s]')

        # Indexmatrix der Sequenzen: (Beispiel, Zeitschritt) -> absolute Zeile.
        self._offsets = np.arange(-(seq_len - 1), 1, dtype=np.int64)

    def timestamps(self):
        """Zeitstempel je Beispiel — Grundlage des zeitlichen Splits."""
        return self.stamps[self.samples[:, 0]]

    def batch(self, rows: np.ndarray):
        sel = self.samples[rows]

        ends = sel[:, 0]
        a = sel[:, 1]
        hi = sel[:, 2]

        # Ein einziger Zugriff statt einer Schleife über den Stapel.
        idx = ends[:, None] + self._offsets[None, :]

        x = self.features[idx]
        y = self.targets[ends, hi]

        return x, a, hi, y


# --------------------------------------------------------------------- Ablauf

def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument('--csv', required=True)
    ap.add_argument('--out', default='ml/models')
    ap.add_argument('--seq', type=int, default=64)
    ap.add_argument('--epochs', type=int, default=8)
    ap.add_argument('--batch', type=int, default=512)
    ap.add_argument('--channels', type=int, default=64)
    ap.add_argument('--layers', type=int, default=6)
    ap.add_argument('--lr', type=float, default=1e-3)
    ap.add_argument('--threads', type=int, default=0)
    ap.add_argument('--horizons', default='',
                    help='Nur diese Horizonte trainieren, z.B. 1,2,3,5,10')
    ap.add_argument('--name', default='deep_forecaster',
                    help='Dateiname des Modells -- getrennte Laeufe brauchen getrennte Namen')
    ap.add_argument('--patience', type=int, default=2,
                    help='Nach so vielen Epochen ohne Verbesserung abbrechen')
    ap.add_argument('--dropout', type=float, default=0.2)
    ap.add_argument('--detrend', action='store_true',
                    help='Die Trainingsdrift vor dem Lernen aus der Zielgroesse nehmen. '
                         'Das Netz kann dann nur noch durch das punkten, was die Drift '
                         'nicht erklaert -- und nicht mehr scheinbar gewinnen, indem es '
                         '"aufwaerts" sagt.')
    args = ap.parse_args()

    if args.threads > 0:
        torch.set_num_threads(args.threads)

    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)

    print(f'lade {args.csv} …', flush=True)
    df = pd.read_csv(args.csv, parse_dates=['ts_utc'])

    horizons = sorted(int(c[3:]) for c in df.columns if c.startswith('y_h'))

    if args.horizons.strip():
        wanted = {int(x) for x in args.horizons.split(',') if x.strip()}
        missing = wanted - set(horizons)
        if missing:
            raise SystemExit(f'nicht im Export enthalten: {sorted(missing)}')
        horizons = [h for h in horizons if h in wanted]
    feature_cols = [c for c in df.columns
                    if c not in ('ts_utc', 'asset_id', 'symbol', 'asset_class')
                    and not c.startswith('y_h')]

    print(f'{len(df):,} Zeilen, {len(feature_cols)} Merkmale, Horizonte {horizons}', flush=True)

    df[feature_cols] = df[feature_cols].replace([np.inf, -np.inf], np.nan).fillna(0.0)

    index = SequenceIndex(df, feature_cols, horizons, args.seq)
    if len(index.samples) == 0:
        raise SystemExit('keine verwertbaren Beispiele')

    stamps = index.timestamps()

    # Zeitlicher Split, nicht zufällig. Siehe Kopfkommentar.
    q_train, q_val = np.quantile(stamps.astype('int64'), [0.65, 0.82])

    tr = np.where(stamps.astype('int64') <= q_train)[0]
    va = np.where((stamps.astype('int64') > q_train) & (stamps.astype('int64') <= q_val))[0]
    te = np.where(stamps.astype('int64') > q_val)[0]

    print(f'Beispiele  Training {len(tr):,}  Abstimmung {len(va):,}  Sperre {len(te):,}', flush=True)
    print(f'  Training bis      {stamps[tr].max()}', flush=True)
    print(f'  Abstimmung bis    {stamps[va].max()}', flush=True)
    print(f'  Sperrbereich bis  {stamps[te].max()}', flush=True)

    # Normierung ausschliesslich aus dem Trainingsbereich. Wuerde ueber alles
    # normiert, floesse ein Stueck Zukunft ueber Mittelwert und Streuung ins
    # Training -- ein leiser Fehler, der die Ergebnisse zuverlaessig schoent.
    #
    # Gerechnet wird direkt auf der Merkmalsmatrix, nicht auf ausgeschnittenen
    # Sequenzen. Der erste Entwurf baute dafuer 200.000 Sequenzen auf: 64 mal
    # dieselben Zahlen, 1,5 GB Speicher und Minuten Rechenzeit -- fuer einen
    # Mittelwert, der in den Rohzeilen unveraendert steht.
    train_rows = np.unique(index.samples[tr, 0])

    f_mean = index.features[train_rows].mean(axis=0)
    f_std = index.features[train_rows].std(axis=0) + 1e-8

    # ------------------------------------------------------------------
    # Die Drift vor dem Lernen herausnehmen.
    #
    # Bestimmt wird sie AUSSCHLIESSLICH auf dem Trainingsbereich. Wuerde der
    # Sperrbereich mitzaehlen, kennte das Netz beim Lernen bereits die mittlere
    # Rendite der Zukunft -- und der Vergleich waere wertlos.
    #
    # Beim Auswerten wird sie wieder aufgeschlagen. Nur so bleiben die Zahlen
    # mit den bisherigen vergleichbar: Ein Fehlerverhaeltnis von 0,96 auf einer
    # entdrifteten Zielgroesse waere eine ganz andere Groesse als eines auf der
    # rohen.
    drift_lernen = np.zeros(len(horizons), dtype=np.float32)

    if args.detrend:
        for hi in range(len(horizons)):
            sel = tr[index.samples[tr, 2] == hi]

            if len(sel) == 0:
                continue

            y_tr = index.targets[index.samples[sel, 0], hi]
            y_tr = y_tr[~np.isnan(y_tr)]

            if len(y_tr) > 0:
                drift_lernen[hi] = float(y_tr.mean())

        print('Drift aus der Zielgroesse genommen: ' +
              ', '.join(f'{h}={d:+.5f}' for h, d in zip(horizons, drift_lernen)),
              flush=True)

    # Je Horizont eine eigene Streuung -- siehe Punkt 5 im Kopfkommentar.
    y_std = np.empty(len(horizons), dtype=np.float32)

    for hi in range(len(horizons)):
        sel = tr[index.samples[tr, 2] == hi]
        vals = index.targets[index.samples[sel, 0], hi] if len(sel) else np.array([0.0])

        # Nach dem Abzug der Drift -- sonst passt die Normierung nicht zu dem,
        # was das Netz tatsaechlich zu lernen bekommt.
        y_std[hi] = float(np.nanstd(vals - drift_lernen[hi]) + 1e-12)

    print('Streuung je Horizont: ' +
          ', '.join(f'{h}={s:.4f}' for h, s in zip(horizons, y_std)), flush=True)

    model = DeepForecaster(len(feature_cols), len(index.asset_ids), len(horizons),
                           channels=args.channels, layers=args.layers,
                           dropout=args.dropout)

    opt = torch.optim.AdamW(model.parameters(), lr=args.lr, weight_decay=1e-4)
    sched = torch.optim.lr_scheduler.OneCycleLR(
        opt, max_lr=args.lr, total_steps=args.epochs * max(1, len(tr) // args.batch))

    def evaluate(rows: np.ndarray, limit: int = 120_000):
        """
        Getrennt je Horizont ausgewertet.

        Ein gemitteltes Fehlerverhaeltnis ueber alle Horizonte verdeckt genau
        das, was man wissen will: ob EIN Horizont traegt. Im ersten Durchgang
        stand dort eine einzige Zahl ueber eins -- ob darunter ein brauchbarer
        Horizont lag, war nicht zu erkennen.
        """
        model.eval()
        sel = rows if len(rows) <= limit else np.random.default_rng(0).choice(rows, limit, replace=False)

        em = np.zeros(len(horizons))
        en = np.zeros(len(horizons))
        hit = np.zeros(len(horizons))
        cnt = np.zeros(len(horizons))

        with torch.no_grad():
            for i in range(0, len(sel), 2048):
                x, a, h, y = index.batch(sel[i:i + 2048])
                x = (x - f_mean) / f_std

                mean, _ = model(torch.from_numpy(x), torch.from_numpy(a), torch.from_numpy(h))
                # Die Drift wieder aufschlagen -- gemessen wird gegen die
                # ROHE Zielgroesse, sonst ist der Vergleich mit den
                # bisherigen Zahlen keiner.
                pred = mean.numpy() * y_std[h] + drift_lernen[h]

                for hi in range(len(horizons)):
                    m = h == hi
                    if not m.any():
                        continue

                    em[hi] += np.abs(pred[m] - y[m]).sum()
                    en[hi] += np.abs(y[m]).sum()
                    hit[hi] += int((np.sign(pred[m]) == np.sign(y[m])).sum())
                    cnt[hi] += int(m.sum())

        model.train()

        ratio = np.where(en > 0, em / np.maximum(1e-12, en), np.nan)
        hits = np.where(cnt > 0, hit / np.maximum(1, cnt), np.nan)

        return ratio, hits

    rng = np.random.default_rng(20260821)
    best = math.inf
    stale = 0

    for ep in range(args.epochs):
        perm = rng.permutation(tr)
        run = 0.0
        steps = 0

        for i in range(0, len(perm) - args.batch, args.batch):
            x, a, h, y = index.batch(perm[i:i + args.batch])
            x = (x - f_mean) / f_std

            mean, logvar = model(torch.from_numpy(x), torch.from_numpy(a), torch.from_numpy(h))

            # Je Horizont normiert -- sonst stammt fast der gesamte Verlust aus
            # den langen, und die kurzen werden nie trainiert.
            loss = gaussian_nll(mean, logvar,
                                torch.from_numpy((y - drift_lernen[h]) / y_std[h]))

            opt.zero_grad()
            loss.backward()
            nn.utils.clip_grad_norm_(model.parameters(), 1.0)
            opt.step()
            sched.step()

            run += float(loss)
            steps += 1

            if steps % 25 == 0:
                print(f'  Epoche {ep + 1}  Schritt {steps}  Verlust {run / steps:.4f}', flush=True)

        ratio, hit = evaluate(va)
        mean_ratio = float(np.nanmean(ratio))

        print(f'Epoche {ep + 1}: Verlust {run / max(1, steps):.4f}  '
              f'Abstimmung Fehlerverhältnis {mean_ratio:.4f}', flush=True)

        for hi, h in enumerate(horizons):
            print(f'    h={h:<4d} Fehlerverhältnis {ratio[hi]:.4f}  '
                  f'Richtung {hits_pct(hit[hi])}', flush=True)

        if mean_ratio < best - 1e-5:
            best = mean_ratio
            stale = 0
            torch.save(model.state_dict(), out / f'{args.name}.pt')
        else:
            stale += 1

            # Frueh abbrechen. Im ersten Durchgang stieg der Abstimmungsfehler
            # vier Epochen in Folge, und die Rechnung lief trotzdem weiter --
            # fuenf Stunden fuer ein Ergebnis, das nach der ersten Epoche
            # feststand.
            if stale >= args.patience:
                print(f'kein Fortschritt seit {stale} Epochen — Abbruch', flush=True)
                break

    model.load_state_dict(torch.load(out / f'{args.name}.pt'))

    ratio_te, hit_te = evaluate(te)

    print('\nSPERRBEREICH', flush=True)
    for hi, h in enumerate(horizons):
        verdict = ('trägt' if ratio_te[hi] < 1 and hit_te[hi] > 0.523
                   else 'schlägt Stillstand' if ratio_te[hi] < 1
                   else 'trägt nicht')
        print(f'    h={h:<4d} Fehlerverhältnis {ratio_te[hi]:.4f}  '
              f'Richtung {hits_pct(hit_te[hi])}   {verdict}', flush=True)

    print('\nEin Fehlerverhältnis unter 1 heißt: besser als anzunehmen, es ändert '
          'sich nichts. Die erste Säule liegt bei 52,3 % Richtung.', flush=True)

    # ------------------------------------------------------------ ONNX-Export
    model.eval()
    dummy = (torch.zeros(1, args.seq, len(feature_cols)),
             torch.zeros(1, dtype=torch.long),
             torch.zeros(1, dtype=torch.long))

    torch.onnx.export(
        model, dummy, str(out / f'{args.name}.onnx'),
        input_names=['features', 'asset_idx', 'horizon_idx'],
        output_names=['mean', 'logvar'],
        dynamic_axes={'features': {0: 'batch'}, 'asset_idx': {0: 'batch'},
                      'horizon_idx': {0: 'batch'},
                      'mean': {0: 'batch'}, 'logvar': {0: 'batch'}},
        opset_version=17)

    # ------------------------------------------------------------------
    # Die zweite Latte: die blosse Drift.
    #
    # "Schlaegt den Stillstand" reicht bei langen Horizonten nicht. Ueber ein
    # Jahr steigen Aktien im Mittel; ein Modell, das nur "aufwaerts" sagt,
    # schlaegt den Stillstand zwangslaeufig, ohne etwas gelernt zu haben.
    #
    # Gemessen am langen Modell dieser Sitzung: Fehlerverhaeltnis 0,9604 bei
    # 250 Tagen sah nach dem ersten Erfolg aus. Die blosse Trainingsdrift --
    # EINE Zahl -- kam auf 0,9139 und war damit naeher an der Wahrheit. Von den
    # Zielwerten im Sperrbereich waren 75,1 % positiv; die Trefferquote des
    # Modells von 64,3 % lag also unter der eines Wuerfels, der immer
    # "aufwaerts" sagt.
    drift_ratio = []
    drift_hit = []
    drift_val = []

    for hi, h in enumerate(horizons):
        y_tr = index.targets[index.samples[tr, 0], hi]
        y_tr = y_tr[~np.isnan(y_tr)]

        y_te = index.targets[index.samples[te, 0], hi]
        y_te = y_te[~np.isnan(y_te)]

        if len(y_tr) == 0 or len(y_te) == 0:
            drift_ratio.append(None); drift_hit.append(None); drift_val.append(None)
            continue

        d = float(y_tr.mean())
        naiv = float(np.abs(y_te).mean())

        drift_val.append(d)
        drift_ratio.append(float(np.abs(y_te - d).mean() / naiv) if naiv > 0 else None)
        drift_hit.append(float((np.sign(y_te) == np.sign(d)).mean()))

    print('\nDRIFTLATTE (eine einzige Zahl aus dem Training)', flush=True)

    for hi, h in enumerate(horizons):
        if drift_ratio[hi] is None:
            continue

        besser = ('das Modell' if ratio_te[hi] < drift_ratio[hi] else 'DIE DRIFT ALLEIN')

        print(f'    h={h:<3d} Drift {drift_val[hi]:+.5f}  '
              f'Fehlerverhaeltnis {drift_ratio[hi]:.4f}  '
              f'Richtung {drift_hit[hi]:.3%}   naeher an der Wahrheit: {besser}',
              flush=True)


    meta = {
        'version': f'deep-2-{args.name}',
        'seq_len': args.seq,
        'features': feature_cols,
        'horizons': horizons,
        'asset_ids': index.asset_ids,
        'feature_mean': f_mean.tolist(),
        'feature_std': f_std.tolist(),
        'target_std': y_std.tolist(),
        'train_until': str(stamps[tr].max()),
        'validate_until': str(stamps[va].max()),
        'test_until': str(stamps[te].max()),
        'validation_error_ratio': float(best),
        'test_error_ratio': float(np.nanmean(ratio_te)),
        'test_hit_rate': float(np.nanmean(hit_te)),
        'detrended': bool(args.detrend),
        'drift_removed': [float(x) for x in drift_lernen],
        'drift_train': drift_val,
        'drift_error_ratio_by_horizon': drift_ratio,
        'drift_hit_rate_by_horizon': drift_hit,
        'test_error_ratio_by_horizon': [float(x) for x in ratio_te],
        'test_hit_rate_by_horizon': [float(x) for x in hit_te],
    }

    (out / f'{args.name}.json').write_text(json.dumps(meta, indent=2), encoding='utf-8')
    print(f'\ngeschrieben: {out / (args.name + ".onnx")}', flush=True)


if __name__ == '__main__':
    main()
