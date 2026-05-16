#!/usr/bin/env python3
# Extracts the GMW3110 $22 PID definition table from a 2 MiB E38 flash readback
# and emits a JSON array of PidDto-shaped placeholders sized for the simulator's
# config.json under ecus[i].pids[]. Auto-detects the table by signature; see
# tools/dps_utility_builder/reports/e38_bin_extraction_survey.md for the format.

import json
import struct
import sys
from pathlib import Path

DEFAULT_BIN = (
    r"C:\Users\Nathan\.claude\projects"
    r"\C--Users-Nathan-OneDrive-ECA-Resources-Visual-Studio-GM-ECU-Simulator"
    r"\agent_e38_extract\BINARY READ.bin"
)

# Acceptable record-type bytes seen in the survey (byte 0 of each 8-byte record).
VALID_TYPES = {0x01, 0x02, 0x04, 0x07, 0x0D}

# A record looks like <type:1><00><pid:2 BE><size:2 BE><ptr_lo:2 BE>.
RECORD_SIZE = 8

# Survey saw sizes 5..27; allow a slightly wider band so future bins still match.
MIN_SIZE = 1
MAX_SIZE = 0x100

# Confidence floor for the signature scan. The real table holds 536 records;
# anything below 200 is almost certainly a false positive on padding.
MIN_RUN = 200


def is_valid_record(data: bytes, off: int) -> bool:
    # Cheap structural validity check used by both the signature scanner and the
    # head/tail walk. Returns true only when the 8 bytes look like a real record.
    if off < 0 or off + RECORD_SIZE > len(data):
        return False
    if data[off + 1] != 0x00:
        return False
    rt = data[off]
    if rt not in VALID_TYPES:
        return False
    pid = (data[off + 2] << 8) | data[off + 3]
    if pid == 0 or pid == 0xFFFF:
        return False
    sz = (data[off + 4] << 8) | data[off + 5]
    if sz < MIN_SIZE or sz > MAX_SIZE:
        return False
    return True


def find_table_offset(data: bytes) -> int:
    # Scans the whole bin for the longest monotonically-PID-increasing run of
    # valid 8-byte records. Anchors on that run, then the caller walks back to
    # the head. The signature is highly specific - the survey reports a single
    # ~536-record run anywhere in this bin, so a >=MIN_RUN match is unambiguous.
    best_start = -1
    best_len = 0
    n = len(data)
    o = 0
    while o + RECORD_SIZE <= n:
        if not is_valid_record(data, o):
            o += 1
            continue
        # Found a candidate start; greedily extend while PIDs strictly increase.
        run_start = o
        prev_pid = (data[o + 2] << 8) | data[o + 3]
        run_len = 1
        p = o + RECORD_SIZE
        while p + RECORD_SIZE <= n and is_valid_record(data, p):
            pid = (data[p + 2] << 8) | data[p + 3]
            if pid <= prev_pid:
                break
            prev_pid = pid
            run_len += 1
            p += RECORD_SIZE
        if run_len > best_len:
            best_len = run_len
            best_start = run_start
        # Skip past the whole run so we do not re-scan every record inside it.
        o = p if run_len > 1 else o + 1

    if best_len < MIN_RUN or best_start < 0:
        raise SystemExit(
            f"PID table signature not found "
            f"(longest valid run = {best_len} records, need >= {MIN_RUN})"
        )
    return best_start


def walk_table(data: bytes, anchor: int):
    # Caller passes any offset inside the table. Walk backwards in 8-byte steps
    # to the head (first record whose predecessor is not a valid record), then
    # forward to the tail; yield (type, pid, size) tuples in table order.
    head = anchor
    while is_valid_record(data, head - RECORD_SIZE):
        # Also require monotonic PIDs across the boundary - guards against an
        # adjacent unrelated table that happens to match the record shape.
        prev_pid = (data[head - RECORD_SIZE + 2] << 8) | data[head - RECORD_SIZE + 3]
        cur_pid = (data[head + 2] << 8) | data[head + 3]
        if prev_pid >= cur_pid:
            break
        head -= RECORD_SIZE

    o = head
    prev_pid = -1
    while is_valid_record(data, o):
        rt = data[o]
        pid = (data[o + 2] << 8) | data[o + 3]
        sz = (data[o + 4] << 8) | data[o + 5]
        if pid <= prev_pid:
            break
        prev_pid = pid
        yield rt, pid, sz
        o += RECORD_SIZE


def pid_to_dto(rt: int, pid: int, size: int) -> dict:
    # Build a PidDto matching the simulator's schema. lengthBytes carries the
    # exact response size; staticBytes is all-zeros placeholder so the handler
    # returns the correct wire shape until the user supplies real values.
    return {
        "address": f"0x{pid:04X}",
        "name": f"PID 0x{pid:04X} (auto, {size} bytes, type 0x{rt:02X})",
        "size": "byte",
        "lengthBytes": size,
        "staticBytes": "0" * (2 * size),
        "dataType": "unsigned",
        "scalar": 1,
        "offset": 0,
        "unit": "",
        "waveform": {
            "shape": "constant",
            "amplitude": 0,
            "offset": 0,
            "frequencyHz": 1,
            "phaseDeg": 0,
            "dutyCycle": 0.5,
        },
    }


def main() -> int:
    bin_path = Path(sys.argv[1] if len(sys.argv) > 1 else DEFAULT_BIN)
    data = bin_path.read_bytes()

    anchor = find_table_offset(data)
    print(f"Detected $22 PID table head near offset 0x{anchor:06X}", file=sys.stderr)

    records = list(walk_table(data, anchor))
    # PID 0x0001 collides with the user's hand-tuned Engine RPM waveform; drop
    # the auto placeholder so the existing config entry survives the paste.
    pids = [pid_to_dto(rt, pid, sz) for rt, pid, sz in records if pid != 0x0001]

    json.dump(pids, sys.stdout, indent=2)
    sys.stdout.write("\n")

    print(f"Wrote {len(pids)} PIDs", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
