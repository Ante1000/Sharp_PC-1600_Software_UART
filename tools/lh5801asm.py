#!/usr/bin/env python3
"""Tiny LH5801 assembler: only the instructions used by asm/uart1500_sl.asm.
Opcodes are taken from the PC-1500 ROM listings and the LH5801 opcode reference.
Syntax (one instruction per line, ';' starts a comment):
    LABEL:  MNEMONIC  operand
    BCH+ / BCS+ / BCR+ / BZS+ / BZR+  label   forward branch  (offset counted from the next instruction)
    BCH- / BCS- / BCR- / BZS- / BZR-  label   backward branch
    LOP  UL,label                              loop back to label
    name = expression  (constant, may use other constants)   and  .db / .org are not needed.
Operands may be numbers (decimal, &hex, 0xhex) or constants defined with 'name = value'.
"""
import re, sys

SIMPLE = {'RIE': [0xFD, 0xBE], 'SIE': [0xFD, 0x81], 'NOP': [0x38], 'REC': [0xF9], 'SEC': [0xFB], 'RTN': [0x9A],
          'ROR': [0xD1], 'SHR': [0xD5], 'LIN X': [0x45], 'SIN X': [0x41], 'INC X': [0x44], 'INC Y': [0x54],
          'DEC X': [0x46], 'DEC Y': [0x56], 'DEC A': [0xDF], 'DEC UL': [0x62], 'DEC UH': [0xFD, 0x62],
          'STX Y': [0xFD, 0x5A], 'PSH A': [0xFD, 0xC8], 'POP A': [0xFD, 0x8A], 'INC UL': [0x60],
          'LDA XH': [0x84], 'LDA XL': [0x04], 'LDA YH': [0x94], 'LDA YL': [0x14], 'LDA UH': [0xA4], 'LDA UL': [0x24],
          'LDA (X)': [0x05], 'LDA (Y)': [0x15],
          'STA XH': [0x08], 'STA XL': [0x0A], 'STA YH': [0x18], 'STA YL': [0x1A], 'STA UH': [0x28], 'STA UL': [0x2A],
          'STA (X)': [0x0E], 'STA (Y)': [0x1E],
          'CPA UH': [0xA6], 'CPA UL': [0x26], 'CPA YH': [0x96], 'CPA YL': [0x16], 'CPA XH': [0x86], 'CPA XL': [0x06],
          'ADC UH': [0xA2], 'ADC UL': [0x22]}
IMM = {'LDI A': 0xB5, 'LDI XL': 0x4A, 'LDI XH': 0x48, 'LDI YL': 0x5A, 'LDI YH': 0x58, 'LDI UL': 0x6A, 'LDI UH': 0x68,
       'BII A': 0xBF, 'CPI A': 0xB7}
IMM2 = {'BII #(Y)': [0xFD, 0x5D], 'ANI #(Y)': [0xFD, 0x59], 'ORI #(Y)': [0xFD, 0x5B]}
ABS = {'LDA': 0xA5, 'STA': 0xAE, 'CPA': 0xA7}
BR = {'BCH': (0x8E, 0x9E), 'BCS': (0x83, 0x93), 'BCR': (0x81, 0x91), 'BZS': (0x8B, 0x9B), 'BZR': (0x89, 0x99)}


def num(tok, syms):
    """number, constant or a sum / difference of them (a+b-c)"""
    tok = tok.strip()
    parts = re.findall(r'([+-]?)\s*([&\w]+)', tok)
    total = 0
    for sign, t in parts:
        if t in syms: v = syms[t]
        elif t.startswith('&'): v = int(t[1:], 16)
        elif t.lower().startswith('0x'): v = int(t, 16)
        else: v = int(t)
        total += -v if sign == '-' else v
    return total


def assemble(src, consts=None):
    syms = dict(consts or {})
    lines = []
    for raw in src.splitlines():
        s = raw.split(';')[0].strip()
        if s: lines.append((raw, s))
    # pass 1: sizes, labels
    out = []      # (kind, data)
    addr = 0
    labels = {}
    items = []
    for raw, s in lines:
        m = re.fullmatch(r'(\w+)\s*=\s*(.+)', s)
        if m and not re.match(r'^(\w+):', s):
            if m.group(1) not in (consts or {}):      # constants given by the caller win (DB, BM for PB0 ...)
                syms[m.group(1)] = num(m.group(2), syms)
            continue
        m = re.match(r'^(\w+):\s*(.*)$', s)
        if m:
            labels[m.group(1)] = addr
            s = m.group(2).strip()
            if not s: continue
        s = re.sub(r'\s+', ' ', s)
        mn, _, op = s.partition(' ')
        mn = mn.upper(); op = op.strip()
        key = (mn + (' ' + op.upper() if op else '')).strip()
        size = None; item = None
        if key in SIMPLE:
            item = ('b', SIMPLE[key]); size = len(SIMPLE[key])
        elif mn[:3] in BR and mn[3:] in ('+', '-'):
            item = ('br', mn[:3], mn[3], op); size = 2
        elif mn == 'LOP':
            item = ('lop', op.split(',')[1].strip()); size = 2
        else:
            done = False
            m2 = re.fullmatch(r'([A-Za-z]+)\s+([A-Za-z]+|#\(Y\))\s*,\s*(.+)', s)
            if m2:
                k2 = (m2.group(1) + ' ' + m2.group(2)).upper()
                if k2 in IMM:
                    item = ('i', [IMM[k2]], m2.group(3)); size = 2
                elif k2 in IMM2:
                    item = ('i', IMM2[k2], m2.group(3)); size = 3
            if item is None:
                m3 = re.fullmatch(r'(LDA|STA|CPA)\s+\((.+)\)', s, re.I)
                if m3 and m3.group(2).upper() not in ('X', 'Y'):
                    item = ('abs', ABS[m3.group(1).upper()], m3.group(2)); size = 3
            if item is None:
                raise SystemExit('cannot assemble: ' + raw)
        items.append((addr, item, raw))
        addr += size
    # pass 2
    code = bytearray()
    listing = []
    for a, item, raw in items:
        b = None
        if item[0] == 'b': b = list(item[1])
        elif item[0] == 'i': b = list(item[1]) + [num(item[2], syms) & 0xFF]
        elif item[0] == 'abs':
            v = num(item[2], syms); b = [item[1], (v >> 8) & 0xFF, v & 0xFF]
        elif item[0] == 'br':
            _, mn, d, lab = item
            t = labels[lab.strip()]
            nxt = a + 2
            if d == '+':
                off = t - nxt
                if not 0 <= off <= 255: raise SystemExit('branch out of range fwd: ' + raw)
                b = [BR[mn][0], off]
            else:
                off = nxt - t
                if not 0 <= off <= 255: raise SystemExit('branch out of range back: ' + raw)
                b = [BR[mn][1], off]
        elif item[0] == 'lop':
            t = labels[item[1]]; off = a + 2 - t
            if not 0 <= off <= 255: raise SystemExit('lop out of range: ' + raw)
            b = [0x88, off]
        assert len(code) == a
        code += bytes(b)
        listing.append((a, b, raw.rstrip()))
    return bytes(code), labels, listing


if __name__ == '__main__':
    src = open(sys.argv[1]).read()
    c, l, lst = assemble(src)
    for a, b, raw in lst:
        print('%02X  %-10s %s' % (a, ' '.join('%02X' % x for x in b), raw))
    print(len(c), 'bytes')
