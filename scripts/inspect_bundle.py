"""Statically inspect a .NET single-file bundle without starting the program."""
from __future__ import annotations

import argparse
import io
import json
from pathlib import Path
import struct
import sys
import urllib.request
import zlib


SIGNATURE = bytes.fromhex(
    "8b1202b96a612038727b930214d7a03213f5b9e6efae3318ee3b2dce24b36aae"
)
FILE_TYPES = {0: "unknown", 1: "assembly", 2: "native", 3: "deps", 4: "runtimeconfig", 5: "symbols"}


def read_remote_range(url: str, offset: int, length: int) -> bytes:
    request = urllib.request.Request(
        url,
        headers={"User-Agent": "Zhehao-Static-Inspection/1.0", "Range": f"bytes={offset}-{offset + length - 1}"},
    )
    with urllib.request.urlopen(request, timeout=35) as response:
        content_range = response.headers.get("Content-Range", "")
        if response.status != 206 or not content_range.startswith(f"bytes {offset}-"):
            raise ValueError(f"Server did not honor the requested range (HTTP {response.status}).")
        data = response.read(length + 1)
        if len(data) != length:
            raise ValueError(f"Range length mismatch: expected {length}, received {len(data)}.")
        return data


def read_string(reader: io.BytesIO) -> str:
    length = 0
    for index in range(5):
        byte = reader.read(1)
        if not byte:
            raise ValueError("Unexpected end of bundle string.")
        value = byte[0]
        length |= (value & 0x7F) << (7 * index)
        if not value & 0x80:
            if length > 1024 * 1024:
                raise ValueError("Bundle string exceeds inspection limit.")
            payload = reader.read(length)
            if len(payload) != length:
                raise ValueError("Truncated bundle string.")
            return payload.decode("utf-8")
    raise ValueError("Invalid string length.")


def parse_manifest(data: bytes) -> dict:
    reader = io.BytesIO(data)
    major, minor, count = struct.unpack("<III", reader.read(12))
    if major not in (1, 2, 6) or count > 10000:
        raise ValueError(f"Unsupported or invalid bundle header: {major}.{minor}, {count} files.")
    bundle_id = read_string(reader)
    metadata = {}
    if major >= 2:
        deps_offset, deps_size, config_offset, config_size, flags = struct.unpack("<QQQQQ", reader.read(40))
        metadata = {"deps_offset": deps_offset, "deps_size": deps_size, "runtimeconfig_offset": config_offset,
                    "runtimeconfig_size": config_size, "flags": flags}
    entries = []
    for _ in range(count):
        offset, size = struct.unpack("<QQ", reader.read(16))
        compressed_size = struct.unpack("<Q", reader.read(8))[0] if major >= 6 else 0
        file_type = reader.read(1)[0]
        name = read_string(reader)
        entries.append({"name": name, "offset": offset, "size": size, "compressed_size": compressed_size,
                        "type": FILE_TYPES.get(file_type, f"unknown-{file_type}")})
    return {"version": f"{major}.{minor}", "bundle_id": bundle_id, "count": count,
            "manifest_size": reader.tell(), "metadata": metadata, "entries": entries}


def inspect(args: argparse.Namespace) -> None:
    with args.file.open("rb") as stream:
        prefix = stream.read(32 * 1024 * 1024)
    signature_at = prefix.find(SIGNATURE)
    if signature_at < 8:
        raise ValueError("No .NET bundle marker was found in the executable header.")
    header_offset = struct.unpack("<Q", prefix[signature_at - 8:signature_at])[0]
    if not header_offset or header_offset >= args.total_size:
        raise ValueError(f"Invalid manifest offset: {header_offset}.")
    manifest_length = min(args.total_size - header_offset, 1024 * 1024)
    if args.file.stat().st_size >= header_offset + manifest_length:
        with args.file.open("rb") as stream:
            stream.seek(header_offset)
            manifest_data = stream.read(manifest_length)
    elif args.url:
        print(f"Fetching bundle manifest at offset {header_offset} ({manifest_length} bytes).", flush=True)
        manifest_data = read_remote_range(args.url, header_offset, manifest_length)
    else:
        print(json.dumps({"marker_offset": signature_at, "manifest_offset": header_offset}, indent=2))
        return
    report = parse_manifest(manifest_data)
    report.update({"source": args.file.name, "marker_offset": signature_at, "manifest_offset": header_offset,
                   "total_size": args.total_size, "execution": "static inspection only; executable was not launched"})
    args.output.mkdir(parents=True, exist_ok=True)
    (args.output / "bundle-report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({key: value for key, value in report.items() if key != "entries"}, indent=2, ensure_ascii=False))
    for entry in report["entries"]:
        if entry["type"] in ("assembly", "deps", "runtimeconfig") and ("laoying" in entry["name"].lower() or entry["type"] in ("deps", "runtimeconfig")):
            print(f"{entry['type']:14} {entry['size']:12} {entry['name']}")
        if args.extract_main and ("laoying" in entry["name"].lower() or "flexcore" in entry["name"].lower()
                                  or entry["type"] in ("deps", "runtimeconfig")):
            stored_length = entry["compressed_size"] or entry["size"]
            if stored_length > 30 * 1024 * 1024:
                print(f"Skipped large entry: {entry['name']}")
                continue
            if args.file.stat().st_size >= entry["offset"] + stored_length:
                with args.file.open("rb") as stream:
                    stream.seek(entry["offset"])
                    payload = stream.read(stored_length)
            else:
                print(f"Fetching {entry['name']} ({stored_length} bytes).", flush=True)
                payload = read_remote_range(args.url, entry["offset"], stored_length)
            if entry["compressed_size"]:
                payload = zlib.decompress(payload, -15)
            if len(payload) != entry["size"]:
                raise ValueError(f"Uncompressed size mismatch: {entry['name']}")
            target = args.output / "assemblies" / entry["name"]
            if not target.resolve().is_relative_to((args.output / "assemblies").resolve()):
                raise ValueError("Unsafe bundle entry path.")
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(payload)
            print(f"Extracted {entry['name']}.", flush=True)


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("file", type=Path)
    parser.add_argument("--total-size", type=int, default=510584240)
    parser.add_argument("--url")
    parser.add_argument("--output", type=Path, default=Path("inspection"))
    parser.add_argument("--extract-main", action="store_true")
    inspect(parser.parse_args())
