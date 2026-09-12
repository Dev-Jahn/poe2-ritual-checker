"""Check Git's publication set without printing potentially sensitive contents."""

from pathlib import Path
import re
import subprocess
import sys

root = Path(__file__).resolve().parents[1]
files = (
    subprocess.check_output(["git", "ls-files", "-z"], cwd=root).decode().split("\0")
)
patterns = {
    "private Windows user path": re.compile(
        r"[A-Za-z]:[\\/]+Users[\\/]+[^\\/\s]+", re.I
    ),
    "private Unix user path": re.compile(r"/(?:home|Users)/[A-Za-z0-9_.-]+/"),
    "GitHub credential": re.compile(
        r"\b(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{30,})\b"
    ),
    "private key": re.compile(r"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----"),
    "credential assignment": re.compile(
        r"""(?i)(?:api_key|password|access_token|client_secret)\s*[:=]\s*["'][^"'\s]{8,}["']"""
    ),
}
private_parts = {
    "work",
    "dist",
    "artifacts",
    "poe2_ritual_data",
    "captures",
    "recognition-reports",
    "capture-dataset",
    "price-cache",
    "market-observations",
    "bin",
    "obj",
}
errors = []
count = 0
for name in filter(None, files):
    p = Path(name)
    if (
        private_parts.intersection(p.parts)
        or p.suffix in {".pfx", ".p12", ".sqlite", ".db"}
        or p.name == "settings.json"
    ):
        errors.append(f"{name}: private/generated file")
    raw = (root / p).read_bytes()
    if b"\0" in raw:
        continue
    text = raw.decode("utf-8-sig", errors="replace")
    count += 1
    for kind, pattern in patterns.items():
        for match in pattern.finditer(text):
            line = text[: match.start()].count("\n") + 1
            errors.append(f"{name}:{line}: {kind}")
if errors:
    print("\n".join(errors))
else:
    print(
        f"Publication scan: {len(list(filter(None, files)))} files, {count} text files, no matched findings"
    )
sys.exit(bool(errors))
