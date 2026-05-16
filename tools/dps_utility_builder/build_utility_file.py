#!/usr/bin/env python3
"""
Build a minimal GM SPS Utility File targeting the GM ECU Simulator.

Wire format reverse-engineered from MikeMcNamara-Ascential/CCRTCommon3700
(Source/Isuzu/FlashStation/UtilityFileInterpreter/Interpreter.cs +
OpCodeHandler.cs) and cross-checked against the local
DPS Programmers Reference Manual.pdf (Appendix D, Interpreter 3 GMLAN op-codes).

Layout produced:

  +0x00  PTI / API header (100 bytes, formatType=1)
           formatType u32 BE | partNo[36] ASCII | blockNo u32 BE
           noOfBlocks u32 BE | dataCreationDate[14] ASCII
           dataType u8 | spare[21] | noOfAddressBytes u32 BE
           noOfDataBytes u32 BE | crcType u32 BE | noOfCRCBytes u32 BE
  +0x64  SPS Interpreter header (24 bytes)
           checksum u16 BE | moduleID u16 BE | partNo u32 BE
           designLevel u16 BE | headerType u16 BE | interpType u16 BE
           routineSectionOffset u16 BE | addType u16 BE
           dataAddressInfo u32 BE | dataBytesPerMessage u16 BE
  +0x7C  Interpreter instructions (16 bytes each)
           step u8 | opCode u8 | actionFields[4] | gotoFields[10]
  +...   Routines (variable)
           address u32 BE | length u16 BE | data[length]
"""
from __future__ import annotations

import argparse
import struct
import sys
from dataclasses import dataclass, field
from pathlib import Path


PTI_FORMAT_TYPE = 1
INTERP_TYPE_GMLAN = 3
ADD_TYPE_GMLAN = 4
PTI_HEADER_SIZE = 0x64
SPS_HEADER_SIZE = 24
INSTRUCTION_SIZE = 16


# Interpreter 3 (GMLAN) op-code constants -- mirror of OpCodeHandler.GMLANOpCodes.
OP_SETUP_GLOBAL_VARIABLES = 0x01
OP_INITIATE_DIAGNOSTIC_OPERATION = 0x10
OP_READ_DATA_BY_PARAMETER_IDENTIFIER = 0x22
OP_SECURITY_ACCESS = 0x27
OP_REQUEST_DOWNLOAD = 0x34
OP_TESTER_PRESENT = 0x3E
OP_NO_OP = 0xF7
OP_END_WITH_ERROR = 0xEE
OP_END_WITH_SUCCESS = 0xFF


@dataclass
class Instruction:
    """One 16-byte instruction line in the interpreter section."""

    op_code: int
    action: bytes = b"\x00\x00\x00\x00"
    gotos: bytes = b"\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00"
    # step number is assigned at assembly time (1-indexed).

    def pack(self, step: int) -> bytes:
        assert len(self.action) == 4
        assert len(self.gotos) == 10
        return bytes([step, self.op_code]) + self.action + self.gotos


@dataclass
class Routine:
    """One (address, length, data) tuple in the routine section."""

    address: int
    data: bytes

    def pack(self) -> bytes:
        return struct.pack(">IH", self.address, len(self.data)) + self.data


@dataclass
class UtilityFile:
    """In-memory representation of a utility file."""

    module_id: int = 0x0062  # HUD diag address; override per ECU.
    part_no: int = 0x12345678
    design_level: int = 0x0001
    data_bytes_per_message: int = 0x00FE  # 254-byte chunks for ISO-TP
    instructions: list[Instruction] = field(default_factory=list)
    routines: list[Routine] = field(default_factory=list)
    # ASCII metadata for the outer PTI header.
    pti_part_no: str = "GMSIM_DPSDEMO_0001"  # 36 bytes ASCII, padded with spaces
    creation_date: str = "20260516000000"  # YYYYMMDDHHMMSS

    def assemble(self, *, include_pti: bool = True) -> bytes:
        """Assemble the file.

        ``include_pti=True``  -> outer PTI/API wrapper (100 B) + SPS body.
                                 What CCRT's Interpreter.openUtilityFile()
                                 expects. Required when handing the file
                                 to a tool that parses the PTI envelope
                                 (TIS2WEB-style consumers).
        ``include_pti=False`` -> bare SPS body only (24 B header +
                                 instructions + routines). DPS's SPAT
                                 picker filter is "Binary Utility File
                                 (*.bin)" and its auto-converter chokes on
                                 a PTI header it thinks is a .pti file --
                                 use this mode for SPAT.
        """
        # 1. Pack instruction section.
        inst_bytes = b"".join(
            inst.pack(step) for step, inst in enumerate(self.instructions, start=1)
        )
        # 2. Pack routine section.
        rout_bytes = b"".join(r.pack() for r in self.routines)
        # 3. SPS header. routineSectionOffset is the byte offset from the
        # start of the SPS header to the first routine, i.e.
        #   24 (header) + len(instructions)
        routine_offset = SPS_HEADER_SIZE + len(inst_bytes)
        body_len = routine_offset + len(rout_bytes)  # bytes from +0x64 onward
        # checksum is computed *after* we know the body bytes; placeholder = 0.
        sps_header = struct.pack(
            ">HHIHHHHHIH",
            0x0000,  # checksum (filled in below)
            self.module_id,
            self.part_no,
            self.design_level,
            0x0000,  # headerType
            INTERP_TYPE_GMLAN,
            routine_offset,
            ADD_TYPE_GMLAN,
            0x00000000,  # dataAddressInfo
            self.data_bytes_per_message,
        )
        assert len(sps_header) == SPS_HEADER_SIZE
        body = sps_header + inst_bytes + rout_bytes
        # 4. Compute checksum: 16-bit sum of all body bytes *after* the
        # checksum field itself. This is the conventional SPS convention --
        # the CCRT parser stores the value but never validates it, so any
        # consistent rule is acceptable; we use a plain wrapping sum.
        chksum = sum(body[2:]) & 0xFFFF
        body = struct.pack(">H", chksum) + body[2:]
        # 5. PTI / API header.
        partno_ascii = self.pti_part_no.encode("ascii").ljust(36, b" ")
        date_ascii = self.creation_date.encode("ascii").ljust(14, b"0")
        assert len(partno_ascii) == 36
        assert len(date_ascii) == 14
        pti = (
            struct.pack(">I", PTI_FORMAT_TYPE)
            + partno_ascii
            + struct.pack(">II", 1, 1)  # blockNo=1, noOfBlocks=1
            + date_ascii
            + bytes([0])  # dataType=normal
            + b"\x00" * 21  # spare
            + struct.pack(">I", 0)  # noOfAddressBytes=0
            + struct.pack(">I", len(body))  # noOfDataBytes
            + struct.pack(">I", 0)  # crcType=none
            + struct.pack(">I", 0)  # noOfCRCBytes=0
        )
        assert len(pti) == PTI_HEADER_SIZE, f"PTI header is {len(pti)} bytes"
        if not include_pti:
            return body
        return pti + body


def build_minimal_demo(
    module_id: int = 0x62,
    diag_target: int = 0x11,
    diag_source: int = 0xF1,
) -> UtilityFile:
    """Build the smallest interesting utility file: setup vars, init diag
    session, request security access, then end with success.

    The actionFields/gotoFields here match the canonical pattern documented
    in the DPS Programmers Reference Manual page 156 ("Typical Interpreter
    Line"). AC0 = target ECU diag address (NOT the GMW3110 functional byte;
    $FE is reserved as a fatal-error sentinel by both the CCRT C# interpreter
    and DPS itself - "Setting the Target Address to $FE will cause a fatal
    error"). Default $11 targets the simulator's SPS_Type_C ECU on $311.

    The simulator's $27 handler enters a "security programming shortcut"
    when $A5 $03 activates programming mode (see Core/Services/ServiceA5
    Handler.cs comment near node.State.SecurityProgrammingShortcutActive)
    so the request-seed step should either succeed outright or get redirected
    to the success branch.
    """
    # gotoFields layout for op-codes that go through GMLANResponseProcessing
    # is pairs of (match_byte, next_step):
    #   - Match on the positive-response service-ID echo for the success path
    #     ($10 -> $50, $27 -> $67, $34 -> $74, $36 -> $76, ...).
    #   - Match $FF as a catch-all so any unexpected response routes to the
    #     END_WITH_ERROR step instead of triggering a "Communication Failure"
    #     abort (per OpCodeHandler.GMLANResponseProcessing default path).
    # See DPS PM p157 (`$10`) and p164 (`$27`) for the canonical examples.
    def gotos(*pairs: tuple[int, int]) -> bytes:
        out = bytearray(10)
        for i, (match, step) in enumerate(pairs):
            out[i * 2] = match & 0xFF
            out[i * 2 + 1] = step & 0xFF
        return bytes(out)

    POS_10 = 0x50  # positive SID echo: $10 InitiateDiagnosticOperation
    POS_27 = 0x67  # positive SID echo: $27 SecurityAccess
    CATCH_ALL = 0xFF  # GMLANResponseProcessing's catch-all match value

    SUCCESS = 4  # END_WITH_SUCCESS step number
    ERROR = 5  # END_WITH_ERROR step number

    insts = [
        # 1. SETUP_GLOBAL_VARIABLES. Returns gotoFields[1] directly without
        # going through GMLANResponseProcessing, so the goto match-byte is
        # ignored and only byte index 1 ("next step") matters. Matches the
        # DPS PM p156 canonical example "01 01 11 F1 00 00  00 02 ...".
        Instruction(
            op_code=OP_SETUP_GLOBAL_VARIABLES,
            action=bytes([diag_target, diag_source, 0x00, 0x00]),
            gotos=gotos((0x00, 2)),
        ),
        # 2. INITIATE_DIAGNOSTIC_OPERATION sub-func 0x02 (disableAllDTCs).
        # TX = "10 02"; positive echo $50.
        Instruction(
            op_code=OP_INITIATE_DIAGNOSTIC_OPERATION,
            action=bytes([0x02, 0x00, 0x00, 0x00]),
            gotos=gotos((POS_10, 3), (CATCH_ALL, ERROR)),
        ),
        # 3. SECURITY_ACCESS request seed via "27 01"; positive echo $67.
        Instruction(
            op_code=OP_SECURITY_ACCESS,
            action=bytes([0x01, 0x00, 0x00, 0x00]),
            gotos=gotos((POS_27, SUCCESS), (CATCH_ALL, ERROR)),
        ),
        # 4. END_WITH_SUCCESS.
        Instruction(op_code=OP_END_WITH_SUCCESS),
        # 5. END_WITH_ERROR -- the catch-all destination for steps 2 and 3.
        Instruction(op_code=OP_END_WITH_ERROR),
    ]
    return UtilityFile(module_id=module_id, instructions=insts)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "-o",
        "--output",
        type=Path,
        default=Path("dist/utility_demo.bin"),
        help="Output utility file path (default: dist/utility_demo.bin)",
    )
    parser.add_argument(
        "--module-id",
        type=lambda s: int(s, 0),
        default=0x62,
        help="GMLAN diag address of target ECU (default 0x62 = HUD)",
    )
    parser.add_argument(
        "--no-pti",
        action="store_true",
        help="Emit bare SPS body only (no PTI/API wrapper). "
        "Use this when feeding SPAT in DPS; SPAT auto-converts files "
        "with a PTI header and currently chokes on ours.",
    )
    args = parser.parse_args()

    uf = build_minimal_demo(module_id=args.module_id)
    blob = uf.assemble(include_pti=not args.no_pti)

    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_bytes(blob)
    print(f"Wrote {args.output} ({len(blob)} bytes)")

    # Diagnostic dump.
    if args.no_pti:
        print("  PTI header:        (omitted, --no-pti)")
    else:
        print(
            f"  PTI header:        {PTI_HEADER_SIZE} bytes, "
            f"format={PTI_FORMAT_TYPE}, dataLen={len(blob) - PTI_HEADER_SIZE}"
        )
    print(
        f"  SPS header:        {SPS_HEADER_SIZE} bytes, "
        f"interpType=GMLAN(3), moduleID=0x{args.module_id:04X}"
    )
    print(
        f"  Instructions:      {len(uf.instructions)} x {INSTRUCTION_SIZE} = "
        f"{len(uf.instructions) * INSTRUCTION_SIZE} bytes"
    )
    print(f"  Routines:          {len(uf.routines)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
