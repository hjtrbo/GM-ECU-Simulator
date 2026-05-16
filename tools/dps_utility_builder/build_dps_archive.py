#!/usr/bin/env python3
"""
Build a SPAT-style DPS programming archive zip *without* using DPS's SPAT GUI.

Use when you already have a utility file + cal files extracted from an SPS
cache (or hand-built via build_utility_file.py) and want to package them into
a zip that DPS will accept via "Loading Calibration Files".

The .tbl format reverse-engineered from real SPAT output:
  Layout (fixed 5206 bytes regardless of populated slot count):
    +0x000  u32 LE   slot_count (1 utility + N cals)
    +0x004  ...      zero-padded header (300 bytes)
    +0x130           Utility slot (96 bytes):
                       +0..5    zero prefix
                       +6..55   filename (50 bytes, ASCII, zero-padded)
                       +56..95  label "Utility File" (40 bytes, zero-padded)
    +0x190..         50 cal-slot records (96 bytes each):
                       +0..1   u16 LE cal index (0..49)
                       +2..5   u32 LE size of *previous* slot's file
                       +6..55  filename (50 bytes, zero-padded)
                       +56..95 label "Description of Cal"
                     Unpopulated cal slots have empty filename / label, but
                     keep their index byte.
    +0x1450..0x1455  6-byte trailer: u16 LE cal index 50, u32 LE 0
"""
from __future__ import annotations

import argparse
import struct
import sys
import zipfile
from pathlib import Path


HEADER_SIZE = 0x130       # 304
UTILITY_SLOT_OFFSET = 0x130
CAL_SLOTS_OFFSET = 0x190
SLOT_SIZE = 0x60          # 96
NUM_CAL_SLOTS = 50        # SPAT writes the full 50-slot template every time
TBL_SIZE = 0x1456         # 5206
FILENAME_FIELD_OFFSET = 6
FILENAME_FIELD_SIZE = 50
LABEL_FIELD_OFFSET = 56
LABEL_FIELD_SIZE = 40
UTILITY_LABEL = b"Utility File"
CAL_LABEL = b"Description of Cal"
MAX_CAL_COUNT = NUM_CAL_SLOTS


def _write_ascii(buf: bytearray, off: int, field_size: int, text: bytes) -> None:
    """Write a zero-padded ASCII string into a fixed-size field."""
    if len(text) > field_size:
        raise ValueError(f"{text!r} exceeds {field_size}-byte field")
    buf[off : off + field_size] = b"\x00" * field_size
    buf[off : off + len(text)] = text


def build_tbl(utility_path: Path, cal_paths: list[Path]) -> bytes:
    """Compose the .tbl manifest for `utility_path` plus N cal files."""
    if len(cal_paths) > MAX_CAL_COUNT:
        raise ValueError(
            f"At most {MAX_CAL_COUNT} cal files supported by this .tbl layout; "
            f"got {len(cal_paths)}"
        )

    buf = bytearray(TBL_SIZE)

    # Slot count = 1 (utility) + len(cal_paths).
    struct.pack_into("<I", buf, 0, 1 + len(cal_paths))

    # Utility slot.
    utility_name = utility_path.name.encode("ascii")
    _write_ascii(buf, UTILITY_SLOT_OFFSET + FILENAME_FIELD_OFFSET,
                 FILENAME_FIELD_SIZE, utility_name)
    _write_ascii(buf, UTILITY_SLOT_OFFSET + LABEL_FIELD_OFFSET,
                 LABEL_FIELD_SIZE, UTILITY_LABEL)

    # Cal slots. Each entry's prev-size is the size of the file in the
    # immediately preceding slot - utility for cal[0], cal[i-1] for cal[i].
    # The prev-size field is also written into the *first empty* slot
    # (cal index == len(cal_paths)) so SPAT's "next available slot" pointer
    # knows the running tail offset; subsequent empty slots stay all zeros.
    files_in_order = [utility_path] + cal_paths
    for i in range(NUM_CAL_SLOTS):
        slot_off = CAL_SLOTS_OFFSET + i * SLOT_SIZE
        struct.pack_into("<H", buf, slot_off, i)  # cal index
        if i <= len(cal_paths):
            prev = files_in_order[i]  # i==len(cal_paths) -> last real cal
            struct.pack_into("<I", buf, slot_off + 2, prev.stat().st_size)
        if i < len(cal_paths):
            _write_ascii(buf, slot_off + FILENAME_FIELD_OFFSET,
                         FILENAME_FIELD_SIZE, cal_paths[i].name.encode("ascii"))
            _write_ascii(buf, slot_off + LABEL_FIELD_OFFSET,
                         LABEL_FIELD_SIZE, CAL_LABEL)

    # Trailer.
    struct.pack_into("<H", buf, 0x1450, 50)

    return bytes(buf)


def build_archive(
    archive_path: Path,
    utility_path: Path,
    cal_paths: list[Path],
    *,
    archive_name: str | None = None,
) -> int:
    """Write the .zip with .tbl + utility + cals to `archive_path`."""
    if archive_name is None:
        archive_name = archive_path.stem  # zip basename without .zip

    tbl_bytes = build_tbl(utility_path, cal_paths)

    print(f"Writing {archive_path}")
    print(f"  archive name: {archive_name}")
    print(f"  utility:      {utility_path.name} ({utility_path.stat().st_size} bytes)")
    for i, c in enumerate(cal_paths):
        print(f"  cal {i}:        {c.name} ({c.stat().st_size} bytes)")

    archive_path.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(archive_path, "w", compression=zipfile.ZIP_DEFLATED) as z:
        # Order matches what SPAT emits: utility first, then cals 1..N, then .tbl.
        z.writestr(utility_path.name, utility_path.read_bytes())
        for c in cal_paths:
            z.writestr(c.name, c.read_bytes())
        z.writestr(f"{archive_name}.tbl", tbl_bytes)

    print(f"  total:        {archive_path.stat().st_size} bytes")
    return 0


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--utility", type=Path, required=True,
                   help="Path to the utility-file bin (slot 0).")
    p.add_argument("--cal", type=Path, action="append", default=[],
                   help="Path to a calibration bin. Pass once per cal in the "
                        "order they should appear in the archive (matches the "
                        "Tunercat / SPS cache numbering).")
    p.add_argument("-o", "--output", type=Path, required=True,
                   help="Output zip path.")
    p.add_argument("--name", default=None,
                   help="Archive name (used for the .tbl filename inside the "
                        "zip). Defaults to the zip basename.")
    args = p.parse_args()

    if not args.utility.is_file():
        raise SystemExit(f"--utility {args.utility} not found")
    for c in args.cal:
        if not c.is_file():
            raise SystemExit(f"--cal {c} not found")

    return build_archive(args.output, args.utility, list(args.cal),
                         archive_name=args.name)


if __name__ == "__main__":
    sys.exit(main())
