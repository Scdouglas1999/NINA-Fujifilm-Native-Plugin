#!/usr/bin/env python3
"""Verify the plugin's SDK interop layer against the real Fujifilm SDK.

Two checks, both of which have caught shipped defects:

  1. Every ``DllImport`` entry point the plugin declares is actually exported by
     ``XAPI.dll``. A missing export throws ``EntryPointNotFoundException`` at the first
     call, which no ``result != XSDK_COMPLETE`` check will catch.

  2. Every SDK constant, API code and API parameter in ``FujifilmSdkWrapper`` matches the
     value in the SDK headers, and every API parameter used as a single global value really
     is identical across all model headers.

CI cannot run this: the Fujifilm SDK is licensed and is not committed. Run it locally
before cutting a release.

    python3 build/verify-sdk-interop.py \
        --sdk-dll installer/Fujifilm/XAPI.dll \
        --headers /path/to/SDK/HEADERS

Exits non-zero if any check fails.
"""

from __future__ import annotations

import argparse
import pathlib
import re
import struct
import sys

WRAPPER = "src/NINA.Plugins.Fujifilm/Interop/FujifilmSdkWrapper.cs"

# Wrapper constant -> SDK header macro/enum, where the names differ.
NAME_OVERRIDES = {
    "XSDK_API_CODE_SetFocusPos": "API_CODE_SetFocusPos",
    "XSDK_API_CODE_GetFocusPos": "API_CODE_GetFocusPos",
    "XSDK_API_CODE_CapFocusPos": "API_CODE_CapFocusPos",
    "XSDK_API_CODE_SetFocusMode": "API_CODE_SetFocusMode",
    "XSDK_API_CODE_GetFocusMode": "API_CODE_GetFocusMode",
    "XSDK_API_CODE_CapFocusMode": "API_CODE_CapFocusMode",
    "XSDK_FOCUS_MANUAL": "SDK_FOCUS_MANUAL",
    "XSDK_FOCUS_AFS": "SDK_FOCUS_AFS",
    "XSDK_FOCUS_AFC": "SDK_FOCUS_AFC",
    "XSDK_LIVEVIEW_QUALITY_FINE": "SDK_LIVEVIEW_QUALITY_FINE",
    "XSDK_LIVEVIEW_QUALITY_NORMAL": "SDK_LIVEVIEW_QUALITY_NORMAL",
    "XSDK_LIVEVIEW_QUALITY_BASIC": "SDK_LIVEVIEW_QUALITY_BASIC",
    "XSDK_LIVEVIEW_SIZE_L": "SDK_LIVEVIEW_SIZE_L",
    "XSDK_LIVEVIEW_SIZE_M": "SDK_LIVEVIEW_SIZE_M",
    "XSDK_LIVEVIEW_SIZE_S": "SDK_LIVEVIEW_SIZE_S",
}

# Constants that are composed, plugin-internal, or deliberately not SDK names.
SKIP_CONSTANTS = {
    "XSDK_RELEASE_SHOOT_S1OFF",  # composed from two SDK values
    "XSDK_RELEASE_N_BULBS1OFF",  # composed from two SDK values
    "API_PARAM_CheckBatteryInfo_Body",
    "API_PARAM_CheckBatteryInfo_BodyRatio",
    "API_PARAM_CheckBatteryInfo_NewModels",
    "API_PARAM_CheckBatteryInfo_OldModels",
    "API_PARAM_LiveView",
    "API_PARAM_LensZoomPos",
    "API_PARAM_Aperture",
    "API_PARAM_DriveMode",
}


def pe_exports(path: pathlib.Path) -> set[str]:
    data = path.read_bytes()
    pe = struct.unpack_from("<I", data, 0x3C)[0]
    if data[pe : pe + 4] != b"PE\0\0":
        raise ValueError(f"{path} is not a PE image")
    section_count = struct.unpack_from("<H", data, pe + 6)[0]
    opt_size = struct.unpack_from("<H", data, pe + 20)[0]
    magic = struct.unpack_from("<H", data, pe + 24)[0]
    export_dir = pe + 24 + (112 if magic == 0x20B else 96)
    export_rva = struct.unpack_from("<I", data, export_dir)[0]

    sections = []
    table = pe + 24 + opt_size
    for i in range(section_count):
        off = table + 40 * i
        vsize = struct.unpack_from("<I", data, off + 8)[0]
        vaddr = struct.unpack_from("<I", data, off + 12)[0]
        rsize = struct.unpack_from("<I", data, off + 16)[0]
        raw = struct.unpack_from("<I", data, off + 20)[0]
        sections.append((vaddr, max(vsize, rsize), raw))

    def to_offset(rva: int) -> int:
        for vaddr, size, raw in sections:
            if vaddr <= rva < vaddr + size:
                return raw + (rva - vaddr)
        raise ValueError(f"RVA {rva:#x} is not mapped")

    base = to_offset(export_rva)
    name_count = struct.unpack_from("<I", data, base + 24)[0]
    name_table = to_offset(struct.unpack_from("<I", data, base + 32)[0])

    names = set()
    for i in range(name_count):
        rva = struct.unpack_from("<I", data, name_table + 4 * i)[0]
        off = to_offset(rva)
        names.add(data[off : data.index(b"\0", off)].decode())
    return names


def read_headers(headers: pathlib.Path) -> tuple[dict[str, int], dict[str, dict[str, int]]]:
    """Return (global constants, per-model API parameters)."""
    globals_: dict[str, int] = {}
    per_model: dict[str, dict[str, int]] = {}

    for path in sorted(headers.iterdir()):
        if path.suffix.lower() != ".h":
            continue
        text = path.read_bytes().decode("latin-1")
        stem = path.stem
        is_shared = stem.upper() in ("XAPI", "XAPIOPT")

        for name, value in re.findall(r"#define\s+([A-Za-z_]\w*)\s+(0x[0-9A-Fa-f]+|-?\d+)", text):
            if is_shared:
                globals_.setdefault(name, int(value, 0))
        # Enum members, including the last one in a block, which has no trailing comma.
        for name, value in re.findall(
            r"\b([A-Za-z_]\w*)\s*=\s*(0x[0-9A-Fa-f]+|-?\d+)\s*(?=[,}])", text
        ):
            if is_shared:
                globals_.setdefault(name, int(value, 0))
            elif "_MOV" not in stem:
                per_model.setdefault(stem, {})[name] = int(value, 0)

    return globals_, per_model


def parse_wrapper(root: pathlib.Path) -> tuple[list[str], dict[str, int]]:
    text = (root / WRAPPER).read_text()
    entry_points = sorted(set(re.findall(r'EntryPoint\s*=\s*"([^"]+)"', text)))
    constants: dict[str, int] = {}
    for name, value in re.findall(
        r"public const int\s+(\w+)\s*=\s*(0x[0-9A-Fa-f]+|-?\d+)\s*;", text
    ):
        constants[name] = int(value, 0)
    return entry_points, constants


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--sdk-dll", type=pathlib.Path, default=pathlib.Path("installer/Fujifilm/XAPI.dll"))
    parser.add_argument("--headers", type=pathlib.Path, required=True)
    parser.add_argument("--root", type=pathlib.Path, default=pathlib.Path("."))
    args = parser.parse_args()

    failures: list[str] = []
    checked = 0

    entry_points, constants = parse_wrapper(args.root)

    # --- 1. every declared entry point exists in the shipped DLL -------------------
    if args.sdk_dll.exists():
        exports = pe_exports(args.sdk_dll)
        print(f"XAPI.dll exports {len(exports)} symbols; wrapper declares {len(entry_points)}")
        for name in entry_points:
            checked += 1
            if name not in exports:
                failures.append(f"DllImport EntryPoint '{name}' is not exported by {args.sdk_dll}")
    else:
        print(f"WARNING: {args.sdk_dll} not found; skipping export check")

    # --- 2. every constant matches the SDK headers --------------------------------
    globals_, per_model = read_headers(args.headers)
    print(f"headers define {len(globals_)} shared constants across {len(per_model)} model files")

    for name, value in sorted(constants.items()):
        if name in SKIP_CONSTANTS:
            continue
        header_name = NAME_OVERRIDES.get(name, name)
        if header_name in globals_:
            checked += 1
            if globals_[header_name] != value:
                failures.append(
                    f"{name} = {value:#x} but {header_name} = {globals_[header_name]:#x} in the SDK headers"
                )
            continue

        # API parameters are declared per model; require that every model that defines the
        # parameter agrees with the single value the wrapper uses.
        match = re.match(r"(?:XSDK_)?API_PARAM_(\w+)$", name)
        if match:
            suffix = f"API_PARAM_{match.group(1)}"
            seen = {
                model: values[key]
                for model, values in per_model.items()
                for key in values
                if key.endswith(suffix)
            }
            supported = {m: v for m, v in seen.items() if v != -1}
            if supported:
                checked += 1
                divergent = {m: v for m, v in supported.items() if v != value}
                if divergent:
                    failures.append(
                        f"{name} = {value} but these models disagree: "
                        + ", ".join(f"{m}={v}" for m, v in sorted(divergent.items()))
                    )
                continue
        print(f"  note: {name} has no counterpart in the headers (not verified)")

    print(f"\n{checked} checks run")
    if failures:
        print(f"\n{len(failures)} FAILURE(S):")
        for failure in failures:
            print(f"  - {failure}")
        return 1
    print("all interop checks passed")
    return 0


if __name__ == "__main__":
    sys.exit(main())
