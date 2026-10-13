"""Download the official release in verified, resumable HTTP byte ranges."""
from __future__ import annotations

import concurrent.futures
import hashlib
import json
from pathlib import Path
import sys
import threading
import time
import urllib.request

from inspect_bundle import read_remote_range


URL = "https://github.com/Lypc233/LaoYing-Toolkit/releases/download/v1.8.4/LaoYing.Toolkit.v1.8.4.exe"
SIZE = 510584240
SHA256 = "b784c1c024d0eee3b570b0471f91b13b516858fcd14921292c5c0d0c1fffa18d"
CHUNK = 8 * 1024 * 1024
ROOT = Path(__file__).resolve().parent.parent / "inspection"
FINAL = ROOT / "LaoYing.Toolkit.v1.8.4.exe"
PART = ROOT / "original.download.part"
STATE = ROOT / "original.download.json"


def digest(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def main() -> None:
    sys.stdout.reconfigure(encoding="utf-8")
    ROOT.mkdir(parents=True, exist_ok=True)
    if FINAL.exists() and FINAL.stat().st_size == SIZE and digest(FINAL) == SHA256:
        print("Official EXE already present; SHA-256 verified.", flush=True)
        return
    completed: set[int] = set()
    if PART.exists() and STATE.exists() and PART.stat().st_size == SIZE:
        state = json.loads(STATE.read_text(encoding="utf-8"))
        if state.get("sha256") == SHA256 and state.get("chunk_size") == CHUNK:
            completed = set(state.get("completed", []))
    else:
        with PART.open("wb") as stream:
            stream.truncate(SIZE)
    request = urllib.request.Request(URL, headers={"Range": "bytes=0-0", "User-Agent": "Zhehao-Static-Inspection/1.0"})
    with urllib.request.urlopen(request, timeout=30) as response:
        if response.status != 206:
            raise RuntimeError("The release CDN did not honor byte ranges.")
        cdn_url = response.geturl()
        response.read(1)
    count = (SIZE + CHUNK - 1) // CHUNK
    lock = threading.Lock()

    def download(index: int) -> None:
        offset = index * CHUNK
        length = min(CHUNK, SIZE - offset)
        for attempt in range(4):
            try:
                data = read_remote_range(cdn_url, offset, length)
                with lock:
                    with PART.open("r+b") as stream:
                        stream.seek(offset)
                        stream.write(data)
                    completed.add(index)
                    temporary_state = STATE.with_suffix(".json.tmp")
                    temporary_state.write_text(json.dumps({"sha256": SHA256, "chunk_size": CHUNK,
                                                           "completed": sorted(completed)}), encoding="utf-8")
                    temporary_state.replace(STATE)
                    if len(completed) % 5 == 0 or len(completed) == count:
                        print(f"Downloaded {len(completed)}/{count} ranges ({len(completed) / count:.0%}).", flush=True)
                return
            except Exception:
                if attempt == 3:
                    raise
                time.sleep(attempt + 1)

    print(f"Downloading official release: {SIZE / 1024 ** 2:.1f} MiB, {len(completed)}/{count} ranges available.", flush=True)
    with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
        list(pool.map(download, (index for index in range(count) if index not in completed)))
    actual = digest(PART)
    if actual != SHA256:
        raise RuntimeError(f"SHA-256 mismatch: {actual}")
    PART.replace(FINAL)
    print(f"Official release SHA-256 verified: {actual}", flush=True)


if __name__ == "__main__":
    main()
