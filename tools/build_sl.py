#!/usr/bin/env python3
"""Builds Basic/uart1600_sl.txt (the BASIC installer of the Saver/Loader) from asm/uart1600_sl.asm. Needs z80asm."""
import os, re, subprocess, sys, tempfile
HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)


def assemble():
    with tempfile.TemporaryDirectory() as d:
        b = os.path.join(d, 'u.bin')
        r = subprocess.run(['z80asm', '-L', '-o', b, os.path.join(ROOT, 'asm', 'uart1600_sl.asm')], capture_output=True, text=True)
        if r.returncode:
            sys.exit(r.stderr)
        lab = {}
        for l in (r.stdout + '\n' + r.stderr).splitlines():
            m = re.match(r'(\w+):\s+equ \$([0-9a-f]+)', l)
            if m:
                lab[m.group(1)] = int(m.group(2), 16)
        return bytearray(open(b, 'rb').read()), lab


def build():
    code, lab = assemble()
    segs = [(0, lab['sv_fin']), (lab['LOAD'], lab['ld_fin'])]
    L = []

    def add(n, t):
        L.append((n, t))
    add(10, 'REM UART1600 SL V1.2')
    add(30, 'CLEAR:WAIT 0:BEEP OFF')
    add(40, 'A=0:INPUT "ADDR (ENTER=49349) ";A')
    add(50, 'IF A=0 THEN LET A=49349')
    add(60, 'IF A<49152 OR A>64000 THEN LET A=0:GOTO 40')
    add(70, 'RESTORE 1000:READ N:C=0')
    add(80, 'FOR S=1 TO N:READ O,L')
    add(90, 'FOR I=0 TO L-1:READ B:POKE A+O+I,B:C=C+B:NEXT I')
    add(100, 'NEXT S')
    add(110, 'READ X:IF X<>C THEN GOTO 900')
    add(120, 'P=PEEK(65084)+256*PEEK(65085)')
    add(130, 'Q=PEEK(65087)+256*PEEK(65088)')
    add(140, 'IF Q<P OR P<32768 THEN GOTO 910')
    add(150, 'CLS:PRINT "UART1600 SL READY"')
    add(160, 'PRINT "SAVE: CALL ";A;",X"')
    add(170, 'PRINT "LOAD: CALL ";A+%d;",X"' % lab['LOAD'])
    add(190, 'END')
    add(900, 'PRINT "DATA ERROR":BEEP 3:END')
    add(910, 'PRINT "POINTERS?":BEEP 3:END')
    add(1000, 'DATA %d' % len(segs))
    n = 1010
    tot = 0
    for a, b in segs:
        add(n, 'DATA %d,%d' % (a, b - a))
        n += 10
        vals = list(code[a:b]); tot += sum(vals)
        # pack as many numbers as fit into one line (<= 72 characters, so that the whole line is <= 80): the text is received over RS-232 and the
        # Sharp accepts only about 2 KB per LOAD, so the listing must stay short
        row = []
        for v in vals:
            if row and len('DATA ' + ','.join(row + [str(v)])) > 72:
                add(n, 'DATA ' + ','.join(row))
                n += 10
                row = []
            row.append(str(v))
        if row:
            add(n, 'DATA ' + ','.join(row))
            n += 10
    add(n, 'DATA %d' % tot)
    L.sort()
    assert len({k for k, _ in L}) == len(L)
    return ''.join('%d %s\n' % (k, t) for k, t in L), code, lab


if __name__ == '__main__':
    text, code, lab = build()
    open(os.path.join(ROOT, 'Basic', 'uart1600_sl.txt'), 'w', newline='\n').write(text)
    print('Basic/uart1600_sl.txt written;', len(text.splitlines()), 'lines, longest', max(len(l) for l in text.splitlines()),
          '; SAVE at +0, LOAD at +%d' % lab['LOAD'])
