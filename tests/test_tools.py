import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
from evaluate import evaluate
from compare_estimators import estimates


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
