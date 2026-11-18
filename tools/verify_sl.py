#!/usr/bin/env python3
"""Emulator test of Basic/sl.txt (the Saver/Loader V3.0, 1200 to 19200 bps). The code and the patch sites are read from the DATA and
POKE lines of the BASIC installer; the delays are calculated as the installer does. For every speed it checks that SAVE sends exactly the
bytes of a program plus the checksum, that LOAD takes the stream (also at +-3 % of the speed and with gaps), and the error cases.
z80asm is not needed. pip install z80
Usage: python3 tools/verify_sl.py"""
import math, os, random, re, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import verify_listing as V
ROOT = V.ROOT
lines = V.load_listing(os.path.join(ROOT, 'Basic', 'sl.txt'))
st = V.Stream(lines, 1000)
n = st.read(); base = bytearray(0x200); tot = 0
for _ in range(n):
    o, l = st.read(2)
    bs = st.read(l); assert all(0 <= x <= 255 for x in bs)
    base[o:o + l] = bytes(bs); tot += sum(bs)
assert st.read() == tot, 'checksum of the DATA lines'
LOAD, SAVE = 0, 256                      # offsets from A = 49408 = &C100 (SAVE at &C200)

def sites(line):
    return [(int(m.group(1)), m.group(2)) for m in re.finditer(r'POKE A\+(\d+),([UV])', lines[line])]

def uv(q):
    u = max(1, math.floor((q + 3) / 4)); v = min(4 * u - q, 8)
    return u, v

def image(bd):
    """the code with the delays of the installer for the speed bd"""
    img = bytearray(base)
    t = 3000000 / bd
    for line, q in ((114, math.floor((t - 100) / 4 + .5)), (116, math.floor((t - 99) / 4 + .5)),
                    (117, math.floor((t - 99) / 4 + .5)), (118, math.floor((t / 2 - 105) / 4 + .5))):
        u, v = uv(q)
        for off, var in sites(line):
            img[off] = u if var == 'U' else v
    return img

P = 0xC100
rnd = random.Random(5)
prog = bytearray()
def line(no, content):          # a valid line: number (high byte first), length (token to CR), content, CR
    global prog
    prog += bytes([no >> 8, no & 255, len(content) + 1]) + bytes(content) + b'\r'
line(10, [241, 171] + list(b'MYPROG.TEST REM TEXT'))
for k in range(20, 200, 10):
    line(k, [rnd.randrange(32, 126) for _ in range(10)])
line(95, [0x41, 0xFF, 0xFF, 0x00, 0x42, 0xFF])     # FFH inside a line does not end the program: only FFH where a line number begins
START = 0x80C5; END = START + len(prog)
def setup(r, pb=prog, start=START):
    mem = r.m.memory
    mem[start:start + len(pb)] = bytes(pb)
    mem[0xFE3C] = start & 255; mem[0xFE3D] = start >> 8; mem[0xFE3E] = 4
    e = start + len(pb); mem[0xFE3F] = e & 255; mem[0xFE40] = e >> 8; mem[0xFE41] = 4
    mem[0xF867] = (e >> 8) ^ 0x80; mem[0xF868] = e & 255; mem[0xF869] = 0x77        # F867H/F868H: the end in the PC-1500 notation
    mem[e] = 0xFF
stream = bytes(prog) + bytes([sum(prog) & 255])
ok = True
print('code from the DATA lines; program %d bytes; LOAD at +0, SAVE at +256' % len(prog))
for bd in (1200, 2400, 4800, 9600, 19200):
    img = image(bd); bit = V.FCLK / bd
    # SAVE
    r = V.Rig(img, SAVE, 0, P); setup(r); r.opc = 0x80
    r.m.alt_bc = 0x1234; r.m.alt_de = 0x5678; r.m.alt_hl = 0x9abc; r.m.bc = 0x0f0e; r.m.hl = 0x2468; r.m.af = 0x1122
    r.run()
    dec = V.decode(r.outs, bit); got = bytes(v for v, s in dec)
    good = got == stream and all(s == 1 for v, s in dec) and r.m.de == 1 and r.opc == 0x80
    # SAVE finds the end of the program from the end marker, not from FE3FH: a wrong end pointer (5 bytes before the start, as seen on
    # a real PC-1600 with a large program: STATUS 1 = -5) and an empty program
    r2 = V.Rig(img, SAVE, 0, P); setup(r2); r2.opc = 0x80
    r2.m.memory[0xFE3F] = (START - 5) & 255; r2.m.memory[0xFE40] = (START - 5) >> 8
    r2.run(); g2 = bytes(v for v, s2 in V.decode(r2.outs, bit)) == stream
    r3 = V.Rig(img, SAVE, 0, P); setup(r3, bytes([0xFF])); r3.opc = 0x80; r3.run()
    g3 = bytes(v for v, s2 in V.decode(r3.outs, bit)) == bytes([0xFF]) or bytes(v for v, s2 in V.decode(r3.outs, bit)) == bytes([0])
    print('        wrong end pointer (start - 5): the whole program is sent: %s; empty program ends at once: %s' % (g2, g3))
    ok &= g2 and g3
    # the acceptance window of an ideal receiver around the nominal bit time
    def sok(m):
        d = V.decode(r.outs, bit * m); return bytes(v for v, s in d) == stream
    ws = [m / 1000 for m in range(900, 1101, 5) if sok(m / 1000)]
    print('%5d bps SAVE: %d bytes sent, exact %s, DE=%d, OPC %02X; a receiver x%.3f..x%.3f of the nominal bit time reads it'
          % (bd, len(got), good, r.m.de, r.opc, min(ws), max(ws)))
    ok &= good and min(ws) <= 0.97 and max(ws) >= 1.03
    # LOAD at several phases and speeds (+-3 %), with and without gaps
    def load(data, start=START, newprog=bytes(40), mult=1.0, gap=0, phase=0.0, regs=False, shorten=False):
        r = V.Rig(img, LOAD, 0, P, wave=V.waveform(data, bit * mult, phase=phase, gap=gap)); setup(r, newprog, start)
        if regs:
            r.m.alt_af = 0x3344; r.m.alt_bc = 0x1234; r.m.alt_de = 0x5678; r.m.alt_hl = 0x9abc; r.m.bc = 0x0f0e; r.m.hl = 0x2468
        if shorten:
            i = P + LOAD; j = bytes(r.m.memory[i:i + 80]).find(bytes([0x0E, 0x00, 0x16, 30])); r.m.memory[i + j + 3] = 1
        r.run(); mem = r.m.memory
        return r, mem[0xFE3F] + 256 * mem[0xFE40]
    def pe2(r): return ((r.m.memory[0xF867] ^ 0x80) << 8) + r.m.memory[0xF868]      # the end as the BASIC reads it (F869H is not touched)
    res = []
    lo, hi = (0.98, 1.02) if bd == 19200 else (0.97, 1.03)      # at 19200 one step of the pad is 2.6 % of the bit: +-2 % there
    for ph, gap, mult in ((0, 0, 1.0), (30, 0, 1.0), (10, 0, lo), (10, 0, hi), (20, 3, 1.0)):
        r, pe = load(stream, phase=ph, gap=gap, mult=mult, regs=True)
        g = r.m.de == 1 and pe == END and pe2(r) == END and r.m.memory[END] == 0xFF and bytes(r.m.memory[START:START + len(prog)]) == bytes(prog)
        regs = (r.m.alt_af, r.m.alt_bc, r.m.alt_de, r.m.alt_hl, r.m.bc, r.m.hl) == (0x3344, 0x1234, 0x5678, 0x9abc, 0x0f0e, 0x2468)
        res.append(g and regs)
    print('%5d bps LOAD: phases 0/30 %%, speeds x%.2f and x%.2f, gap of 3 bits: %s' % (bd, lo, hi, 'ok' if all(res) else 'WRONG %s' % res))
    ok &= all(res)
    if bd in (9600, 19200):
        bad = bytearray(stream); bad[-1] ^= 0x55
        r, pe = load(bytes(bad)); g = r.m.de == 2 and pe == START and pe2(r) == START and r.m.memory[START] == 0xFF; ok &= g
        print('        wrong checksum: status', r.m.de, '(expect 2); memory empty:', g)
        r, pe = load(b'', shorten=True); g = r.m.de == 3 and pe == START + 40; ok &= g
        print('        nothing received: status', r.m.de, '(expect 3); untouched:', g)
        r, pe = load(bytes([7])); g = r.m.de == 2 and pe == START and r.m.memory[START] == 0xFF; ok &= g
        print('        one byte: status', r.m.de, '(expect 2); empty:', g)
        r, pe = load(bytes(stream), start=0xBF00); g = r.m.de == 4 and pe == 0xBF00 and r.m.memory[0xBF00] == 0xFF; ok &= g
        print('        does not fit: status', r.m.de, '(expect 4); empty:', g)
        r, pe = load(bytes([5, 5]), newprog=bytes(40)); g = r.m.de == 1 and pe == START + 1 and r.m.memory[START + 1] == 0xFF; ok &= g
        print('        one byte and its checksum: status', r.m.de, 'marker', g)
        longp = bytes([0, 10, 6, 241, 171, 79, 76, 79, 13, 0, 20, 9, 241, 171, 77, 65, 78, 79, 76, 79, 13, 0, 30, 8, 76, 79, 86, 69, 79, 76, 79, 13])
        sm = bytes([0, 10, 8, 241, 171, 72, 69, 76, 76, 79, 13, 0, 20, 6, 241, 171, 88, 89, 90, 13]); stt = sm + bytes([sum(sm) & 255])
        r, pe = load(stt, newprog=longp); g = r.m.de == 1 and r.m.memory[START + len(sm)] == 0xFF and pe == START + len(sm) and pe2(r) == pe; ok &= g
        print('        a short program over a long one: marker after the last line:', g)
print('ALL OK' if ok else 'FAILED')
sys.exit(0 if ok else 1)
