#!/usr/bin/env python3
"""Builds Basic/sl1500.txt (the BASIC installer of the PC-1500 Saver/Loader) from asm/uart1500_sl.asm.
The code is assembled with tools/lh5801asm.py. The installer POKEs it from DATA lines (RX port PB2 by default) and then patches
the 5 bytes that depend on the RX port (PB0 / PB2)."""
import os, sys
HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
sys.path.insert(0, HERE)
import lh5801asm as A

LOAD_OFF = 0xC5            # LOAD at RAM start + &C5 (&40C5 on a PC-1500A); SAVE follows it directly (RAM start + &184)
NEW_OFF = 0x200            # BASIC must start at RAM start + &200 or later (NEW &4200)


def assemble():
    src = open(os.path.join(ROOT, 'asm', 'uart1500_sl.asm')).read()
    code, lab, lst = A.assemble(src)                          # PB2
    c0, _, _ = A.assemble(src, {'DB': 0xFB, 'BM': 4})
    c1, _, _ = A.assemble(src, {'DB': 0xFE, 'BM': 1})        # PB0
    c2, _, _ = A.assemble(src, {'DB': 0x12, 'BM': 4})
    db_pos = [i for i in range(len(c0)) if c0[i] != c2[i]]
    bm_pos = [i for i in range(len(c0)) if c0[i] != c1[i] and i not in db_pos]
    return code, lab, db_pos, bm_pos


def build():
    code, lab, db_pos, bm_pos = assemble()
    n = len(code)
    assert LOAD_OFF + n <= NEW_OFF, 'code too long for NEW &4200: %d' % n
    L = []

    def add(num, text):
        L.append((num, text))
    add(10, 'REM SL1500 4800 BPS')
    add(20, 'A=(PEEK &7863)*256:L=A+%d:S=L+%d' % (LOAD_OFF, lab['SAVE']))
    add(30, 'B=(PEEK &7865)*256+PEEK &7866')
    add(40, 'IF B<A+%d PRINT "NEW &4200 IN PRO MODE!":BEEP 3:END' % NEW_OFF)
    add(50, 'P=2:INPUT "RX PORT (DEFAULT PB2) ";P')
    add(60, 'M=0:IF P=0 LET M=1')
    add(70, 'IF P=2 LET M=4')
    add(80, 'IF M=0 BEEP 1:GOTO 50')
    add(90, 'RESTORE 1000:C=0')
    add(100, 'FOR I=0 TO %d:READ V:POKE L+I,V:C=C+V:NEXT I' % (n - 1))
    add(110, 'READ V:IF V<>C PRINT "DATA ERROR":BEEP 3:END')
    add(120, 'POKE L+%d,255-M' % db_pos[0])
    pk = ':'.join('POKE L+%d,M' % p for p in bm_pos)
    add(130, pk)
    add(140, 'CLS:WAIT 100:PRINT "SL1500 READY 4800 BPS"')
    add(150, 'PRINT "LOAD CALL ";L;" NEW 1ST"')
    add(160, 'PRINT "SAVE CALL ";S')
    add(170, 'WAIT 0:END')
    nl = 1000
    row = []
    for v in code:
        if row and len('DATA ' + ','.join(row + [str(v)])) > 72:
            add(nl, 'DATA ' + ','.join(row)); nl += 10; row = []
        row.append(str(v))
    row.append(str(sum(code)))
    add(nl, 'DATA ' + ','.join(row))
    return L, code, lab, db_pos, bm_pos


def main():
    L, code, lab, db_pos, bm_pos = build()
    text = ''.join('%d %s\n' % (n, t) for n, t in L)
    open(os.path.join(ROOT, 'Basic', 'sl1500.txt'), 'w').write(text)
    print('code %d bytes: LOAD %d, SAVE %d; DB at %s, BM at %s; %d lines, %d characters' % (
        len(code), lab['SAVE'], len(code) - lab['SAVE'], db_pos, bm_pos, len(L), len(text)))
    print('longest line: %d' % max(len('%d %s' % (n, t)) for n, t in L))


if __name__ == '__main__':
    main()
