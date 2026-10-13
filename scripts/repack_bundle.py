"""Apply the branded assembly and PE resources while retaining original dependencies."""
from __future__ import annotations

import argparse
import hashlib
import io
import json
from pathlib import Path
import shutil
import struct
import sys
import zlib

import pefile
from PIL import Image
from inspect_bundle import SIGNATURE, parse_manifest

ROOT = Path(__file__).resolve().parent.parent
ORIGINAL = ROOT / "inspection" / "LaoYing.Toolkit.v1.8.4.exe"
EXPECTED = "b784c1c024d0eee3b570b0471f91b13b516858fcd14921292c5c0d0c1fffa18d"
BRAND = "喆好工具箱"
RELEASE = json.loads((ROOT / "release.json").read_text(encoding="utf-8-sig"))
TYPE_IDS = {"unknown": 0, "assembly": 1, "native": 2, "deps": 3, "runtimeconfig": 4, "symbols": 5}


def align4(value: int) -> int:
    return (value + 3) & ~3


def parse_version_block(data: bytes, offset: int = 0) -> dict:
    length, value_length, kind = struct.unpack_from("<HHH", data, offset)
    end = offset + length
    cursor = offset + 6
    key_end = cursor
    while data[key_end:key_end + 2] != b"\0\0":
        key_end += 2
    key = data[cursor:key_end].decode("utf-16le")
    cursor = align4(key_end + 2)
    byte_length = value_length * 2 if kind == 1 else value_length
    value = data[cursor:cursor + byte_length]
    cursor = align4(cursor + byte_length)
    children = []
    while cursor + 6 <= end:
        child_length = struct.unpack_from("<H", data, cursor)[0]
        if not child_length:
            break
        child = parse_version_block(data, cursor)
        children.append(child)
        cursor = align4(cursor + child_length)
    return {"key": key, "kind": kind, "value": value, "children": children}


def build_version_block(block: dict) -> bytes:
    value = block["value"]
    data = bytearray(b"\0" * 6 + block["key"].encode("utf-16le") + b"\0\0")
    data.extend(b"\0" * (align4(len(data)) - len(data)))
    data.extend(value)
    for child in block["children"]:
        data.extend(b"\0" * (align4(len(data)) - len(data)))
        data.extend(build_version_block(child))
    value_length = len(value) // 2 if block["kind"] == 1 else len(value)
    struct.pack_into("<HHH", data, 0, len(data), value_length, block["kind"])
    return bytes(data)


def brand_version(block: dict) -> None:
    if block["key"] in ("ProductName", "FileDescription"):
        block["value"] = BRAND.encode("utf-16le") + b"\0\0"
    elif block["key"] in ("FileVersion", "ProductVersion"):
        block["value"] = (RELEASE["version"] + ".0").encode("utf-16le") + b"\0\0"
    elif block["key"] == "OriginalFilename":
        block["value"] = "喆好工具箱.exe".encode("utf-16le") + b"\0\0"
    elif block["key"] == "VS_VERSION_INFO":
        major, minor, patch = (int(value) for value in RELEASE["version"].split("."))
        fixed = bytearray(block["value"])
        struct.pack_into("<IIII", fixed, 8, (major << 16) | minor, patch << 16, (major << 16) | minor, patch << 16)
        block["value"] = bytes(fixed)
    for child in block["children"]:
        brand_version(child)


def encode_string(value: str) -> bytes:
    data = value.encode("utf-8")
    length = len(data)
    prefix = bytearray()
    while length >= 0x80:
        prefix.append((length & 0x7F) | 0x80)
        length >>= 7
    prefix.append(length)
    return bytes(prefix) + data


def encode_manifest(report: dict) -> bytes:
    major, minor = (int(value) for value in report["version"].split("."))
    data = bytearray(struct.pack("<III", major, minor, report["count"]))
    data.extend(encode_string(report["bundle_id"]))
    if major >= 2:
        metadata = report["metadata"]
        data.extend(struct.pack("<QQQQQ", metadata["deps_offset"], metadata["deps_size"],
                                metadata["runtimeconfig_offset"], metadata["runtimeconfig_size"], metadata["flags"]))
    for entry in report["entries"]:
        data.extend(struct.pack("<QQ", entry["offset"], entry["size"]))
        if major >= 6:
            data.extend(struct.pack("<Q", entry["compressed_size"]))
        data.append(TYPE_IDS[entry["type"]])
        data.extend(encode_string(entry["name"]))
    return bytes(data)


def native_png(size: int, capacity: int) -> bytes:
    path = ROOT / "assets" / f"zhehao-{size}.png"
    if path.stat().st_size <= capacity:
        return path.read_bytes()
    with Image.open(path) as image:
        for colors in (64, 32, 16, 8, 4):
            output = io.BytesIO()
            image.quantize(colors=colors, method=Image.Quantize.FASTOCTREE).save(output, "PNG", optimize=True)
            data = output.getvalue()
            if len(data) <= capacity:
                return data
    raise RuntimeError(f"The {size}px brand icon exceeds its native resource slot ({capacity} bytes).")


def patch_native_resources(header: bytearray, inspection: bool) -> dict:
    pe = pefile.PE(data=bytes(header))
    counts = {"icons": 0, "version": 0, "manifest": 0}
    icon_lengths = {}

    def replace_resource(entry, payload: bytes) -> None:
        structure = entry.data.struct
        if len(payload) > structure.Size:
            raise RuntimeError("Native resource replacement would move bundle offsets.")
        offset = pe.get_offset_from_rva(structure.OffsetToData)
        header[offset:offset + structure.Size] = payload + b"\0" * (structure.Size - len(payload))
        struct.pack_into("<I", header, structure.get_file_offset() + 4, len(payload))

    for resource_type in pe.DIRECTORY_ENTRY_RESOURCE.entries:
        for resource_name in resource_type.directory.entries:
            for language in resource_name.directory.entries:
                data = pe.get_data(language.data.struct.OffsetToData, language.data.struct.Size)
                if resource_type.id == 3:
                    if not data.startswith(b"\x89PNG"):
                        raise RuntimeError("Unexpected native icon encoding.")
                    width, height = struct.unpack_from(">II", data, 16)
                    if width != height:
                        raise RuntimeError("Non-square native icon.")
                    payload = native_png(width, len(data))
                    replace_resource(language, payload)
                    icon_lengths[resource_name.id] = len(payload)
                    counts["icons"] += 1
                elif resource_type.id == 16:
                    version = parse_version_block(data)
                    brand_version(version)
                    replace_resource(language, build_version_block(version))
                    counts["version"] += 1
                elif resource_type.id == 24 and inspection:
                    replace_resource(language, data.replace(b"requireAdministrator", b"asInvoker"))
                    counts["manifest"] += 1
    for resource_type in pe.DIRECTORY_ENTRY_RESOURCE.entries:
        if resource_type.id != 14:
            continue
        for resource_name in resource_type.directory.entries:
            for language in resource_name.directory.entries:
                data = bytearray(pe.get_data(language.data.struct.OffsetToData, language.data.struct.Size))
                count = struct.unpack_from("<H", data, 4)[0]
                for index in range(count):
                    at = 6 + index * 14
                    resource_id = struct.unpack_from("<H", data, at + 12)[0]
                    struct.pack_into("<I", data, at + 8, icon_lengths[resource_id])
                replace_resource(language, bytes(data))
    # A modified executable must not retain an invalid publisher signature.
    security = pe.OPTIONAL_HEADER.DATA_DIRECTORY[4]
    struct.pack_into("<II", header, security.get_file_offset(), 0, 0)
    struct.pack_into("<I", header, pe.OPTIONAL_HEADER.get_field_absolute_offset("CheckSum"), 0)
    return counts


def main(args: argparse.Namespace) -> None:
    sys.stdout.reconfigure(encoding="utf-8")
    with ORIGINAL.open("rb") as original:
        original_digest = hashlib.file_digest(original, "sha256").hexdigest()
    if original_digest != EXPECTED:
        raise RuntimeError("The original release hash does not match the official asset.")
    assembly_path = ROOT / "build" / ("LaoYing.Optimizer.inspection.dll" if args.inspection and not args.smoke else "LaoYing.Optimizer.dll")
    assembly = assembly_path.read_bytes()
    with ORIGINAL.open("rb") as original:
        header = bytearray(original.read(10015744))
        marker = header.find(SIGNATURE)
        if marker < 8:
            raise RuntimeError("The .NET bundle marker is missing.")
        manifest_offset = struct.unpack_from("<Q", header, marker - 8)[0]
        original.seek(manifest_offset)
        report = parse_manifest(original.read())
    pe = pefile.PE(data=bytes(header))
    certificate_offset = pe.OPTIONAL_HEADER.DATA_DIRECTORY[4].VirtualAddress
    changes = patch_native_resources(header, args.inspection)
    compressed = zlib.compressobj(level=9, wbits=-15)
    payload = compressed.compress(assembly) + compressed.flush()
    main_entry = next(entry for entry in report["entries"] if entry["name"] == "LaoYing.Optimizer.dll")
    original_payload_size = main_entry["compressed_size"] or main_entry["size"]
    if len(payload) > original_payload_size:
        raise RuntimeError("The branded assembly exceeds its bundle slot.")
    main_entry["size"] = len(assembly)
    main_entry["compressed_size"] = len(payload)
    assembly_digest = hashlib.sha256(assembly).hexdigest()
    report["bundle_id"] = "ZH" + assembly_digest[:len(report["bundle_id"]) - 2]
    manifest = encode_manifest(report)
    if len(manifest) != report["manifest_size"]:
        raise RuntimeError("Bundle manifest size changed unexpectedly.")
    output_name = "喆好工具箱-启动检查.exe" if args.smoke else ("喆好工具箱-界面检查.exe" if args.inspection else "喆好工具箱.exe")
    output = (ROOT / "build" if args.inspection else args.output_dir) / output_name
    output.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(ORIGINAL, output)
    with output.open("r+b") as target:
        target.seek(0)
        target.write(header)
        target.seek(main_entry["offset"])
        target.write(payload)
        target.write(b"\0" * (original_payload_size - len(payload)))
        target.seek(manifest_offset)
        target.write(manifest)
        if certificate_offset:
            target.truncate(certificate_offset)
    with output.open("rb") as result:
        result_digest = hashlib.file_digest(result, "sha256").hexdigest()
    metadata = {"brand": BRAND, "version": RELEASE["version"], "update_repository": RELEASE["repository"],
                "original_sha256": EXPECTED, "output_sha256": result_digest,
                "assembly_sha256": assembly_digest, "output": output.name,
                "size": output.stat().st_size, "bundle_id": report["bundle_id"], "native_resources": changes,
                "inspection_build": args.inspection, "original_signature_retained": False}
    output.with_suffix(".build.json").write_text(json.dumps(metadata, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(metadata, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--inspection", action="store_true")
    parser.add_argument("--smoke", action="store_true", help="Inspect the exact release assembly with a non-elevated host.")
    parser.add_argument("--output-dir", type=Path, default=ROOT / "dist", help="Release artifact directory.")
    args = parser.parse_args()
    if args.smoke:
        args.inspection = True
    main(args)
