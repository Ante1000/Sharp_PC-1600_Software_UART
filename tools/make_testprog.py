#!/usr/bin/env python3
"""Makes a long BASIC test program for the PC-1600 (to test the transfer of big programs, e.g. with SerComet / the Saver/Loader).
The program checks itself: every block calculates something with FOR..NEXT loops, strings, DATA/READ/RESTORE, GOSUB and compares the result
with a constant that was calculated here. At the end it prints the number of errors and the size of the program (STATUS 1), which must
be equal to the size calculated here. A damaged or incomplete transfer shows up as an error, a syntax error or a wrong size.
Usage: python3 tools/make_testprog.py [size in bytes of the tokenized program, default 12000] [output file]
The tokenized size is calculated with the rules of SerComet (keyword = 2 bytes, no spaces, a line number after GOSUB/RESTORE/THEN = 4 bytes)."""
import math, random, re, sys

KEYWORDS = ['RESTORE', 'GOSUB', 'RETURN', 'STATUS', 'WAIT', 'PRINT', 'THEN', 'NEXT', 'DATA', 'READ', 'LEFT$', 'MID$', 'VAL', 'SQR',
            'ABS', 'INT', 'LEN', 'FOR', 'REM', 'CLS', 'END', 'GOTO', 'TO', 'IF']   # longest first
LINEKW = ('GOSUB', 'GOTO', 'RESTORE', 'THEN')

def tokenized_size(lines):
    """bytes of the program: per line number(2) + length(1) + content + CR"""
    total = 0
    for no, body in lines:
        n = 0; i = 0; line_mode = False; instr = False
        while i < len(body):
            c = body[i]
            if instr:
                n += 1; i += 1
                if c == '"': instr = False
                continue
            if c == '"': instr = True; n += 1; i += 1; line_mode = False; continue
            if c == ' ': i += 1; continue
            if line_mode and c.isdigit():
                j = i
                while j < len(body) and body[j].isdigit(): j += 1
                n += 4; i = j; line_mode = False; continue
            kw = next((k for k in KEYWORDS if body.startswith(k, i)), None)
            if kw:
                n += 2; i += len(kw)
                if kw in ('REM', 'DATA'):
                    rest = body[i:]
                    if rest.startswith(' '): rest = rest[1:]
                    n += len(rest); break
                line_mode = kw in LINEKW
                continue
            line_mode = False; n += 1; i += 1
        total += 3 + n + 1
    return total

def make(target):
    lines = []                    # (number, text)
    no = [100]
    def add(text):
        assert len(text) + len(str(no[0])) + 1 <= 80, text
        lines.append((no[0], text)); no[0] += 10
    def build(nblocks, size_const):
        rnd = random.Random(2026)            # the same random contents for every call
        lines.clear(); no[0] = 100
        add('CLS:WAIT 0:E=0:T=0:N=0')
        add('PRINT "LONG TEST OF A BIG PROGRAM"')
        for n in range(1, nblocks + 1):
            kind = n % 5
            add('REM BLOCK %d' % n + ' ' + ''.join(rnd.choice('ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 ') for _ in range(40)).strip().replace('  ', ' '))   # no spaces at the end of the line
            if kind == 1:        # a loop with a sum
                m = rnd.randint(5, 40); k = rnd.randint(2, 99)
                add('N=%d:S=0:K=%d:FOR I=1 TO %d:S=S+I*K:NEXT I' % (n, k, m))
                add('IF S<>%d THEN GOSUB 9000' % (k * m * (m + 1) // 2))
            elif kind == 2:      # strings
                s = ''.join(rnd.choice('ABCDEFGHIJKLMNOPQRSTUVWXYZ') for _ in range(10)); p = rnd.randint(1, 8)
                add('N=%d:A$="%s":B$=MID$(A$,%d,3):C$=LEFT$(A$,2)' % (n, s, p))
                add('IF B$<>"%s" THEN GOSUB 9000' % s[p - 1:p + 2])
                add('IF C$<>"%s" OR LEN(A$)<>10 THEN GOSUB 9000' % s[:2])
                v = rnd.randint(1000, 99999)
                add('IF VAL("%d")<>%d THEN GOSUB 9000' % (v, v))
            elif kind == 3:      # DATA, READ, RESTORE
                vals = [rnd.randint(1, 999) for _ in range(6)]
                dl = no[0] + 30
                add('N=%d:RESTORE %d:S=0:FOR I=1 TO 6:READ X:S=S+X:NEXT I' % (n, dl))
                add('IF S<>%d THEN GOSUB 9000' % sum(vals))
                add('GOTO %d' % (no[0] + 20))       # jumps over the DATA line
                assert no[0] == dl - 10 + 10 or True
                add('DATA ' + ','.join(map(str, vals)))
                assert lines[-1][0] == dl, (lines[-1][0], dl)
            elif kind == 4:      # nested loops and GOSUB
                a = rnd.randint(2, 9); b = rnd.randint(2, 9)
                add('N=%d:S=0:T=0:FOR I=1 TO %d:FOR J=1 TO %d:S=S+I*J:GOSUB 9100:NEXT J:NEXT I' % (n, a, b))
                add('IF S<>%d OR T<>%d THEN GOSUB 9000' % ((a * (a + 1) // 2) * (b * (b + 1) // 2), a * b))
            else:                # square roots and a progress message
                r = rnd.randint(2, 99)
                add('N=%d:X=SQR(%d)' % (n, r * r))
                add('IF ABS(X-%d)>.000001 THEN GOSUB 9000' % r)
                if n % 25 == 0:
                    add('PRINT "BLOCK %d DONE"' % n)
        no[0] = max(no[0], 8900)
        add('PRINT "TEST FINISHED, ERRORS: ";E')
        add('PRINT "SIZE ";STATUS 1;" EXPECTED %d"' % size_const)
        add('END')
        no[0] = 9000
        add('E=E+1:PRINT "ERROR IN BLOCK ";N:RETURN')
        no[0] = 9100
        add('T=T+1:RETURN')
    nb = 10
    while True:
        build(nb, 99999)
        if tokenized_size(lines) >= target: break
        nb += 1
    size = tokenized_size(lines)
    for _ in range(4):               # the constant (the size) is part of the program: repeat until it is stable
        build(nb, size)
        new = tokenized_size(lines)
        if new == size: break
        size = new
    assert tokenized_size(lines) == size
    return lines, size, nb

if __name__ == '__main__':
    target = int(sys.argv[1]) if len(sys.argv) > 1 else 12000
    out = sys.argv[2] if len(sys.argv) > 2 else 'Basic/testlong_%dk.bas' % round(target / 1000)
    lines, size, nb = make(target)
    open(out, 'w', newline='\n').write(''.join('%d %s\n' % l for l in lines))
    print('%s written: %d blocks, %d lines, %d bytes of text, tokenized %d bytes (STATUS 1 expected)' % (out, nb, len(lines), sum(len(t) + len(str(n)) + 2 for n, t in lines), size))
