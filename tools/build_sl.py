#!/usr/bin/env python3
"""Builds Basic/sl.txt (the BASIC installer of the Saver/Loader) from asm/uart1600_sl.asm. Needs z80asm."""
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
    # fixed places: LOAD (receive into the PC-1600) at &C100 = 49408, SAVE (send from the PC-1600) at &C1C0 = 49600.
    # The code is position independent; the offsets are relative to 49408. LOAD (191 bytes) must end before SAVE.
    LOAD_AT, SAVE_AT = 0xC100, 0xC1C0
    assert lab['ld_fin'] - lab['LOAD'] <= SAVE_AT - LOAD_AT
    segs = [(lab['LOAD'], lab['ld_fin'], 0), (0, lab['sv_fin'], SAVE_AT - LOAD_AT)]    # (source from, to, offset in the PC-1600)
    L = []

    def add(n, t):
        L.append((n, t))
    add(10, 'REM SL V2.0: LOAD &C100, SAVE &C1C0')
    add(30, 'CLEAR:WAIT 0:BEEP OFF:A=%d' % LOAD_AT)
    add(40, 'IF PEEK(63589)+256*PEEK(63590)<A+%d THEN PRINT "NO ROOM":BEEP 3:END' % (SAVE_AT - LOAD_AT + (segs[1][1] - segs[1][0])))
    add(70, 'RESTORE 1000:READ N:C=0')
    add(80, 'FOR S=1 TO N:READ O,L')
    add(90, 'FOR I=0 TO L-1:READ B:POKE A+O+I,B:C=C+B:NEXT I')
    add(100, 'NEXT S')
    add(110, 'READ X:IF X<>C THEN GOTO 900')
    add(120, 'P=PEEK(65084)+256*PEEK(65085)')
    add(130, 'Q=PEEK(65087)+256*PEEK(65088)')
    add(140, 'IF Q<P OR P<32768 THEN GOTO 910')
    add(150, 'CLS:PRINT "SL READY"')
    add(160, 'PRINT "LOAD (to PC-1600): CALL &C100"')
    add(170, 'PRINT "SAVE (from PC-1600): CALL &C1C0"')
    add(190, 'END')
    add(900, 'PRINT "DATA ERROR":BEEP 3:END')
    add(910, 'PRINT "POINTERS?":BEEP 3:END')
    add(1000, 'DATA %d' % len(segs))
    n = 1010
    tot = 0
    for a, b, off in segs:
        add(n, 'DATA %d,%d' % (off, b - a))
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
    open(os.path.join(ROOT, 'Basic', 'sl.txt'), 'w', newline='\n').write(text)
    print('Basic/sl.txt written;', len(text.splitlines()), 'lines,', len(text), 'bytes, longest', max(len(l) for l in text.splitlines()),
          '; LOAD at &C100, SAVE at &C1C0')
