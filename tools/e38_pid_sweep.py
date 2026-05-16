"""
Mode 22 PID sweep for E38 PCM bin 12647991.

Each handler is a short Book-E PowerPC routine. The compiler-generated form is:

    stwu r1, -0x10(r1)              ; prologue
    mflr r0
    stw  r31, 0xc(r1)
    stw  r0, 0x14(r1)
    addi r31, r3, 0                 ; r31 = ptr to caller's response buffer

    [optional: lbz rX, off(r13)     ; check a flag through SDA]
    [optional: cmpwi/bc ...]

    lXX r3, off(r13)                ; >>> load the RAW raw VALUE from RAM via SDA <<<
    addi r4, r0, K                  ; multiplier / arg to formatter
    [addi r5, r0, K2]               ; optional second arg
    bl   <formatter>                ; scale raw RAM value -> wire bytes

    stb/sth/stw r3, 0(r31)          ; write to response buffer
    ... epilogue ...
    blr

SDA base is r13 = 0x00400000 (recovered empirically: gives 99.0% A2L hit rate
across 1667 r13-relative load offsets in the 701 data-fetch handlers).

For each handler we:
  1. Pick the LAST r13-relative load whose destination is r3 (or r4) before
     the final bl. That is the raw RAM variable being fetched.
  2. Add SDA base to recover the absolute RAM address.
  3. Cross-reference against the A2L MEASUREMENT index.
  4. Also record the formatter `bl` target (the wire-scaling helper).

Output CSV: PID, handler, ram_addr, a2l_name, type, conv, A2L scale/offset/units,
description, formatter_addr, formatter_arg_r4.
"""

import json, struct, os, sys, csv
from collections import Counter

BIN = r'C:\Users\Nathan\OneDrive\Cars\VF2 HSV R8 LSA\GM Programming\PCMHacking\ECM\From Smokeshow\12647991.bin'
ADDR_JSON = r'C:\Users\Nathan\OneDrive\ECA\Resources\Visual Studio\GM ECU Simulator\.tmp_a2l_addr.json'
COMPU_JSON = r'C:\Users\Nathan\OneDrive\ECA\Resources\Visual Studio\GM ECU Simulator\.tmp_a2l_compu.json'
OUT_CSV = r'C:\Users\Nathan\OneDrive\ECA\Resources\Visual Studio\GM ECU Simulator\e38_12647991_mode22_pids.csv'

SDA_BASE = 0x00400000
RAM_RANGES = [(0x3F8000, 0x400000), (0x418000, 0x41A000), (0x400000, 0x418000)]
FLASH_END = 0x200000

def in_ram(a):
    # RAM proper, or DATA_FLASH calibration region (used by handlers that
    # read fixed calibration constants instead of live values).
    if any(lo <= a < hi for (lo, hi) in RAM_RANGES) or 0x3F8000 <= a < 0x420000:
        return True
    if 0x1C0000 <= a < 0x1FFFF8:  # DATA_FLASH_2 / CALIBRATION_VARIABLES_FLASH_3
        return True
    return False

def sext(v, bits):
    m = 1 << (bits - 1)
    return (v ^ m) - m

with open(BIN, 'rb') as f:
    BIN_BYTES = f.read()

def word(off):
    if off + 4 > len(BIN_BYTES) or off < 0:
        return None
    return struct.unpack('>I', BIN_BYTES[off:off+4])[0]

# Decode just the opcodes we need
def decode(w, pc):
    op = (w >> 26) & 0x3F
    rD = (w >> 21) & 0x1F
    rA = (w >> 16) & 0x1F
    d = sext(w & 0xFFFF, 16)
    # GPR loads
    if op in (32, 33, 34, 35, 40, 41, 42, 43):
        sizes = {32: 4, 33: 4, 34: 1, 35: 1, 40: 2, 41: 2, 42: 2, 43: 2}
        signed = {32: False, 33: False, 34: False, 35: False, 40: False, 41: False, 42: True, 43: True}
        names = {32: 'lwz', 33: 'lwzu', 34: 'lbz', 35: 'lbzu', 40: 'lhz', 41: 'lhzu', 42: 'lha', 43: 'lhau'}
        return ('load', rD, rA, d, sizes[op], signed[op], names[op], 'gpr')
    # FP loads: lfs=48, lfsu=49, lfd=50, lfdu=51
    if op in (48, 49, 50, 51):
        sizes = {48: 4, 49: 4, 50: 8, 51: 8}
        names = {48: 'lfs', 49: 'lfsu', 50: 'lfd', 51: 'lfdu'}
        # rD here is FPR number, not GPR; but the data load is from RAM at rA+d
        return ('load', rD, rA, d, sizes[op], True, names[op], 'fpr')
    if op == 18:
        li = w & 0x03FFFFFC
        aa = (w >> 1) & 1
        lk = w & 1
        off = sext(li, 26)
        target = (off if aa else (pc + off)) & 0xFFFFFFFF
        return ('branch', target, lk, aa)
    if op == 19 and ((w >> 1) & 0x3FF) == 16:
        return ('blr',)
    if op == 14:
        return ('addi', rD, rA, d)
    if op == 15:
        return ('addis', rD, rA, d)
    if op == 24:
        return ('ori', rA, rD, w & 0xFFFF)  # rD is rS in encoding
    if op == 25:
        return ('oris', rA, rD, w & 0xFFFF)
    if op == 31:
        xo = (w >> 1) & 0x3FF
        rB = (w >> 11) & 0x1F
        if xo == 444:  # or rA, rS, rB (mr if rS==rB)
            return ('or', rA, rD, rB)
    return ('?',)

def analyze_handler(handler_pc, max_instrs=80, _depth=0):
    """
    Walk the handler. Track all 32 GPRs that have known immediate values
    (from lis/addi/addis/ori/oris/mr). Record every load whose effective
    address lands in RAM (regardless of which base register).

    Returns dict with the pick of best load + the formatter helper info.
    """
    if _depth > 1:
        return None
    regs = [None] * 32  # known immediate value or None
    # Convention: r13 is always SDA_BASE on entry.
    regs[13] = SDA_BASE
    loads = []        # list of (idx, dst_kind, dst_reg, ram_addr, size, signed, name)
    r4_const = None
    bl_targets = []   # list of (idx, target, r4_const)
    pc = handler_pc
    disasm = []
    seen = set()
    last_bl_idx = -1

    for i in range(max_instrs):
        if pc in seen: break
        seen.add(pc)
        w = word(pc)
        if w is None: break
        ins = decode(w, pc)
        kind = ins[0]
        if kind == 'load':
            _, rD, rA, d, size, signed, name, dst_kind = ins
            base = 0 if rA == 0 else regs[rA]
            if base is not None:
                addr = (base + d) & 0xFFFFFFFF
                if in_ram(addr):
                    loads.append((i, dst_kind, rD, addr, size, signed, name))
            # Destination GPR (if any) now holds an unknown value.
            if dst_kind == 'gpr':
                regs[rD] = None
            disasm.append((pc, f'{name} r{rD},{d:#x}(r{rA})'))
        elif kind == 'addi':
            _, rD, rA, simm = ins
            if rA == 0:
                regs[rD] = simm & 0xFFFFFFFF
            elif regs[rA] is not None:
                regs[rD] = (regs[rA] + simm) & 0xFFFFFFFF
            else:
                regs[rD] = None
            if rA == 0 and rD == 4:
                r4_const = simm & 0xFFFF
            disasm.append((pc, f'addi r{rD},r{rA},{simm:#x}'))
        elif kind == 'addis':
            _, rD, rA, simm = ins
            v = (simm << 16) & 0xFFFFFFFF
            if rA == 0:
                regs[rD] = v
            elif regs[rA] is not None:
                regs[rD] = (regs[rA] + v) & 0xFFFFFFFF
            else:
                regs[rD] = None
            disasm.append((pc, f'addis r{rD},r{rA},{simm:#x}'))
        elif kind == 'ori':
            _, rA, rS, uimm = ins
            if regs[rS] is not None:
                regs[rA] = (regs[rS] | uimm) & 0xFFFFFFFF
            else:
                regs[rA] = None
            disasm.append((pc, f'ori r{rA},r{rS},{uimm:#x}'))
        elif kind == 'oris':
            _, rA, rS, uimm = ins
            if regs[rS] is not None:
                regs[rA] = (regs[rS] | (uimm << 16)) & 0xFFFFFFFF
            else:
                regs[rA] = None
            disasm.append((pc, f'oris r{rA},r{rS},{uimm:#x}'))
        elif kind == 'or':
            _, rA, rS, rB = ins
            if rS == rB:
                regs[rA] = regs[rS]
            else:
                regs[rA] = None
            disasm.append((pc, f'or r{rA},r{rS},r{rB}'))
        elif kind == 'branch':
            _, target, lk, aa = ins
            if lk:
                bl_targets.append((i, target, r4_const))
                last_bl_idx = i
                disasm.append((pc, f'bl 0x{target:06X}'))
                # r3..r12 caller-saved per PPC EABI: their immediate state is gone after the call.
                for k in range(3, 13):
                    regs[k] = None
                r4_const = None
            else:
                disasm.append((pc, f'b 0x{target:06X}'))
                # leave seen-guard to break tail-call loops
        elif kind == 'blr':
            disasm.append((pc, 'blr'))
            break
        else:
            disasm.append((pc, '?'))
        pc += 4

    # Pick the load that fed the scaling call. Heuristic order:
    #   1. last GPR load into r3 BEFORE last bl
    #   2. last GPR load into r4 BEFORE last bl
    #   3. last FPR load BEFORE last bl
    #   4. last GPR load into r3 anywhere
    #   5. any RAM load
    pick = None
    def pick_match(filter_fn):
        for ld in reversed(loads):
            if filter_fn(ld):
                return ld
        return None
    if last_bl_idx >= 0:
        pick = pick_match(lambda ld: ld[0] < last_bl_idx and ld[1] == 'gpr' and ld[2] == 3)
        if pick is None:
            pick = pick_match(lambda ld: ld[0] < last_bl_idx and ld[1] == 'gpr' and ld[2] == 4)
        if pick is None:
            pick = pick_match(lambda ld: ld[0] < last_bl_idx and ld[1] == 'fpr')
    if pick is None:
        pick = pick_match(lambda ld: ld[1] == 'gpr' and ld[2] == 3)
    if pick is None and loads:
        pick = loads[-1]

    formatter = bl_targets[-1][1] if bl_targets else None
    formatter_arg = bl_targets[-1][2] if bl_targets else None

    result = {
        'load': pick,
        'all_loads': loads,
        'formatter': formatter,
        'formatter_arg_r4': formatter_arg,
        'disasm': disasm,
        'bl_targets': bl_targets,
    }

    # If no load was found AND the handler is essentially "call X and store result",
    # recurse into the first bl.
    if pick is None and bl_targets and _depth == 0:
        sub = analyze_handler(bl_targets[0][1], max_instrs=80, _depth=_depth + 1)
        if sub and sub['load'] is not None:
            result['load'] = sub['load']
            # Keep the original handler's formatter info though.
            if result['formatter'] is None:
                result['formatter'] = sub['formatter']
                result['formatter_arg_r4'] = sub['formatter_arg_r4']
    return result

def main():
    # Re-walk PID table
    pid_table = []
    o = 0x1289DA
    prev = -1
    while o + 8 <= len(BIN_BYTES):
        pid, addr, length, flags = struct.unpack('>HIBB', BIN_BYTES[o:o+8])
        if pid <= prev or pid >= 0xFFFE or length > 64: break
        if addr != 0 and not (addr < 0x200000 or 0x3F8000 <= addr < 0x420000): break
        pid_table.append((pid, addr, length, flags))
        prev = pid
        o += 8

    with open(ADDR_JSON) as f:
        addr_idx = {int(k, 16): v for k, v in json.load(f).items()}
    with open(COMPU_JSON) as f:
        compu = json.load(f)

    rows = []
    formatter_use = Counter()
    n_a2l = n_load = n_cmd = n_noload = n_loadbutnoa2l = 0

    for pid, handler, length, flags in pid_table:
        row = {
            'pid': f'${pid:04X}',
            'handler': f'0x{handler:06X}' if handler else '-',
            'resp_len': length,
            'flags': f'0x{flags:02X}',
            'ram_addr': '', 'load_size': '', 'load_signed': '',
            'a2l_name': '', 'a2l_type': '', 'conv': '',
            'conv_kind': '', 'scale': '', 'offset': '', 'unit': '',
            'a': '', 'b': '', 'c': '', 'd': '', 'e': '', 'f': '',
            'desc': '',
            'formatter': '', 'formatter_arg_r4_hex': '',
            'note': '',
        }
        if handler == 0:
            row['note'] = 'command/control PID (handler=0)'
            n_cmd += 1; rows.append(row); continue

        info = analyze_handler(handler)
        if info['load'] is None:
            row['note'] = 'no r13-relative load found'
            n_noload += 1; rows.append(row); continue

        _, dst_kind, rD, ram, size, signed, name = info['load']
        row['ram_addr'] = f'0x{ram:06X}'
        row['load_size'] = size
        row['load_signed'] = signed
        reg_label = f'fp{rD}' if dst_kind == 'fpr' else f'r{rD}'
        row['note'] = f'load into {reg_label} ({name})'
        n_load += 1

        if info['formatter'] is not None:
            row['formatter'] = f'0x{info["formatter"]:06X}'
            formatter_use[info['formatter']] += 1
            if info['formatter_arg_r4'] is not None:
                row['formatter_arg_r4_hex'] = f'0x{info["formatter_arg_r4"]:04X}'

        entries = addr_idx.get(ram, [])
        if not entries:
            # Member of a struct/array? try a few bytes below
            for delta in (1, 2, 3, 4, 5, 6, 7, 8):
                e = addr_idx.get(ram - delta, [])
                if e:
                    entries = e
                    row['note'] += f' (A2L matched {delta}B below)'
                    break
        if not entries:
            n_loadbutnoa2l += 1
            rows.append(row); continue

        e = entries[0]
        row['a2l_name'] = e['name']
        row['a2l_type'] = e['type']
        row['conv'] = e['conv']
        row['desc'] = e['desc']
        cm = compu.get(e['conv'])
        if cm:
            row['conv_kind'] = cm.get('kind', '')
            row['unit'] = cm.get('unit', '')
            coefs = cm.get('coefs') or []
            for k, name in enumerate(('a','b','c','d','e','f')):
                if k < len(coefs):
                    row[name] = coefs[k]
            pairs = cm.get('tab_first_pairs') or []
            if len(pairs) >= 2:
                (r0, p0), (r1, p1) = pairs[0], pairs[1]
                if r1 != r0:
                    slope = (p1 - p0) / (r1 - r0)
                    intercept = p0 - slope * r0
                    # Pretty-print as fraction if obvious
                    row['scale'] = f'{slope:.10g}'
                    row['offset'] = f'{intercept:.10g}'
            if not row['scale'] and coefs and cm.get('kind') == 'RAT_FUNC':
                # RAT_FUNC: phys = (a*raw + b) / (d*raw + e). Most GM entries are simple b/e.
                a, b, c, d_, e_, f_ = coefs
                if a == 0 and d_ == 0 and e_ != 0:
                    row['scale'] = f'{b/e_:.10g}'
                    row['offset'] = f'{c/e_:.10g}' if c else '0'
        n_a2l += 1
        rows.append(row)

    print(f'PIDs total:          {len(pid_table)}')
    print(f'  command PIDs:      {n_cmd}')
    print(f'  no load resolved:  {n_noload}')
    print(f'  load found:        {n_load}')
    print(f'    no A2L match:    {n_loadbutnoa2l}')
    print(f'    A2L matched:     {n_a2l}')
    print()
    print('Top formatter helpers (wire-scaling routines):')
    for tgt, n in formatter_use.most_common(10):
        print(f'  0x{tgt:06X}  used by {n} PIDs')

    with open(OUT_CSV, 'w', newline='', encoding='utf-8') as f:
        cols = ['pid','handler','resp_len','flags','ram_addr','load_size','load_signed',
                'a2l_name','a2l_type','conv','conv_kind','scale','offset','unit',
                'a','b','c','d','e','f','desc',
                'formatter','formatter_arg_r4_hex','note']
        w = csv.DictWriter(f, fieldnames=cols)
        w.writeheader()
        for row in rows:
            w.writerow(row)
    print(f'\nWrote {OUT_CSV}')

if __name__ == '__main__':
    main()
