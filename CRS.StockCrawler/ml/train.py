"""
Stufe 3 — Querschnittsmodell auf der Merkmalsmatrix.

Trainiert je Horizont ein LightGBM-Modell und exportiert es nach ONNX, damit
die .NET-Seite es ohne Python ausführen kann.

Wesentlich ist der **zeitliche** Split. Ein zufälliger Split wäre hier fatal:
die Zeilen desselben Zeitpunkts über alle Werte hinweg sind hochkorreliert, und
ein Zeitpunkt im Training würde denselben Zeitpunkt im Test verraten. Das
Ergebnis sähe hervorragend aus und wäre wertlos. Deshalb: die ältesten Daten
zum Trainieren, die jüngsten zum Prüfen, dazwischen die Validierung.

Aufruf:
    python ml/train.py --csv src/Ingest.Api/export/features_1d.csv --out ml/models
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path

import numpy as np
import pandas as pd

META_COLS = ("ts_utc", "asset_id", "symbol", "asset_class")


def load(csv_path: Path):
    df = pd.read_csv(csv_path, parse_dates=["ts_utc"])
    df = df.sort_values("ts_utc").reset_index(drop=True)

    features = [c for c in df.columns if c not in META_COLS and not c.startswith("y_h")]
    targets = [c for c in df.columns if c.startswith("y_h")]

    return df, features, targets


def time_split(df: pd.DataFrame, train_frac=0.6, valid_frac=0.2):
    """Teilt nach Zeit, nicht zufällig — siehe Modulkommentar."""
    stamps = np.sort(df["ts_utc"].unique())
    n = len(stamps)

    train_end = stamps[int(n * train_frac)]
    valid_end = stamps[int(n * (train_frac + valid_frac))]

    tr = df["ts_utc"] <= train_end
    va = (df["ts_utc"] > train_end) & (df["ts_utc"] <= valid_end)
    te = df["ts_utc"] > valid_end

    return tr, va, te, train_end, valid_end


def direction_hit(pred: np.ndarray, actual: np.ndarray) -> float:
    """
    Anteil richtig getroffener Richtungen.

    Eine Prognose ohne erkennbare Richtung zählt als Fehlschlag — sonst könnte
    ein Modell die Quote hochhalten, indem es schlicht nichts sagt. Dieselbe
    Regel wie in der .NET-Lernschleife.
    """
    moved = np.abs(actual) > 1e-6
    if moved.sum() == 0:
        return 0.0

    p, a = pred[moved], actual[moved]
    no_call = np.abs(p) < 1e-4

    return float((~no_call & (np.sign(p) == np.sign(a))).mean())


def train_one(df, features, target, tr, va, te, params, rounds):
    import lightgbm as lgb

    mask = df[target].notna()

    Xtr, ytr = df.loc[tr & mask, features], df.loc[tr & mask, target]
    Xva, yva = df.loc[va & mask, features], df.loc[va & mask, target]
    Xte, yte = df.loc[te & mask, features], df.loc[te & mask, target]

    if len(Xtr) < 500 or len(Xte) < 200:
        return None

    model = lgb.LGBMRegressor(**params, n_estimators=rounds)
    model.fit(
        Xtr, ytr,
        eval_set=[(Xva, yva)],
        eval_metric="l1",
        callbacks=[lgb.early_stopping(50, verbose=False), lgb.log_evaluation(0)],
    )

    pte = model.predict(Xte)

    # Messlatte: „keine Änderung". Schlägt das Modell die nicht, ist es wertlos.
    naive_mae = float(np.mean(np.abs(yte)))
    model_mae = float(np.mean(np.abs(pte - yte)))

    return {
        "model": model,
        "target": target,
        "n_train": int(len(Xtr)),
        "n_test": int(len(Xte)),
        "best_iteration": int(model.best_iteration_ or rounds),
        "mae": model_mae,
        "naive_mae": naive_mae,
        "mae_vs_naive_pct": float((model_mae / naive_mae - 1.0) * 100),
        "hit_rate": direction_hit(pte, yte.to_numpy()),
        "corr": float(np.corrcoef(pte, yte)[0, 1]) if len(pte) > 2 else 0.0,
        "importance": dict(
            sorted(
                zip(features, model.feature_importances_.tolist()),
                key=lambda kv: -kv[1],
            )
        ),
    }


def to_onnx(model, features, path: Path):
    from onnxmltools import convert_lightgbm
    from onnxmltools.convert.common.data_types import FloatTensorType

    onnx_model = convert_lightgbm(
        model.booster_,
        initial_types=[("features", FloatTensorType([None, len(features)]))],
        target_opset=13,
        zipmap=False,
    )
    path.write_bytes(onnx_model.SerializeToString())


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--csv", required=True)
    ap.add_argument("--out", default="ml/models")
    ap.add_argument("--rounds", type=int, default=600)
    ap.add_argument("--leaves", type=int, default=63)
    ap.add_argument("--lr", type=float, default=0.05)
    args = ap.parse_args()

    csv_path = Path(args.csv)
    out_dir = Path(args.out)
    out_dir.mkdir(parents=True, exist_ok=True)

    df, features, targets = load(csv_path)
    tr, va, te, train_end, valid_end = time_split(df)

    print(f"Zeilen {len(df):,} | Merkmale {len(features)} | Ziele {targets}")
    print(f"Training bis {train_end}  Validierung bis {valid_end}")
    print(f"Aufteilung: {tr.sum():,} / {va.sum():,} / {te.sum():,}\n")

    params = dict(
        objective="regression_l1",     # robuster gegen die dicken Ränder von Renditen
        learning_rate=args.lr,
        num_leaves=args.leaves,
        min_child_samples=100,
        subsample=0.8,
        subsample_freq=1,
        colsample_bytree=0.8,
        reg_lambda=1.0,
        verbose=-1,
        n_jobs=-1,
    )

    summary = {
        "feature_version": None,
        "features": features,
        "csv": str(csv_path),
        "train_end": str(train_end),
        "valid_end": str(valid_end),
        "models": {},
    }

    print(f"{'Ziel':<8}{'n Test':>9}{'MAE':>10}{'vs naiv':>10}{'Richtung':>11}{'Korr':>8}")
    print("-" * 56)

    for target in targets:
        res = train_one(df, features, target, tr, va, te, params, args.rounds)
        if res is None:
            print(f"{target:<8}  zu wenige Daten")
            continue

        onnx_path = out_dir / f"model_{target}.onnx"
        to_onnx(res["model"], features, onnx_path)

        print(
            f"{target:<8}{res['n_test']:>9,}{res['mae']:>10.5f}"
            f"{res['mae_vs_naive_pct']:>+9.2f}%{res['hit_rate'] * 100:>10.2f}%"
            f"{res['corr']:>8.3f}"
        )

        summary["models"][target] = {
            k: v for k, v in res.items() if k not in ("model", "importance")
        } | {
            "onnx": onnx_path.name,
            "top_features": dict(list(res["importance"].items())[:12]),
        }

    (out_dir / "summary.json").write_text(
        json.dumps(summary, indent=2, ensure_ascii=False), encoding="utf-8"
    )

    print(f"\nModelle und summary.json unter {out_dir}")

    # Wichtigste Merkmale des kürzesten Horizonts — zeigt, worauf das Modell schaut.
    first = next(iter(summary["models"].values()), None)
    if first:
        print("\nWichtigste Merkmale (" + first["target"] + "):")
        for name, imp in first["top_features"].items():
            print(f"  {name:<16}{imp:>8}")


if __name__ == "__main__":
    main()
