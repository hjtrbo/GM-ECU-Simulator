#!/usr/bin/env python3
"""
Rebuild a DPS programming archive zip by swapping the utility-file bin
inside an existing SPAT-built archive.

SPAT (the GUI in dps.exe) wraps a utility file + .tbl manifest into a zip.
The .tbl is a 5206-byte derivative of the calfil00.DTM template with the
utility-file's filename baked in; it does NOT contain a checksum or size
of the bin. So as long as we keep the filename stable we can swap the bin
freely without re-running SPAT.

Usage:
    rebuild_archive.py <base-archive.zip> <new-utility.bin> [-o out.zip]

The output archive is identical to the input except the named entry's
payload comes from the new bin.
"""
from __future__ import annotations

import argparse
import sys
import zipfile
from pathlib import Path


def rebuild(base_zip: Path, new_bin: Path, target_entry: str | None, out_zip: Path) -> int:
    bin_bytes = new_bin.read_bytes()

    with zipfile.ZipFile(base_zip, "r") as src:
        entries = src.namelist()
        if target_entry is None:
            bin_entries = [n for n in entries if n.lower().endswith(".bin")]
            if len(bin_entries) != 1:
                raise SystemExit(
                    f"Cannot auto-pick utility entry; archive has {len(bin_entries)} "
                    f".bin entries: {bin_entries}. Pass --entry explicitly."
                )
            target_entry = bin_entries[0]
            print(f"Auto-picked utility entry: {target_entry}")

        if target_entry not in entries:
            raise SystemExit(f"Entry {target_entry!r} not found in {base_zip}")

        with zipfile.ZipFile(out_zip, "w", compression=zipfile.ZIP_DEFLATED) as dst:
            for name in entries:
                info = src.getinfo(name)
                if name == target_entry:
                    print(
                        f"  swapping {name}: {info.file_size} -> {len(bin_bytes)} bytes"
                    )
                    new_info = zipfile.ZipInfo(filename=name, date_time=info.date_time)
                    new_info.compress_type = info.compress_type
                    new_info.external_attr = info.external_attr
                    dst.writestr(new_info, bin_bytes)
                else:
                    print(f"  copying  {name}: {info.file_size} bytes")
                    dst.writestr(info, src.read(name))

    print(f"Wrote {out_zip} ({out_zip.stat().st_size} bytes)")
    return 0


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("base_zip", type=Path, help="Existing SPAT-built archive zip")
    p.add_argument("new_bin", type=Path, help="New utility-file bin to embed")
    p.add_argument(
        "--entry",
        default=None,
        help="Name of the entry to replace inside the zip (default: auto-detect the single .bin)",
    )
    p.add_argument(
        "-o",
        "--output",
        type=Path,
        default=None,
        help="Output zip path (default: overwrite base_zip in place)",
    )
    args = p.parse_args()

    out = args.output or args.base_zip
    if out == args.base_zip:
        # Write to a sibling temp file first to avoid clobbering on error.
        tmp = args.base_zip.with_suffix(args.base_zip.suffix + ".new")
        rc = rebuild(args.base_zip, args.new_bin, args.entry, tmp)
        if rc != 0:
            return rc
        tmp.replace(args.base_zip)
        print(f"Replaced {args.base_zip} in place")
        return 0
    else:
        return rebuild(args.base_zip, args.new_bin, args.entry, out)


if __name__ == "__main__":
    sys.exit(main())
