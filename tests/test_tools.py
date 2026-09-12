import sys
import copy
import json
import hashlib
import pytest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
from evaluate import evaluate
from compare_estimators import estimates
from update_catalog import apply_mod_corrections
from capture_dataset import assign_splits, near_duplicate, view_features
from train_recognition import train
from validate_recognition import validate
import numpy as np


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


def test_duplicate_sessions_share_a_split():
    rows = [
        dict(session="a", members=[dict(session="a"), dict(session="b")]),
        dict(session="b", members=[dict(session="b")]),
        dict(session="c", members=[dict(session="c")]),
    ]
    assign_splits(rows)
    assert rows[0]["splitGroup"] == rows[1]["splitGroup"]
    assert rows[0]["split"] == rows[1]["split"]


def test_capture_dedup_ignores_world_but_retains_changed_items():
    image = np.zeros((240, 400, 3), np.uint8)
    rng = np.random.default_rng(1)
    image[30:150, 100:244] = rng.integers(0, 200, (120, 144, 3), np.uint8)
    p = dict(grid=dict(bounds=dict(x=100, y=30, width=144, height=120)))
    same = image.copy()
    same[:, 300:] = 255
    different = image.copy()
    different[60:120, 120:180] = 255
    assert near_duplicate(view_features(image, p), view_features(same, p))
    assert not near_duplicate(view_features(image, p), view_features(different, p))


def test_predictions_or_validation_items_cannot_train(tmp_path):
    manifest = tmp_path / "manifest.json"
    records = [
        dict(split="train", verifiedBy=None, groundTruth=None),
        dict(split="validation", verifiedBy="reviewer", groundTruth=dict(items=[])),
    ]
    manifest.write_text(json.dumps(dict(records=records)), encoding="utf-8")
    with pytest.raises(ValueError, match="No reviewed training items"):
        train(manifest, dict(version="a", items=[]), tmp_path / "model.json")


def test_reviewed_source_hash_is_checked_before_training(tmp_path):
    source = tmp_path / "frame.png"
    source.write_bytes(b"changed")
    manifest = tmp_path / "manifest.json"
    manifest.write_text(
        json.dumps(
            dict(
                records=[
                    dict(
                        split="train",
                        verifiedBy="reviewer",
                        groundTruth=dict(items=[]),
                        file=str(source),
                        sha256="wrong",
                    )
                ]
            )
        ),
        encoding="utf-8",
    )
    with pytest.raises(ValueError, match="capture changed"):
        train(manifest, dict(version="a", items=[]), tmp_path / "model.json")


def test_validation_rejects_trained_session_and_changed_source(tmp_path):
    source = tmp_path / "frame.png"
    source.write_bytes(b"changed")
    manifest = tmp_path / "manifest.json"
    manifest.write_text(
        json.dumps(
            dict(
                records=[
                    dict(
                        split="validation",
                        splitGroup="shared",
                        sha256="a",
                        file=str(source),
                        verifiedBy="reviewer",
                        groundTruth=dict(complete=True, grid=None, items=[]),
                    )
                ]
            )
        ),
        encoding="utf-8",
    )
    model = tmp_path / "model.json"
    model.write_text(
        json.dumps(dict(prototypes=[dict(sourceHash="a")])), encoding="utf-8"
    )
    result = validate(manifest, tmp_path, model)
    assert not result["passed"]
    assert {e["kind"] for e in result["errors"]} == {
        "training_session_leakage",
        "source_hash",
    }


def test_unreviewed_validation_cannot_pass(tmp_path):
    manifest = tmp_path / "manifest.json"
    manifest.write_text(
        json.dumps(dict(records=[dict(split="validation", groundTruth=None)])),
        encoding="utf-8",
    )
    result = validate(manifest, tmp_path)
    assert not result["passed"] and result["unreviewedFrames"] == 1


def test_dialog_training_crops_do_not_require_visible_price_regions(tmp_path):
    source = tmp_path / "dialog.png"
    source.write_bytes(b"fixture")
    manifest = tmp_path / "manifest.json"
    manifest.write_text(
        json.dumps(
            dict(
                records=[
                    dict(
                        split="train",
                        verifiedBy="reviewer",
                        file=str(source),
                        sha256=hashlib.sha256(source.read_bytes()).hexdigest(),
                        groundTruth=dict(
                            complete=False,
                            analysisSuppressed=True,
                            items=[dict(instanceId="0:0")],
                        ),
                    )
                ]
            )
        ),
        encoding="utf-8",
    )
    prediction = tmp_path / "dialog.json"
    prediction.write_text(json.dumps(dict(items=[], elapsedMs=1)), encoding="utf-8")
    result = validate(manifest, tmp_path, split="train")
    assert (
        result["passed"]
        and result["reviewedItems"] == 0
        and result["annotatedItems"] == 1
    )
    prediction.write_text(
        json.dumps(dict(items=[dict(instanceId="0:0")], elapsedMs=1)), encoding="utf-8"
    )
    assert not validate(manifest, tmp_path, split="train")["passed"]
