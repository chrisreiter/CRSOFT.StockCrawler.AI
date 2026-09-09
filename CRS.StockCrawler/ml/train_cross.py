"""
Querschnittsmodell — der Erhaltungsgedanke, direkt umgesetzt.

Aufruf:
    python ml/train_cross.py --csv D:/_data/stockcrawler/features_1d.csv --out ml/models

Der Unterschied zum Sequenzmodell ist grundsätzlich, nicht graduell.

  Sequenzmodell:    die letzten 48 Tage EINES Wertes  ->  dieser Wert morgen
  Querschnitt:      der letzte Tag ALLER Werte        ->  ein Wert morgen

Der zweite Aufbau ist die Erhaltungsidee in ihrer prüfbaren Form: Wenn sich
Kapital im Wesentlichen zwischen den beobachteten Werten verschiebt, muss in der
heutigen Bewegung aller anderen stecken, was morgen mit einem einzelnen
geschieht.

Diese Frage ist bereits linear geprüft worden — mit Ridge-Regression über
97 Werte, gemessen auf Tagen außerhalb der Anpassung:

    gleichzeitig      Bestimmtheitsmaß 0,355
    einen Tag voraus  Bestimmtheitsmaß 0,0039

Die Kopplung ist also stark, aber gleichzeitig. Was dieses Programm hinzufügt,
ist die Möglichkeit einer NICHTLINEAREN Beziehung: Vielleicht liegt die
Information nicht in einer gewichteten Summe, sondern in einem Muster, das eine
Regression nicht sieht. Das ist eine ernsthafte Möglichkeit und der Grund,
warum es sich lohnt, sie zu rechnen statt sie abzutun.

Gemeinsamer Handelskalender: Ausgewertet werden nur Tage, an denen ein
ausreichender Teil ALLER Werte gehandelt hat. Am Wochenende handelt nur Krypto
— solche Tage in den Querschnitt zu nehmen hieße, für hunderte Werte eine
Nullbewegung zu behaupten, die keine ist, und genau die Erhaltungsrechnung
damit zu verfälschen.
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


class CrossForecaster(nn.Module):
    """
    Der gesamte Marktquerschnitt eines Tages als Eingabe, ein einzelner Wert als
    Ziel.

    Die Einbettung des Zielwertes ist wesentlich: Dieselbe Marktlage bedeutet
    für einen Halbleiterhersteller etwas anderes als für einen Versorger. Ohne
    sie müsste das Netz für alle Werte dieselbe Antwort geben.
    """

    def __init__(self, n_assets: int, n_horizons: int, hidden: int = 256,
                 emb_asset: int = 32, emb_horizon: int = 8, dropout: float = 0.3):
        super().__init__()

        self.trunk = nn.Sequential(
            nn.Linear(n_assets, hidden),
            nn.LayerNorm(hidden),
            nn.GELU(),
            nn.Dropout(dropout),
            nn.Linear(hidden, hidden // 2),
            nn.LayerNorm(hidden // 2),
            nn.GELU(),
            nn.Dropout(dropout),
        )

        self.asset_emb = nn.Embedding(n_assets, emb_asset)
        self.horizon_emb = nn.Embedding(n_horizons, emb_horizon)

        self.head = nn.Sequential(
            nn.Linear(hidden // 2 + emb_asset + emb_horizon, 128),
            nn.GELU(),
            nn.Dropout(dropout),
            nn.Linear(128, 2),
        )

    def forward(self, market, asset_idx, horizon_idx):
        h = self.trunk(market)
        z = torch.cat([h, self.asset_emb(asset_idx), self.horizon_emb(horizon_idx)], dim=1)

        out = self.head(z)
        return out[:, 0], out[:, 1].clamp(-8.0, 4.0)


def gaussian_nll(mean, logvar, target):
    """Belohnt eine ehrlich eingestandene Unsicherheit statt lauten Ratens."""
    return 0.5 * (logvar + (target - mean) ** 2 / logvar.exp()).mean()


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument('--csv', required=True)
    ap.add_argument('--out', default='ml/models')
    ap.add_argument('--name', default='cross_short')
    ap.add_argument('--horizons', default='1,2,3,5')
    ap.add_argument('--epochs', type=int, default=30)
    ap.add_argument('--batch', type=int, default=1024)
    ap.add_argument('--hidden', type=int, default=256)
    ap.add_argument('--dropout', type=float, default=0.3)
    ap.add_argument('--lr', type=float, default=1e-3)
    ap.add_argument('--patience', type=int, default=4)
    ap.add_argument('--coverage', type=float, default=0.9,
                    help='Anteil der Werte, die an einem Tag gehandelt haben muessen')
    ap.add_argument('--threads', type=int, default=0)
    ap.add_argument('--klasse', default='',
                    help="'stocks' (Aktien+ETF) oder 'crypto' -- getrennte Handelskalender")
    ap.add_argument('--min-days', type=int, default=0,
                    help='Werte verwerfen, die weniger Tage abdecken')
    ap.add_argument('--contemporaneous', action='store_true',
                    help='Kontrolle: heutige Bewegung aus den heutigen der ANDEREN')
    args = ap.parse_args()

    if args.threads > 0:
        torch.set_num_threads(args.threads)

    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)

    horizons = [int(x) for x in args.horizons.split(',') if x.strip()]

    print(f'lade {args.csv} …', flush=True)
    cols = ['ts_utc', 'asset_id', 'symbol', 'asset_class', 'r1'] + [f'y_h{h}' for h in horizons]
    df = pd.read_csv(args.csv, usecols=cols, parse_dates=['ts_utc'])

    # Getrennte Handelskalender getrennt rechnen.
    #
    # Aktien und ETFs teilen einen Kalender, Krypto einen anderen. Zusammen
    # gibt es kein Raster, auf dem beide dicht besetzt sind: Verlangt man
    # 90 % Beteiligung ueber alle Werte, bleiben nur die letzten vier Jahre --
    # nicht wegen der Wochenenden, sondern weil das Universum gewachsen ist.
    # Getrennt reicht der Aktienteil bis 2001 zurueck.
    if args.klasse == 'crypto':
        df = df[df.asset_class == 'Crypto']
    elif args.klasse == 'stocks':
        df = df[df.asset_class.isin(['Stock', 'Etf'])]

    if args.min_days > 0:
        n_per = df.groupby('asset_id').ts_utc.nunique()
        df = df[df.asset_id.isin(n_per[n_per >= args.min_days].index)]

    if args.klasse or args.min_days:
        print(f'Betrachtungsraum {args.klasse or "alle"}: '
              f'{df.asset_id.nunique()} Werte', flush=True)

    # Querschnittsmatrix: Tage x Werte
    market = df.pivot_table(index='ts_utc', columns='asset_id', values='r1', aggfunc='first')

    # Nur Tage mit ausreichender Beteiligung -- siehe Kopfkommentar.
    present = market.notna().mean(axis=1)
    days = market.index[present >= args.coverage]

    market = market.loc[days]
    assets = list(market.columns)

    print(f'{len(days)} gemeinsame Handelstage mit mindestens '
          f'{args.coverage:.0%} Beteiligung, {len(assets)} Werte', flush=True)
    print(f'  von {days.min().date()} bis {days.max().date()}', flush=True)

    # Fehlende Einzelwerte auf null -- an diesen Tagen handelte der Wert nicht.
    X = np.nan_to_num(market.to_numpy(dtype=np.float32), nan=0.0)

    day_index = {d: i for i, d in enumerate(days)}
    asset_index = {a: i for i, a in enumerate(assets)}

    # Beispiele: Tag, Wert, Horizont -- nur wo eine Zielgroesse vorliegt.
    sub = df[df.ts_utc.isin(day_index)]

    if args.contemporaneous:
        # Der Zielwert wird spaeter aus seiner eigenen Eingabe entfernt --
        # sonst waere die Aufgabe: lies Feld j ab. Das koennte auch eine
        # Nachschlagetabelle.
        horizons = [0]
        sub = sub[sub.r1.notna()].copy()
        sub['y_h0'] = sub.r1

    rows = []
    targets = []

    for hi, h in enumerate(horizons):
        col = f'y_h{h}'
        ok = sub[sub[col].notna()]

        d_idx = ok.ts_utc.map(day_index).to_numpy(dtype=np.int64)
        a_idx = ok.asset_id.map(asset_index).to_numpy(dtype=np.float64)

        valid = ~np.isnan(a_idx)

        rows.append(np.stack([
            d_idx[valid],
            a_idx[valid].astype(np.int64),
            np.full(int(valid.sum()), hi, dtype=np.int64)
        ], axis=1))

        targets.append(ok[col].to_numpy(dtype=np.float32)[valid])

    samples = np.concatenate(rows, axis=0)
    y_all = np.concatenate(targets, axis=0)

    print(f'{len(samples):,} Beispiele', flush=True)

    # Zeitlicher Split, nie zufaellig.
    q_train, q_val = np.quantile(samples[:, 0], [0.65, 0.82])

    tr = np.where(samples[:, 0] <= q_train)[0]
    va = np.where((samples[:, 0] > q_train) & (samples[:, 0] <= q_val))[0]
    te = np.where(samples[:, 0] > q_val)[0]

    print(f'Training {len(tr):,} (bis {days[int(q_train)].date()})  '
          f'Abstimmung {len(va):,}  Sperre {len(te):,}', flush=True)

    # Normierung je Horizont -- die Streuungen unterscheiden sich um das Doppelte.
    y_std = np.empty(len(horizons), dtype=np.float32)
    for hi in range(len(horizons)):
        sel = tr[samples[tr, 2] == hi]
        y_std[hi] = float(np.nanstd(y_all[sel]) + 1e-12) if len(sel) else 1.0

    print('Streuung je Horizont: ' +
          ', '.join(f'{h}={s:.4f}' for h, s in zip(horizons, y_std)), flush=True)

    x_mean = X[samples[tr, 0]].mean(axis=0)
    x_std = X[samples[tr, 0]].std(axis=0) + 1e-8

    model = CrossForecaster(len(assets), len(horizons), args.hidden, dropout=args.dropout)

    opt = torch.optim.AdamW(model.parameters(), lr=args.lr, weight_decay=1e-3)

    mask_self = args.contemporaneous

    def batch(idx):
        s = samples[idx]
        m = (X[s[:, 0]] - x_mean) / x_std

        if mask_self:
            m = m.copy()
            m[np.arange(len(s)), s[:, 1]] = 0.0

        return m, s[:, 1], s[:, 2], y_all[idx]

    def evaluate(rows_):
        model.eval()

        em = np.zeros(len(horizons))
        en = np.zeros(len(horizons))
        hit = np.zeros(len(horizons))
        cnt = np.zeros(len(horizons))

        with torch.no_grad():
            for i in range(0, len(rows_), 4096):
                m, a, h, y = batch(rows_[i:i + 4096])

                mean, _ = model(torch.from_numpy(m), torch.from_numpy(a), torch.from_numpy(h))
                pred = mean.numpy() * y_std[h]

                for hi in range(len(horizons)):
                    k = h == hi
                    if not k.any():
                        continue

                    em[hi] += np.abs(pred[k] - y[k]).sum()
                    en[hi] += np.abs(y[k]).sum()
                    hit[hi] += int((np.sign(pred[k]) == np.sign(y[k])).sum())
                    cnt[hi] += int(k.sum())

        model.train()

        return (np.where(en > 0, em / np.maximum(1e-12, en), np.nan),
                np.where(cnt > 0, hit / np.maximum(1, cnt), np.nan))

    rng = np.random.default_rng(20260822)
    best = math.inf
    stale = 0

    for ep in range(args.epochs):
        perm = rng.permutation(tr)
        run = 0.0
        steps = 0

        for i in range(0, len(perm) - args.batch, args.batch):
            m, a, h, y = batch(perm[i:i + args.batch])

            mean, logvar = model(torch.from_numpy(m), torch.from_numpy(a), torch.from_numpy(h))
            loss = gaussian_nll(mean, logvar, torch.from_numpy(y / y_std[h]))

            opt.zero_grad()
            loss.backward()
            nn.utils.clip_grad_norm_(model.parameters(), 1.0)
            opt.step()

            run += float(loss)
            steps += 1

        ratio, hitr = evaluate(va)
        mr = float(np.nanmean(ratio))

        print(f'Epoche {ep + 1}: Verlust {run / max(1, steps):.4f}  '
              f'Abstimmung {mr:.4f}  ' +
              ' '.join(f'h{h}={r:.4f}/{v:.1%}' for h, r, v in zip(horizons, ratio, hitr)),
              flush=True)

        if mr < best - 1e-5:
            best = mr
            stale = 0
            torch.save(model.state_dict(), out / f'{args.name}.pt')
        else:
            stale += 1
            if stale >= args.patience:
                print(f'kein Fortschritt seit {stale} Epochen — Abbruch', flush=True)
                break

    model.load_state_dict(torch.load(out / f'{args.name}.pt'))
    ratio_te, hit_te = evaluate(te)

    print('\nSPERRBEREICH', flush=True)
    for hi, h in enumerate(horizons):
        verdict = ('trägt' if ratio_te[hi] < 1 and hit_te[hi] > 0.523
                   else 'schlägt Stillstand' if ratio_te[hi] < 1 else 'trägt nicht')
        print(f'    h={h:<3d} Fehlerverhältnis {ratio_te[hi]:.4f}  '
              f'Richtung {hit_te[hi]:.3%}   {verdict}', flush=True)

    meta = {
        'version': f'cross-1-{args.name}',
        'kind': 'cross-section',
        'horizons': horizons,
        'asset_ids': [int(a) for a in assets],
        'market_mean': x_mean.tolist(),
        'market_std': x_std.tolist(),
        'target_std': y_std.tolist(),
        'days': len(days),
        'coverage': args.coverage,
        'from': str(days.min().date()),
        'until': str(days.max().date()),
        'validation_error_ratio': float(best),
        'test_error_ratio': float(np.nanmean(ratio_te)),
        'test_hit_rate': float(np.nanmean(hit_te)),
        'test_error_ratio_by_horizon': [float(x) for x in ratio_te],
        'test_hit_rate_by_horizon': [float(x) for x in hit_te],
    }

    (out / f'{args.name}_meta.json').write_text(json.dumps(meta, indent=2), encoding='utf-8')
    print(f'\ngeschrieben: {out / (args.name + "_meta.json")}', flush=True)


if __name__ == '__main__':
    main()
