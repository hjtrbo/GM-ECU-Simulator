#!/usr/bin/env python3
"""
Parse a GM SPS utility file and print every field.

The parser mirrors what
MikeMcNamara-Ascential/CCRTCommon3700 Interpreter.cs does -- if a file
decodes cleanly here, the C# interpreter (and by extension DPS) should
accept it. Run this against build_utility_file.py output to round-trip
verify the builder.
"""
from __future__ import annotations

import argparse
import struct
import sys
from pathlib import Path

INTERP_TYPE_NAMES = {0: "UART", 1: "CLASS_2", 2: "KWP2000", 3: "GMLAN"}

OP_NAMES = {
    0x01: "SETUP_GLOBAL_VARIABLES",
    0x10: "INITIATE_DIAGNOSTIC_OPERATION",
    0x14: "CLEAR_DTCS",
    0x1A: "READ_DATA_BY_IDENTIFIER",
    0x20: "RETURN_TO_NORMAL_MODE",
    0x22: "READ_DATA_BY_PARAMETER_IDENTIFIER",
    0x27: "SECURITY_ACCESS",
    0x34: "REQUEST_DOWNLOAD",
    0x3B: "WRITE_DATA_BY_IDENTIFIER",
    0x50: "COMPARE_BYTES",
    0x51: "COMPARE_CHECKSUM",
    0x53: "COMPARE_DATA",
    0x54: "CHANGE_DATA",
    0x56: "INTERPRETER_IDENTIFIER",
    0x84: "SET_COMMUNICATIONS_PARAMETERS",
    0xA2: "REPORT_PROGRAMMED_STATE_AND_SAVE_RESPONSE",
    0xAA: "READ_DATA_BY_PACKET_IDENTIFIER",
    0xAE: "REQUEST_DEVICE_CONTROL",
    0xB0: "BLOCK_TRANSFER_TO_RAM",
    0xEE: "END_WITH_ERROR",
    0xF1: "SET_GLOBAL_MEMORY_ADDRESS",
    0xF2: "SET_GLOBAL_MEMORY_LENGTH",
    0xF3: "SET_GLOBAL_HEADER_LENGTH",
    0xF4: "IGNORE_RESPONSES_FOR_MILLISECONDS",
    0xF5: "OVERRIDE_MESSAGE_LENGTH",
    0xF7: "NO_OP",
    0xF8: "GOTO_FIELD_CONTINUATION",
    0xFB: "SET_AND_DECREMENT_COUNTER",
    0xFC: "DELAY_FOR_XX_SECONDS",
    0xFD: "RESET_COUNTER",
    0xFF: "END_WITH_SUCCESS",
}


def hex_dump(data: bytes, width: int = 16) -> str:
    return " ".join(f"{b:02X}" for b in data[:width])


def parse(blob: bytes) -> None:
    # Auto-detect PTI wrapper presence by looking at offset 0 for the
    # format-type magic. PTI files start with a u32 BE of 1 or 2; a bare
    # SPS-body file (--no-pti output) starts with the 2-byte checksum
    # which won't look like a valid PTI format type.
    has_pti = len(blob) >= 0x64 + 24 and struct.unpack(">I", blob[0:4])[0] in (1, 2)
    if not has_pti:
        print("=== PTI / API Header: NOT PRESENT (bare SPS body) ===")
        print(f"  file size: {len(blob)} bytes")
        _parse_sps_body(blob, base_offset=0)
        return

    if len(blob) < 0x64 + 24:
        raise SystemExit(f"File too short ({len(blob)} bytes)")

    # PTI header.
    (format_type,) = struct.unpack(">I", blob[0:4])
    part_no_ascii = blob[4:40].rstrip(b" \x00").decode("ascii", "replace")
    block_no, num_blocks = struct.unpack(">II", blob[40:48])
    date_ascii = blob[48:62].decode("ascii", "replace")
    data_type = blob[62]
    addr_bytes, data_len, crc_type, crc_bytes = struct.unpack(">IIII", blob[84:100])

    print("=== PTI / API Header (0x00..0x64) ===")
    print(f"  formatType         = {format_type}")
    print(f"  partNo (ASCII)     = {part_no_ascii!r}")
    print(f"  blockNo / numBlocks= {block_no} / {num_blocks}")
    print(f"  creationDate       = {date_ascii!r}")
    print(f"  dataType           = {data_type}")
    print(f"  noOfAddressBytes   = {addr_bytes}")
    print(
        f"  noOfDataBytes      = {data_len} "
        f"(expected blob-100 = {len(blob) - 0x64})"
    )
    print(f"  crcType / crcBytes = {crc_type} / {crc_bytes}")

    if format_type != 1:
        print(f"  WARNING: only format 1 supported by this parser")
        return

    _parse_sps_body(blob, base_offset=0x64, data_len=data_len)


def _parse_sps_body(blob: bytes, *, base_offset: int, data_len: int | None = None) -> None:
    if len(blob) < base_offset + 24:
        raise SystemExit(
            f"File too short ({len(blob)} bytes) for SPS header at offset {base_offset:#x}"
        )
    if data_len is None:
        data_len = len(blob) - base_offset

    # SPS interpreter header.
    sps = blob[base_offset : base_offset + 24]
    (
        checksum,
        module_id,
        sps_part_no,
        design_level,
        header_type,
        interp_type,
        routine_offset,
        add_type,
        data_addr_info,
        data_bytes_per_msg,
    ) = struct.unpack(">HHIHHHHHIH", sps)

    interp_name = INTERP_TYPE_NAMES.get(interp_type, f"unknown({interp_type})")
    print()
    print(f"=== SPS Interpreter Header ({base_offset:#x}..{base_offset + 24:#x}) ===")
    print(f"  checksum             = 0x{checksum:04X}")
    print(f"  moduleID             = 0x{module_id:04X}")
    print(f"  utility partNo       = 0x{sps_part_no:08X}")
    print(f"  designLevel          = 0x{design_level:04X}")
    print(f"  headerType           = 0x{header_type:04X}")
    print(f"  interpType           = {interp_type} ({interp_name})")
    print(f"  routineSectionOffset = 0x{routine_offset:04X}")
    print(f"  addType              = {add_type}")
    print(f"  dataAddressInfo      = 0x{data_addr_info:08X}")
    print(f"  dataBytesPerMessage  = {data_bytes_per_msg}")

    # Verify routineSectionOffset is sensible.
    inst_section_len = routine_offset - 24
    if inst_section_len < 0 or inst_section_len % 16 != 0:
        print(
            f"  ERROR: instruction-section length {inst_section_len} is not a multiple of 16"
        )
        return
    num_instructions = inst_section_len // 16
    print(f"  -> {num_instructions} interpreter instructions")

    # Interpreter instructions.
    print()
    print("=== Interpreter Instructions ===")
    inst_base = base_offset + 24
    for i in range(num_instructions):
        raw = blob[inst_base + i * 16 : inst_base + (i + 1) * 16]
        step, op = raw[0], raw[1]
        action = raw[2:6]
        gotos = raw[6:16]
        op_name = OP_NAMES.get(op, f"0x{op:02X}")
        print(
            f"  step {step:>3}  op=0x{op:02X} {op_name:<35}"
            f"  action={hex_dump(action)}  gotos={hex_dump(gotos)}"
        )

    # Routines.
    routine_base = base_offset + routine_offset
    routine_end = base_offset + data_len
    print()
    print(f"=== Routines ({routine_base:#x}..{routine_end:#x}) ===")
    cursor = routine_base
    idx = 0
    while cursor < routine_end:
        if routine_end - cursor < 6:
            print(f"  trailing bytes ({routine_end - cursor}): not enough for header")
            break
        addr, length = struct.unpack(">IH", blob[cursor : cursor + 6])
        data = blob[cursor + 6 : cursor + 6 + length]
        print(
            f"  routine[{idx}] addr=0x{addr:08X} len={length} "
            f"first_bytes={hex_dump(data, 16)}"
        )
        cursor += 6 + length
        idx += 1
    if idx == 0:
        print("  (none)")


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("path", type=Path)
    args = p.parse_args()
    blob = args.path.read_bytes()
    print(f"Parsing {args.path} ({len(blob)} bytes)")
    print()
    parse(blob)
    return 0


if __name__ == "__main__":
    sys.exit(main())
