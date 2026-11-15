#!/usr/bin/env python3
"""Builds Basic/uart1600.txt (the BASIC loader UART1600) from asm/uart1600.asm.

Needs z80asm (apt install z80asm). Usage:  python3 tools/build_loader.py
The labels of the patch sites come from the assembler, so the loader never disagrees with the code.
"""
import os, re, subprocess, sys, tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)

FX = 4          # NOPs of the pad `si_fx` (first bit of SERIN), calibrated in the simulator


def assemble():
    src = open(os.path.join(ROOT, 'asm', 'uart1600.asm')).read()
    src = re.sub(r'si_fx:\n(?:[ \t]*nop[^\n]*\n)+', 'si_fx:\n' + ' nop\n' * FX, src)
    with tempfile.TemporaryDirectory() as d:
        a = os.path.join(d, 'u.asm'); b = os.path.join(d, 'u.bin')
        open(a, 'w').write(src)
        r = subprocess.run(['z80asm', '-L', '-o', b, a], capture_output=True, text=True)
        if r.returncode:
            sys.exit(r.stderr)
        lab = {}
        for l in (r.stdout + '\n' + r.stderr).splitlines():
            m = re.match(r'(\w+):\s+equ \$([0-9a-f]+)', l)
            if m:
                lab[m.group(1)] = int(m.group(2), 16)
        return bytearray(open(b, 'rb').read()), lab


def segments(code, lab):
    """The three pieces of code (the gaps between them are not stored)."""
    return [(0, lab['so_end']), (lab['SERIN'], lab['si_fin']), (lab['PROBE'], lab['pr_fin'])]


SIG = [183, 41, 226, 7, 158, 90, 213, 64]     # signature of the PROBE search (any bytes, not 0)
SIG2 = [99, 214, 8, 171, 52, 245, 120, 33]    # second signature: used for the check after patching


# timing variables of the loader: index of TT(), meaning
# (site label, index, multiplier): the BASIC loader does POKE A+site+1, TT(index)*multiplier
def patch_table(lab):
    t = []
    def add(label, idx, mul=1):
        t.append((lab[label] + 1, idx, mul))
    add('so_dn', 1); add('so_jd', 2); add('so_pol', 7, 128)
    add('si_dn', 3); add('si_dn2', 3); add('si_dj', 4); add('si_dj2', 4)
    add('si_hn', 5); add('si_hj', 6)
    add('si_p1', 8, 4); add('si_p2', 8, 4); add('si_p3', 8, 4)
    add('si_t1m', 9); add('si_t1f', 10); add('si_t2m', 11); add('si_t2f', 12)
    return t


def data_lines(first, values, per=14):
    out = []
    for i in range(0, len(values), per):
        out.append((first + 10 * (i // per), 'DATA ' + ','.join(str(v) for v in values[i:i + per])))
    return out


def build():
    code, lab = assemble()
    segs = segments(code, lab)
    stream = []
    for a, b in segs:
        stream += list(code[a:b])
    chk = sum(stream)
    pt = patch_table(lab)
    L = []

    def add(n, t):
        L.append((n, t))
    TXP = lab['so_tp'] + 1
    RXP = lab['si_rp'] + 1
    sites = [lab['pr_s%d' % i] + 1 for i in range(8)]
    add(10, 'REM UART1600 V1.0: SOFTWARE UART FOR SHARP PC-1600 (MODE 0), 19200 BPS 8N1')
    add(20, 'REM TX=PC7 (CN9 PIN 29), RX=PB2 (CN9 PIN 27), CMOS LEVELS, GND = PINS 52-54')
    add(30, 'REM CODE+BUFFERS = 767 BYTES FROM A (DEFAULT 63744=&F900, RAM; 197 IS ROM!)')
    add(40, 'CLEAR:DIM TT(12):WAIT 0')
    add(100, 'REM --- TIMING VARIABLES, 19200 BPS (CPU CLOCK 3.58 MHZ, T-STATES) ---')
    add(110, 'TT(1)=6:TT(2)=2:REM TX BIT = 100+16*TT(1)-4*TT(2) T-STATES (TT(2)=0 TO 8)')
    add(120, 'TT(3)=6:TT(4)=2:REM RX BIT = 99+16*TT(3)-4*TT(4) T-STATES (TT(4)=0 TO 8)')
    add(130, 'TT(5)=1:TT(6)=8:REM START BIT CHECK = 83+16*TT(5)-4*TT(6) T-STATES')
    add(140, 'TT(7)=0:TT(8)=0:REM POLARITY TX, RX: 0=NORMAL (IDLE HIGH), 1=INVERTED')
    add(150, 'TT(9)=0:TT(10)=36:REM WAIT FOR THE FIRST BYTE = 45*256*256*36 T = 30 S')
    add(160, 'TT(11)=154:TT(12)=1:REM END OF PACKET = 45*256*154*1 T = 0.5 S')
    add(200, 'A=0:INPUT "ML ADDRESS (ENTER=63744) ";A:IF A=0 THEN LET A=63744')
    add(210, 'REM --- STORE THE CODE (SEROUT, SERIN, SEARCH ROUTINE PROBE AT A+512) ---')
    add(220, 'RESTORE 1000:READ N:C=0')
    add(230, 'FOR S=1 TO N:READ O,L')
    add(240, 'FOR I=0 TO L-1:READ B:POKE A+O+I,B:C=C+B:NEXT I')
    add(250, 'NEXT S')
    add(260, 'READ X:IF X<>C THEN GOTO 960')
    add(300, 'REM --- Z-80 ADDRESS P OF A: PROBE LOOKS FOR A SIGNATURE IN THE RAM ---')
    add(310, 'RESTORE 920:GOSUB 600:P=Z:IF P=0 THEN GOTO 390')
    add(320, 'RESTORE 930:GOSUB 600:IF Z<>P THEN GOTO 970')
    add(330, 'GOTO 420')
    add(390, 'PRINT "Z-80 ADDRESS NOT FOUND":INPUT "Z-80 ADDRESS OF ML AREA? ";P')
    add(420, 'REM --- BUFFER POINTERS: TXBUF = P+256, RXBUF = P+512 ---')
    add(430, 'W=P+256:W=W-65536*INT(W/65536):H=A+%d:GOSUB 700' % TXP)
    add(440, 'W=P+512:W=W-65536*INT(W/65536):H=A+%d:GOSUB 700' % RXP)
    add(450, 'REM --- TIMING, POLARITY BYTES: TABLE AT 900 (OFFSET, TT INDEX, FACTOR) ---')
    add(460, 'RESTORE 900:READ N:FOR S=1 TO N:READ O,K,L:POKE A+O,TT(K)*L:NEXT S')
    add(490, 'POKE A+240,85,P-256*INT(P/256),INT(P/256):REM DESCRIPTION: "U", Z-80 ADDRESS')
    add(500, 'CLS:PRINT "UART1600 READY, 19200 BPS"')
    add(510, 'PRINT "SEROUT: CALL ";A;",N"')
    add(520, 'PRINT "SERIN: CALL ";A+%d;",N"' % lab['SERIN'])
    add(530, 'PRINT "TX BUF ";A+256;" RX BUF ";A+512')
    add(540, 'END')
    add(600, 'REM SUB: READ SIGNATURE (8 PAIRS VALUE,OFFSET; 2 SITES), SEARCH. RESULT Z')
    add(610, 'Q=A+512:Z=0')
    add(620, 'FOR J=0 TO 7:READ B,O:POKE A+256+J,B:POKE A+O,B:NEXT J')
    add(630, 'READ U,V:POKE A+V,64')
    add(640, 'FOR G=192 TO 128 STEP -64')
    add(650, 'IF Z<>0 THEN GOTO 680')
    add(660, 'POKE A+U,G:X=0:CALL Q,X:IF X<0 THEN LET X=X+65536')
    add(670, 'Z=X')
    add(680, 'NEXT G')
    add(690, 'POKE A+256,0,0,0,0,0,0,0,0:RETURN')
    add(700, 'POKE H,W-256*INT(W/256),INT(W/256):RETURN')
    add(900, 'DATA %d' % len(pt))
    row = ['%d,%d,%d' % (o, i, m) for o, i, m in pt]
    for k in range(0, len(row), 4):
        add(901 + k // 4, 'DATA ' + ','.join(row[k:k + 4]))
    for n, sig in ((920, SIG), (930, SIG2)):
        add(n, 'DATA ' + ','.join('%d,%d' % (v, o) for v, o in zip(sig[:4], sites[:4])))
        add(n + 1, 'DATA ' + ','.join('%d,%d' % (v, o) for v, o in zip(sig[4:], sites[4:])))
        add(n + 2, 'DATA %d,%d' % (lab['pr_sh'] + 1, lab['pr_np'] + 1))
    add(960, 'PRINT "DATA ERROR: CHECK THE LISTING":BEEP 3:END')
    add(970, 'PRINT "Z-80 ADDRESS UNSURE":PRINT "NEW 1000 DONE?":BEEP 3:END')
    add(1000, 'DATA %d,%s' % (len(segs), ','.join('%d,%d' % (a, b - a) for a, b in segs)))
    add(1005, 'REM THE CODE BYTES (SEROUT, SERIN, PROBE), THEN THEIR SUM')
    dl = data_lines(1010, stream)
    L += dl
    add(dl[-1][0] + 10, 'DATA %d' % chk)
    L.sort()
    nums = [n for n, _ in L]
    assert len(set(nums)) == len(nums), 'duplicate line numbers'
    text = ''.join('%d %s\n' % (n, t) for n, t in L)
    return text, code, lab, segs, pt, TXP, RXP


if __name__ == '__main__':
    text, code, lab, segs, pt, TXP, RXP = build()
    open(os.path.join(ROOT, 'Basic', 'uart1600.txt'), 'w', newline='\n').write(text)
    print('Basic/uart1600.txt written;', len(text), 'bytes; code', len(code), 'bytes; segments', segs)
    print('longest line', max(len(l) for l in text.splitlines()))
