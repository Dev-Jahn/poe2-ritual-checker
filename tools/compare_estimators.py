"""Compare indicative asking-price estimators on saved trade observations."""

import argparse
import json
from pathlib import Path
import numpy as np


def estimates(prices):
    a = np.sort(prices)
    if not len(a):
        return {}
    return {
        **{f"low_{n}_median": float(np.median(a[:n])) for n in (5, 10, 20)},
        "lower_quartile": float(np.quantile(a, 0.25)),
    }


def analyze(records):
    rows = []
    for record in records:
        rate = (record.get("rate") or {}).get("exaltedPerDivine")
        sellers = {}
        for listing in record["listings"]:
            amount = (
                listing["price"]
                if listing["currency"] == "exalted"
                else listing["price"] * rate
                if listing["currency"] == "divine" and rate
                else None
            )
            if amount is None or amount <= 0:
                continue
            seller = listing["seller"].lower()
            sellers[seller] = min(amount, sellers.get(seller, float("inf")))
        prices = list(sellers.values())
        base = estimates(prices)
        contaminated = estimates(prices + [min(prices) * 0.01]) if prices else {}
        rows.append(
            dict(
                id=record.get("id"),
                uniqueSellers=len(prices),
                estimates=base,
                lowOutlierSensitivity={
                    k: abs(contaminated[k] - v) / v for k, v in base.items()
                },
                insufficientFor20=len(prices) < 20,
            )
        )
    groups = {}
    for row in rows:
        groups.setdefault(row["id"], []).append(row)
    repeated = {key: values for key, values in groups.items() if len(values) >= 3}
    stability = {
        key: {
            method: float(
                np.std([v["estimates"][method] for v in values])
                / max(0.00001, np.mean([v["estimates"][method] for v in values]))
            )
            for method in values[0]["estimates"]
        }
        for key, values in repeated.items()
    }
    return dict(
        records=rows,
        timeStability=stability,
        provisionalDefault="low_10_median",
        finalDecision="pending_repeated_market_observations",
        note="Snapshot estimator experiments are not evidence of executable prices or completed market validation.",
    )


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--observations", type=Path, required=True)
    p.add_argument("--out", type=Path, required=True)
    a = p.parse_args()
    records = [
        json.loads(f.read_text(encoding="utf-8")) for f in a.observations.glob("*.json")
    ]
    result = analyze(records)
    a.out.parent.mkdir(parents=True, exist_ok=True)
    a.out.write_text(json.dumps(result, indent=2), encoding="utf-8")
    print(
        f"{len(records)} observations compared; final decision pending time-series data"
    )


if __name__ == "__main__":
    main()
