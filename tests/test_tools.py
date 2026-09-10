import sys
import copy
import json
import pytest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
from evaluate import evaluate
from compare_estimators import estimates
from update_catalog import apply_mod_corrections


def test_unlabeled_data_cannot_pass_release(tmp_path):
    result = evaluate(
        {
            "screens": [
                dict(
                    file="sample.png",
                    session="a",
                    split="test",
                    sha256="a",
                    groundTruth=None,
                )
            ]
        },
        tmp_path,
    )
    assert (
        not result["releaseReady"]
        and result["labeledTestScreens"] == 0
        and result["accuracyClaim"] is None
    )


def test_training_session_leakage_blocks_release(tmp_path):
    rows = [
        dict(
            file=f"{i}.png",
            session="same",
            split=split,
            sha256=str(i),
            groundTruth=None,
        )
        for i, split in enumerate(["development", "test"])
    ]
    assert evaluate({"screens": rows}, tmp_path)["sessionLeakage"] == ["same"]


def test_estimator_handles_sparse_sample():
    assert estimates([1, 10, 100])["low_10_median"] == 10
    assert estimates([]) == {}


def test_catalog_correction_survives_refresh_and_rejects_unexpected_changes():
    root = Path(__file__).resolve().parents[1]
    path = root / "data/catalog-mod-corrections.json"
    item = copy.deepcopy(
        next(
            i
            for i in json.loads(
                (root / "data/catalog.json").read_text(encoding="utf8")
            )["items"]
            if i["id"] == "Chober_Chaber"
        )
    )
    correction = json.loads(path.read_text(encoding="utf8"))["corrections"][0]
    for lang in ("modsEn", "modsKo"):
        for old, new in correction[lang].items():
            item[lang][item[lang].index(new)] = old
    apply_mod_corrections([item], path)
    apply_mod_corrections([item], path)
    assert "+(2 — 3) to Level of all Minion Skills" in item["modsEn"]
    item["modsEn"].remove("+(2 — 3) to Level of all Minion Skills")
    with pytest.raises(ValueError, match="Review changed modifier"):
        apply_mod_corrections([item], path)
