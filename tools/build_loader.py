#!/usr/bin/env python3
"""Builds Basic/uart1600.txt (the BASIC loader UART1600) from asm/uart1600.asm.

Needs z80asm (apt install z80asm). Usage:  python3 tools/build_loader.py

The loader contains two variants of SEROUT/SERIN made from the same source:
  F (fast):  bit time = BASE + 16*N - 4*D T-states (N = 1...255), used for 750 ... 38400 bps
  S (slow):  every delay is repeated M times (EXX loop), used for 300 ... 749 bps
and the shared search routine PROBE. At 38400 bps four more bytes pairs turn the delay loops of F into short jumps.
The labels of the patch sites come from the assembler, so the loader never disagrees with the code.
"""
import os, re, subprocess, sys, tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)

FX = 4          # NOPs of the pad `si_fx` (first bit of SERIN)
TURBO_J = 4     # NOPs executed in the turbo bit (4 = 82 T-states, adjusted on a real PC-1600 at 38400 bps)


def assemble(slow=False):
    src = open(os.path.join(ROOT, 'asm', 'uart1600.asm')).read()
    src = re.sub(r'si_fx:\n(?:[ \t]*nop[^\n]*\n)+', 'si_fx:\n' + ' nop\n' * FX, src)
    if slow:
        # every delay loop is run M times: EXX, LD B,M, (LD D,N / DEC D / JR NZ) , DJNZ, EXX  (the carry is kept)
        for X, L in (('so_dn', 'so_dl'), ('si_hn', 'si_hl'), ('si_dn', 'si_dd'), ('si_dn2', 'si_dd2')):
            pat = r'%s:\n ld d,(\d+)([^\n]*)\n%s:\n dec d\n jr nz,%s\n' % (X, L, L)
            new = '%sm:\n exx\n ld b,1\n%s:\n ld d,\\1\\2\n%s:\n dec d\n jr nz,%s\n djnz %s\n exx\n' % (X, X, L, L, X)
            src, n = re.subn(pat, new, src)
            assert n == 1, X
        # the alternate registers BC' and DE' are used by the EXX loops: save them on entry, restore them on exit
        for a, b in ((' push de\n push hl\n', ' push de\n push hl\n exx\n push bc\n push de\n exx\n'),
                     (' ei\n pop hl\n pop de\n pop bc\n pop af\n or a', ' ei\n exx\n pop de\n pop bc\n exx\n pop hl\n pop de\n pop bc\n pop af\n or a'),
                     ('SERIN:\n push af\n push bc\n push hl\n', 'SERIN:\n push af\n push bc\n push hl\n exx\n push bc\n push de\n exx\n'),
                     (' ei\n pop hl\n pop bc\n pop af\n scf', ' ei\n exx\n pop de\n pop bc\n exx\n pop hl\n pop bc\n pop af\n scf')):
            assert src.count(a) == 1, a
            src = src.replace(a, b)
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


SIG = [183, 41, 226, 7, 158, 90, 213, 64]     # signature of the PROBE search (any bytes, not 0)
SIG2 = [99, 214, 8, 171, 52, 245, 120, 33]    # second signature: used for the check after patching


def seg_main(lab):
    return [(0, lab['so_end']), (lab['SERIN'], lab['si_fin'])]


def seg_probe(lab):
    return [(lab['PROBE'], lab['pr_fin'])]


def patch_table(lab, slow):
    """(offset, index of TT(), factor): the loader does POKE A+offset, TT(index)*factor"""
    t = []

    def add(label, idx, mul=1, off=1):
        t.append((lab[label] + off, idx, mul))
    add('so_dn', 1); add('so_jd', 2); add('so_pol', 7, 128)
    add('si_dn', 3); add('si_dn2', 3); add('si_dj', 4); add('si_dj2', 4)
    add('si_hn', 5); add('si_hj', 6)
    add('si_p1', 8, 4); add('si_p2', 8, 4); add('si_p3', 8, 4)
    add('si_t1m', 9); add('si_t1f', 10); add('si_t2m', 11); add('si_t2f', 12)
    if slow:
        for l in ('so_dnm', 'si_hnm', 'si_dnm', 'si_dn2m'):
            add(l, 13, 1, 2)             # LD B,M follows the EXX
    add('so_rs', 21, 211); add('so_rs', 21, 24)    # restore OPC after SEROUT: OUT (18H),A or two NOPs
    add('so_tp', 14); add('so_tp', 15, 1, 2)      # TXPTR low, high (TT(14), TT(15))
    add('si_rp', 16); add('si_rp', 17, 1, 2)      # RXPTR low, high (TT(16), TT(17))
    return t


def turbo(lab):
    """(offset, displacement): JR instructions that replace LD D,N in the fast variant (38400 bps)"""
    j = TURBO_J
    return [(lab['so_dn'], (lab['so_pad'] - j) - (lab['so_dn'] + 2)),
            (lab['si_dn'], (lab['si_dp'] - j) - (lab['si_dn'] + 2)),
            (lab['si_dn2'], lab['si_dp2'] - (lab['si_dn2'] + 2)),
            (lab['si_st'], (lab['si_fx'] - 2) - (lab['si_st'] + 2))]


def data_lines(first, values, per=14):
    out = []
    for i in range(0, len(values), per):
        out.append((first + 10 * (i // per), 'DATA ' + ','.join(str(v) for v in values[i:i + per])))
    return out


def build():
    codeF, labF = assemble(False)
    codeS, labS = assemble(True)
    assert labF['PROBE'] == labS['PROBE'] and codeF[labF['PROBE']:labF['pr_fin']] == codeS[labS['PROBE']:labS['pr_fin']]
    assert labS['so_end'] <= labS['SERIN'] and labS['si_fin'] <= 0xFA, 'slow SERIN reaches the description at +250'
    L = []

    def add(n, t):
        L.append((n, t))
    add(10, 'REM UART1600 V2.6: SOFTWARE UART PC-1600 (MODE 0), 300-38400 BPS, WITH TEST')
    add(20, 'REM TX=PC7 (CN9 PIN 29), RX=PB2 (CN9 PIN 27), CMOS LEVELS, GND = PINS 52-54')
    add(30, 'REM CODE+BUFFERS = 767 BYTES FROM A (DEFAULT 49349=&C0C5: ML AREA OF RAM)')
    add(40, 'CLEAR:DIM TT(21):DIM Z$(0)*26:WAIT 0:BEEP OFF')
    add(100, 'REM --- SPEED (BD) ---')
    add(104, 'PRINT "BAUD 300-38400"')
    add(105, 'BD=0:INPUT "ENTER=19200 ";BD')
    add(106, 'IF BD=0 THEN LET BD=19200')
    add(107, 'IF BD<300 OR BD>38400 THEN GOTO 105')
    add(108, 'IF BD>28800 AND BD<38400 THEN GOTO 105')
    add(110, 'REM --- POLARITY (0 NORMAL, 1 INVERTED) AND TIMEOUTS; CPU ABOUT 3.0 MHZ ---')
    add(111, 'TT(7)=0:TT(8)=0:REM POLARITY OF TX AND RX')
    add(112, 'TT(9)=0:TT(10)=30:REM FIRST BYTE: 45*256*256*TT(10) T = 30 S')
    add(113, 'TT(11)=130:TT(12)=1:REM END OF PACKET: 45*256*TT(11)*TT(12) T = 0.5 S')
    add(114, 'TT(13)=1:REM REPEAT COUNT M OF THE DELAYS (SLOW CODE, UNDER 750 BPS)')
    add(115, 'TT(21)=1:REM 1 = SEROUT PUTS OPC (BUZZER, TX PIN) BACK AFTER SENDING')
    add(120, 'B=3000000/BD:SL=0:TU=0:IF BD<750 THEN LET SL=1')
    add(121, 'IF BD=38400 THEN LET TU=1')
    add(122, 'IF TU=1 THEN LET TT(1)=1:TT(2)=8:TT(3)=1:TT(4)=8:TT(5)=1:TT(6)=8')
    add(123, 'IF TU=1 THEN GOTO 199')
    add(124, 'IF SL=1 THEN GOTO 150')
    add(125, 'REM FAST CODE: BIT = BASE + 16*N - 4*D T-STATES (TX 100, RX 99, CHECK 83)')
    add(130, 'Q=INT((B-100)/4+.5):GOSUB 190:TT(1)=U:TT(2)=V')
    add(131, 'Q=INT((B-99)/4+.5):GOSUB 190:TT(3)=U:TT(4)=V')
    add(132, 'Q=INT((B/2-105)/4+.5):GOSUB 190:TT(5)=U:TT(6)=V')
    add(133, 'GOTO 199')
    add(150, 'REM SLOW CODE: BIT = BASE + M*(15+16*N) - 4*D T-STATES (D=4)')
    add(151, 'M=INT((B-92)/4000)+1:TT(13)=M:TT(2)=4:TT(4)=4:TT(6)=4')
    add(152, 'TT(1)=INT(((B-92)/M-15)/16+.5)')
    add(153, 'TT(3)=INT(((B-91)/M-15)/16+.5)')
    add(154, 'TT(5)=INT(((B/2-97)/M-15)/16+.5)')
    add(155, 'GOTO 199')
    add(190, 'U=INT((Q+3)/4):IF U<1 THEN LET U=1')
    add(191, 'V=4*U-Q:IF V>8 THEN LET V=8')
    add(192, 'RETURN')
    add(199, 'IF A<>0 THEN GOTO 210')
    add(200, 'INPUT "ML ADDRESS (ENTER=49349) ";A')
    add(201, 'IF A=0 THEN LET A=49349')
    add(202, 'IF A<49152 OR A>64000 THEN LET A=0:GOTO 200')
    add(210, 'REM --- STORE THE CODE: FAST OR SLOW SEROUT/SERIN, THEN PROBE ---')
    add(215, 'RESTORE 1000:IF SL=1 THEN RESTORE 2000')
    add(220, 'GOSUB 230')
    add(225, 'RESTORE 3000:GOSUB 230:GOTO 300')
    add(230, 'READ N:C=0')
    add(235, 'FOR S=1 TO N:READ O,L')
    add(240, 'FOR I=0 TO L-1:READ B:POKE A+O+I,B:C=C+B:NEXT I')
    add(250, 'NEXT S')
    add(260, 'READ X:IF X<>C THEN GOTO 960')
    add(265, 'RETURN')
    add(300, 'REM --- Z-80 ADDRESS P OF A: PROBE LOOKS FOR A SIGNATURE IN THE RAM ---')
    add(310, 'RESTORE 920:GOSUB 600:P=Z:IF P=0 THEN GOTO 390')
    add(320, 'RESTORE 930:GOSUB 600:IF Z<>P THEN GOTO 970')
    add(330, 'GOTO 420')
    add(390, 'PRINT "Z-80 ADDRESS NOT FOUND":INPUT "Z-80 ADDRESS OF ML AREA? ";P')
    add(420, 'REM --- BUFFER POINTERS: TXBUF = P+256, RXBUF = P+512 ---')
    add(430, 'W=P+256:W=W-65536*INT(W/65536):TT(14)=W-256*INT(W/256):TT(15)=INT(W/256)')
    add(440, 'W=P+512:W=W-65536*INT(W/65536):TT(16)=W-256*INT(W/256):TT(17)=INT(W/256)')
    add(445, 'REM (THE POINTERS TXBUF=P+256, RXBUF=P+512 GO IN WITH THE TABLES)')
    add(450, 'REM --- POINTERS, TIMING, POLARITY: TABLES AT 900 (FAST), 910 (SLOW) ---')
    add(460, 'RESTORE 900:IF SL=1 THEN RESTORE 910')
    add(465, 'READ N:FOR S=1 TO N:READ O,K,L:POKE A+O,TT(K)*L:NEXT S')
    add(470, 'IF TU=1 THEN GOSUB 800')
    add(490, 'V=INT(BD/256):POKE A+250,85,P-256*INT(P/256),INT(P/256),BD-256*V,V')
    add(500, 'CLS:PRINT "UART1600 OK ";BD;" BPS"')
    add(510, 'PRINT "SEROUT CALL ";A;",N"')
    add(520, 'PRINT "SERIN CALL ";A+%d;",N"' % labF['SERIN'])
    add(530, 'PRINT "TX ";A+256;" RX ";A+512')
    add(535, 'X=0:CALL A+512,X:REM PROBE ENDS WITH EI: THE KEYBOARD (INKEY$) NEEDS INTERRUPTS')
    add(540, 'GOTO 4000')
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
    add(800, 'REM 38400 BPS: JR REPLACES LD D,N IN 4 PLACES (SHORT DELAYS)')
    add(810, 'RESTORE 940:FOR S=1 TO 4:READ O,L:POKE A+O,24,L:NEXT S:RETURN')
    for first, lab, slow in ((900, labF, False), (910, labS, True)):
        pt = patch_table(lab, slow)
        add(first, 'DATA %d' % len(pt))
        row = ['%d,%d,%d' % t for t in pt]
        for k in range(0, len(row), 4):
            add(first + 1 + k // 4, 'DATA ' + ','.join(row[k:k + 4]))
    for n, sig in ((920, SIG), (930, SIG2)):
        sites = [labF['pr_s%d' % i] + 1 for i in range(8)]
        add(n, 'DATA ' + ','.join('%d,%d' % (v, o) for v, o in zip(sig[:4], sites[:4])))
        add(n + 1, 'DATA ' + ','.join('%d,%d' % (v, o) for v, o in zip(sig[4:], sites[4:])))
        add(n + 2, 'DATA %d,%d' % (labF['pr_sh'] + 1, labF['pr_np'] + 1))
    add(940, 'DATA ' + ','.join('%d,%d' % t for t in turbo(labF)))
    add(960, 'PRINT "DATA ERROR: CHECK THE LISTING":BEEP 3:END')
    add(970, 'PRINT "Z-80 ADDRESS UNSURE":PRINT "ML AREA TOO SMALL?":BEEP 3:END')
    for first, code, lab, segs in ((1000, codeF, labF, seg_main(labF)), (2000, codeS, labS, seg_main(labS)),
                                   (3000, codeF, labF, seg_probe(labF))):
        add(first, 'DATA %d' % len(segs))
        if first == 1000:
            add(first + 5, 'REM PER PIECE: OFFSET, LENGTH, BYTES. AT THE END: SUM OF ALL BYTES')
        n = first + 10
        tot = 0
        for a, b in segs:
            add(n, 'DATA %d,%d' % (a, b - a))
            n += 10
            vals = list(code[a:b]); tot += sum(vals)
            for k, t in data_lines(n, vals):
                add(k, t)
                n = k
            n += 10
        add(n, 'DATA %d' % tot)
    menu = [
        (4000, 'REM --- TEST MENU: KEYS 1 TX, 2 RX, 3 STREAM OF 55H, 4 NEW SPEED, 0 END ---'),
        (4005, 'S=A:I=A+%d:T=A+256:R=A+512:L=A+511' % labF['SERIN']),
        (4010, 'PRINT "1TX 2RX 3U 4BAUD 0END"'),
        (4012, 'K=0:FOR G=1 TO 40:Y$=INKEY$'),
        (4013, 'IF Y$="1" THEN K=1:G=40'),
        (4014, 'IF Y$="2" THEN K=2:G=40'),
        (4015, 'IF Y$="3" THEN K=3:G=40'),
        (4016, 'IF Y$="4" THEN K=4:G=40'),
        (4017, 'IF Y$="0" THEN K=9:G=40'),
        (4018, 'NEXT G'),
        (4020, 'IF K=1 THEN GOTO 4100'),
        (4030, 'IF K=2 THEN GOTO 4400'),
        (4040, 'IF K=3 THEN GOTO 4300'),
        (4050, 'IF K=4 THEN GOTO 100'),
        (4060, 'IF K=9 THEN END'),
        (4070, 'GOTO 4012'),
        (4100, 'REM --- TX: HELLO PC-1600 CR LF ---'),
        (4110, 'POKE T,72,69,76,76,79,32,80,67,45,49,54,48,48,13,10'),
        (4120, 'N=15:CALL S,N:PRINT "SENT 15 BYTES":GOTO 4010'),
        (4300, 'REM --- 55H BYTES (SQUARE WAVE FOR A SCOPE), STOP: BREAK ---'),
        (4310, 'FOR J=0 TO 254:POKE T+J,85:NEXT J'),
        (4320, 'PRINT "255 X 55H, STOP: BREAK"'),
        (4330, 'N=255:CALL S,N:GOTO 4330'),
        (4400, 'REM --- RX: PACKETS. HOLD KEY 0 (UP TO 3 S): BACK TO THE MENU ---'),
        (4402, 'F=%d:IF TT(19)=1 THEN LET F=%d' % (labF['si_t1f'] + 1, labS['si_t1f'] + 1)),
        (4404, 'PRINT "ECHO BACK? 1=YES 0=NO"'),
        (4406, 'Y$=INKEY$'),
        (4407, 'IF Y$="1" THEN E=1:GOTO 4410'),
        (4408, 'IF Y$="0" THEN E=0:GOTO 4410'),
        (4409, 'GOTO 4406'),
        (4410, 'POKE A+F,3:PRINT "WAITING (HOLD 0=MENU)"'),
        (4430, 'N=0:CALL I,N:D=PEEK(L)'),
        (4440, 'IF D=0 THEN Y$=INKEY$'),
        (4442, 'IF D=0 AND Y$="0" THEN GOTO 4590'),
        (4445, 'IF D=0 THEN GOTO 4430'),
        (4450, 'PRINT "RX ";D;" BYTES:"'),
        (4460, 'IF E<>1 THEN GOTO 4500'),
        (4470, 'FOR J=0 TO D-1:POKE T+J,PEEK(R+J):NEXT J'),
        (4480, 'M=D:CALL S,M'),
        (4500, 'Z$=""'),
        (4510, 'FOR J=0 TO D-1:C=PEEK(R+J)'),
        (4520, 'IF C<32 THEN GOTO 4560'),
        (4530, 'IF C>126 THEN LET C=46'),
        (4540, 'Z$=Z$+CHR$(C)'),
        (4550, 'IF LEN(Z$)>=26 THEN PRINT Z$:Z$=""'),
        (4560, 'NEXT J'),
        (4570, 'IF LEN(Z$)>0 THEN PRINT Z$'),
        (4580, 'GOTO 4430'),
        (4590, 'POKE A+F,TT(10):GOTO 4010')]
    L += menu
    L.sort()
    # no two-letter simple variables (the PC-1600 has A-Z only): BD, SL, TU live in the array TT()
    names = {'BD': 'TT(18)', 'SL': 'TT(19)', 'TU': 'TT(20)'}

    def fix(t):
        if t.startswith(('DATA', 'REM')):
            return t
        code, sep, comment = t.partition(':REM')
        parts = code.split('"')
        for i in range(0, len(parts), 2):
            parts[i] = re.sub(r'\b(BD|SL|TU)\b', lambda m: names[m.group(1)], parts[i])
        return '"'.join(parts) + sep + comment
    L = [(n, fix(t)) for n, t in L]
    nums = [n for n, _ in L]
    assert len(set(nums)) == len(nums), 'duplicate line numbers'
    text = ''.join('%d %s\n' % (n, t) for n, t in L)
    return text, (codeF, labF), (codeS, labS)


if __name__ == '__main__':
    text, (cF, lF), (cS, lS) = build()
    open(os.path.join(ROOT, 'Basic', 'uart1600.txt'), 'w', newline='\n').write(text)
    print('Basic/uart1600.txt written;', len(text), 'bytes;', len(text.splitlines()), 'lines; longest',
          max(len(l) for l in text.splitlines()))
