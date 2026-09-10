"""Fetch public PoE2DB references. No game access. Images retain provenance.
Usage: python tools/update_catalog.py --out data [--html-cache work]
"""

import argparse
import subprocess
import sys
import hashlib
import io
import json
import re
import time
from datetime import datetime, timezone
from pathlib import Path
from urllib.parse import urlparse

import requests
from bs4 import BeautifulSoup
from PIL import Image

PAGES = [
    ("unique", "Unique_item"),
    ("currency", "Currency"),
    ("currency", "Omen"),
    ("currency", "Essence"),
    ("currency", "Rune"),
    ("currency", "Expedition"),
    ("currency", "Delirium"),
    ("currency", "Soul_Core"),
    ("currency", "Ritual"),
    ("currency", "Idol"),
    ("currency", "Map_Fragments"),
]


def parse_page(html, kind, language):
    soup = BeautifulSoup(html, "html.parser")
    rows = {}
    for card in soup.select("div.d-flex.border-top.rounded"):
        art = card.select_one("div.flex-shrink-0 img")
        body = card.select_one("div.flex-grow-1")
        if not art or not body:
            continue
        anchor = (
            body.select_one("a.UniqueItem")
            if kind == "unique"
            else body.find("a", recursive=False)
        )
        if not anchor:
            continue
        slug = anchor.get("href", "").split("/")[-1]
        name = anchor.select_one(".uniqueName") if kind == "unique" else anchor
        if not slug or not name:
            continue
        if any(
            marker in (slug + " " + name.get_text()).lower()
            for marker in ("dnt-", "unused", "[test]")
        ):
            continue
        image = art.get("src", "")
        if urlparse(image).hostname != "cdn.poe2db.tw":
            continue
        stack = re.search(
            r"Stack Size:\s*1\s*/\s*(\d+)", body.get_text(" ", strip=True)
        )
        max_stack = (
            int(stack.group(1))
            if stack
            else (1 if "AtlasCurrency" in anchor.get("class", []) else None)
        )
        rows[slug] = dict(
            maxStackSize=max_stack,
            name=name.get_text(" ", strip=True),
            image=image,
            mods=[m.get_text(" ", strip=True) for m in body.select(".explicitMod")],
        )
    return rows


def apply_mod_corrections(items, path):
    corrections = json.loads(path.read_text(encoding="utf-8"))["corrections"]
    by_id = {item["id"]: item for item in items}
    for correction in corrections:
        item = by_id.get(correction["id"])
        if item is None:
            raise ValueError(f"Corrected item missing: {correction['id']}")
        for language in ("modsEn", "modsKo"):
            for old, new in correction[language].items():
                matches = [
                    i for i, mod in enumerate(item[language]) if mod in (old, new)
                ]
                if len(matches) != 1:
                    raise ValueError(
                        f"Review changed modifier: {correction['id']} {language}"
                    )
                item[language][matches[0]] = new


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--out", type=Path, default=Path("data"))
    p.add_argument("--html-cache", type=Path)
    p.add_argument(
        "--skip-official-art", action="store_true", help="Only update PoE2DB references"
    )
    args = p.parse_args()
    images = args.out / "images"
    images.mkdir(parents=True, exist_ok=True)
    session = requests.Session()
    session.headers["User-Agent"] = (
        "RitualChecker/0.1 (local reference catalog updater)"
    )
    static = session.get(
        "https://www.pathofexile.com/api/trade2/data/static", timeout=30
    )
    static.raise_for_status()
    current_currency_names = {
        i["text"]
        for g in static.json()["result"]
        for i in g["entries"]
        if i.get("image")
    }
    items, failures = [], []
    for kind, page in PAGES:
        localized = {}
        for lang, suffix in [("us", "en"), ("kr", "ko")]:
            cache_key = kind if page in ("Unique_item", "Currency") else page
            cached = (
                args.html_cache / f"{cache_key}-{suffix}.html"
                if args.html_cache
                else None
            )
            if cached and cached.exists():
                html = cached.read_text(encoding="utf-8")
            else:
                r = session.get(f"https://poe2db.tw/{lang}/{page}", timeout=45)
                if not r.ok:
                    failures.append(
                        dict(page=page, language=lang, error=f"HTTP {r.status_code}")
                    )
                    localized[lang] = {}
                    continue
                html = r.text
                if cached:
                    cached.write_text(html, encoding="utf-8")
                time.sleep(0.4)
            localized[lang] = parse_page(html, kind, lang)
        if not localized["us"]:
            failures.append(
                dict(page=page, error="No matching cards: coverage not established")
            )
            continue
        for slug, en in localized["us"].items():
            if kind == "currency" and en["name"] not in current_currency_names:
                continue
            ko = localized["kr"].get(slug, {})
            asset = hashlib.sha256(en["image"].encode()).hexdigest()[:24] + ".png"
            path = images / asset
            try:
                if not path.exists():
                    r = session.get(
                        en["image"],
                        headers={"Referer": f"https://poe2db.tw/us/{slug}"},
                        timeout=30,
                    )
                    r.raise_for_status()
                    im = Image.open(io.BytesIO(r.content)).convert("RGBA")
                    im.save(path)
                    time.sleep(0.2)
                with Image.open(path) as im:
                    w, h = im.size
                # Current PoE2DB art: 104 pixels/cell plus 4 pixels of padding.
                cols, rows = max(1, round((w - 4) / 104)), max(1, round((h - 4) / 104))
                items.append(
                    dict(
                        id=slug,
                        nameEn=en["name"],
                        nameKo=ko.get("name", ""),
                        kind=kind,
                        image="images/" + asset,
                        width=cols,
                        height=rows,
                        source=f"https://poe2db.tw/us/{slug}",
                        imageSource=en["image"],
                        modsEn=en["mods"],
                        modsKo=ko.get("mods", []),
                        maxStackSize=en.get("maxStackSize"),
                    )
                )
            except (requests.RequestException, OSError) as exc:
                failures.append(dict(id=slug, error=str(exc)))
        print(
            f"{kind}: {len(localized['us'])} entries, {len(items)} total downloaded",
            flush=True,
        )
    stamp = datetime.now(timezone.utc).isoformat()
    items = list({item["id"]: item for item in items}.values())
    apply_mod_corrections(
        items, Path(__file__).resolve().parents[1] / "data/catalog-mod-corrections.json"
    )
    body = dict(
        version=hashlib.sha256(json.dumps(items, sort_keys=True).encode()).hexdigest()[
            :16
        ],
        retrievedAt=stamp,
        items=items,
    )
    (args.out / "catalog-download-report.json").write_text(
        json.dumps(
            dict(retrievedAt=stamp, count=len(items), failures=failures),
            ensure_ascii=False,
            indent=2,
        ),
        encoding="utf-8",
    )
    if failures:
        raise SystemExit(
            f"{len(failures)} downloads failed; existing catalog retained; see catalog-download-report.json"
        )
    metadata = ["catalog.json", "official-references.json", "embedding.json"]
    backups = {
        name: (args.out / name).read_bytes() if (args.out / name).exists() else None
        for name in metadata
    }
    try:
        temp = args.out / "catalog.json.tmp"
        temp.write_text(
            json.dumps(body, ensure_ascii=False, indent=2), encoding="utf-8"
        )
        temp.replace(args.out / "catalog.json")
        if not args.skip_official_art:
            subprocess.run(
                [
                    sys.executable,
                    str(Path(__file__).with_name("update_official_art.py")),
                    "--data",
                    str(args.out),
                ],
                check=True,
            )
        subprocess.run(
            [
                sys.executable,
                str(Path(__file__).with_name("train_embedding.py")),
                "--data",
                str(args.out),
            ],
            check=True,
        )
    except BaseException:
        for name, previous in backups.items():
            path = args.out / name
            if previous is None:
                path.unlink(missing_ok=True)
            else:
                temp = path.with_suffix(".rollback.tmp")
                temp.write_bytes(previous)
                temp.replace(path)
        raise


if __name__ == "__main__":
    main()
