"""Verify that repacking retains every bundled dependency and resource payload."""
from __future__ import annotations

import argparse
import hashlib
import io
import json
from pathlib import Path
import struct
import sys
import zlib

import pefile
from PIL import Image
from inspect_bundle import SIGNATURE, parse_manifest

ROOT = Path(__file__).resolve().parent.parent


def manifest(path: Path) -> dict:
    with path.open("rb") as stream:
        host = stream.read(10015744)
        marker = host.find(SIGNATURE)
        assert marker >= 8, "Bundle marker missing."
        offset = struct.unpack_from("<Q", host, marker - 8)[0]
        stream.seek(offset)
        return parse_manifest(stream.read())


def payload_digest(stream, entry: dict) -> str:
    stream.seek(entry["offset"])
    remaining = entry["compressed_size"] or entry["size"]
    digest = hashlib.sha256()
    while remaining:
        chunk = stream.read(min(remaining, 1024 * 1024))
        assert chunk, "Truncated bundle payload."
        digest.update(chunk)
        remaining -= len(chunk)
    return digest.hexdigest()


def main(output_dir: Path) -> None:
    sys.stdout.reconfigure(encoding="utf-8")
    original = ROOT / "inspection" / "LaoYing.Toolkit.v1.8.4.exe"
    branded = output_dir / "喆好工具箱.exe"
    before = manifest(original)
    after = manifest(branded)
    assert before["count"] == after["count"] == 467
    assert before["metadata"] == after["metadata"]
    assert before["version"] == after["version"]
    assert after["bundle_id"].startswith("ZH") and after["bundle_id"] != before["bundle_id"]
    unchanged = 0
    with original.open("rb") as old_stream, branded.open("rb") as new_stream:
        for old, new in zip(before["entries"], after["entries"], strict=True):
            assert old["name"] == new["name"], "Bundle entry identity changed."
            assert old["offset"] == new["offset"], "Bundle entry offset moved."
            if old["name"] == "LaoYing.Optimizer.dll":
                new_stream.seek(new["offset"])
                packed = new_stream.read(new["compressed_size"])
                extracted = zlib.decompress(packed, -15)
                assert len(extracted) == new["size"]
                assert extracted == (ROOT / "build" / "LaoYing.Optimizer.dll").read_bytes()
            else:
                assert old == new, "A dependency manifest entry changed."
                assert payload_digest(old_stream, old) == payload_digest(new_stream, new), "A dependency payload changed."
                unchanged += 1
    pe = pefile.PE(str(branded), fast_load=True)
    pe.parse_data_directories(directories=[2])
    info = {}
    for block in pe.FileInfo:
        for item in block:
            if item.Key == b"StringFileInfo":
                for table in item.StringTable:
                    info.update({key.decode(): value.decode() for key, value in table.entries.items()})
    assert info["ProductName"] == info["FileDescription"] == "喆好工具箱"
    release = json.loads((ROOT / "release.json").read_text(encoding="utf-8-sig"))
    assert info["FileVersion"] == info["ProductVersion"] == release["version"] + ".0"
    assert pe.OPTIONAL_HEADER.DATA_DIRECTORY[4].Size == 0
    icons = 0
    require_admin = False
    for kind in pe.DIRECTORY_ENTRY_RESOURCE.entries:
        for name in kind.directory.entries:
            for language in name.directory.entries:
                data = pe.get_data(language.data.struct.OffsetToData, language.data.struct.Size)
                if kind.id == 3:
                    with Image.open(io.BytesIO(data)) as image:
                        image.verify()
                    icons += 1
                elif kind.id == 24:
                    require_admin = b'requireAdministrator' in data
    assert icons == 9
    assert require_admin, "The release execution manifest changed."
    report = {"result": "passed", "retained_bundle_files": unchanged, "branded_assembly_verified": True,
              "native_icons_verified": icons, "product_name": info["ProductName"],
              "version": info["FileVersion"], "release_admin_manifest_preserved": require_admin,
              "update_repository": release["repository"],
              "runtime_execution_test": "pending"}
    (output_dir / "验证报告.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(report, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output-dir", type=Path, default=ROOT / "dist")
    main(parser.parse_args().output_dir)
