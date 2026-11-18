#!/usr/bin/env python3
"""Tests of the PC-1500 Saver/Loader (asm/uart1500_sl.asm, Basic/sl1500.txt) without a PC-1500.

A small LH5801 simulator (only the instructions the SL uses, with the cycle counts of the pc1500emu opcode table: 1.3 MHz) runs
the routines with a memory laid out like the BASIC of the PC-1500 (pointers &7864..&7868), records the PC7 output with the cycle
count of every edge (decoded as 8N1 at 4800 bps) and feeds PB2 / PB0 with a bit stream. Tests: SAVE of programs of different
lengths, LOAD (also with +-3 % baud error, gaps, a glitch, PB0), checksum error, nothing received, does not fit, exact fit.
The installer text Basic/sl1500.txt is parsed too: its DATA must give exactly the assembled code (and the patched PB0 bytes)."""
import os, random, re, sys
HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
sys.path.insert(0, HERE)
import lh5801asm as A

CLK = 1.3e6
SRC = os.path.join(ROOT, 'asm', 'uart1500_sl.asm')


class Cpu:
    def __init__(self, mem, rx=(), rxmask=4):
        self.m = mem; self.a = 0; self.x = 0x1234; self.y = 0x5678; self.u = 0x9ABC; self.c = 0; self.z = 0; self.ie = 1
        self.s = 0x7BF0; self.p = 0; self.now = 0; self.tx = []; self.txl = 1; self.rx = list(rx); self.ri = 0; self.lvl = 1
        self.rxmask = rxmask; self.io = [0] * 16; self.io[8] = 0x80

    # --- memory (ME0: RAM 4000-47FF, 7600-7FFF; ME1: LH5811 ports F000-F00F)
    def rd(self, a): return self.m[a & 0xFFFF]
    def wr(self, a, v):
        a &= 0xFFFF
        if 0x4000 <= a < 0x4800 or 0x7600 <= a < 0x8000: self.m[a] = v & 0xFF
    def rd1(self, a):
        a &= 15
        if a == 15:
            while self.ri < len(self.rx) and self.rx[self.ri][0] <= self.now: self.lvl = self.rx[self.ri][1]; self.ri += 1
            return self.rxmask if self.lvl else 0
        return self.io[a]
    def wr1(self, a, v):
        a &= 15; self.io[a] = v & 0xFF
        if a == 8:
            l = (v >> 7) & 1
            if l != self.txl: self.tx.append((self.now, l)); self.txl = l
    def push(self, v): self.wr(self.s, v); self.s -= 1
    def pop(self): self.s += 1; return self.rd(self.s)
    def fetch(self): v = self.rd(self.p); self.p = (self.p + 1) & 0xFFFF; return v
    def add(self, a, b, c):
        s = a + b + c; self.c = 1 if s > 255 else 0; self.z = 1 if (s & 255) == 0 else 0; return s & 255
    def step(self):
        o = self.fetch(); m = self.m
        X, Y, U = 'x', 'y', 'u'
        def reg(n): return getattr(self, n)
        def setr(n, v): setattr(self, n, v & 0xFFFF)
        def lo(n): return reg(n) & 255
        def hi(n): return reg(n) >> 8
        def setlo(n, v): setr(n, (reg(n) & 0xFF00) | (v & 255))
        def sethi(n, v): setr(n, (reg(n) & 0x00FF) | ((v & 255) << 8))
        def ld(v): self.a = v; self.z = 1 if v == 0 else 0
        def cmp(v): self.add(self.a, (~v) & 255, 1)
        def br(cond, fwd):
            i = self.fetch()
            if cond: self.p = (self.p + i if fwd else self.p - i) & 0xFFFF; return None
            return 8
        t = {0x04: lambda: (ld(lo(X)), 5)[1], 0x14: lambda: (ld(lo(Y)), 5)[1], 0x24: lambda: (ld(lo(U)), 5)[1],
             0x84: lambda: (ld(hi(X)), 5)[1], 0x94: lambda: (ld(hi(Y)), 5)[1], 0xA4: lambda: (ld(hi(U)), 5)[1],
             0x05: lambda: (ld(self.rd(self.x)), 6)[1], 0x15: lambda: (ld(self.rd(self.y)), 6)[1],
             0x45: lambda: (ld(self.rd(self.x)), setattr(self, 'x', (self.x + 1) & 0xFFFF), 6)[2],
             0x08: lambda: (sethi(X, self.a), 5)[1], 0x0A: lambda: (setlo(X, self.a), 5)[1],
             0x18: lambda: (sethi(Y, self.a), 5)[1], 0x1A: lambda: (setlo(Y, self.a), 5)[1],
             0x28: lambda: (sethi(U, self.a), 5)[1], 0x2A: lambda: (setlo(U, self.a), 5)[1],
             0x0E: lambda: (self.wr(self.x, self.a), 6)[1], 0x1E: lambda: (self.wr(self.y, self.a), 6)[1],
             0x41: lambda: (self.wr(self.x, self.a), setattr(self, 'x', (self.x + 1) & 0xFFFF), 6)[2],
             0x06: lambda: (cmp(lo(X)), 6)[1], 0x16: lambda: (cmp(lo(Y)), 6)[1], 0x26: lambda: (cmp(lo(U)), 6)[1],
             0x86: lambda: (cmp(hi(X)), 6)[1], 0x96: lambda: (cmp(hi(Y)), 6)[1], 0xA6: lambda: (cmp(hi(U)), 6)[1],
             0xA2: lambda: (setattr(self, 'a', self.add(self.a, hi(U), self.c)), 6)[1],
             0x38: lambda: 5, 0xF9: lambda: (setattr(self, 'c', 0), 4)[1], 0xFB: lambda: (setattr(self, 'c', 1), 4)[1],
             0x44: lambda: (setr(X, self.x + 1), 5)[1], 0x54: lambda: (setr(Y, self.y + 1), 5)[1],
             0x46: lambda: (setr(X, self.x - 1), 5)[1], 0x56: lambda: (setr(Y, self.y - 1), 5)[1],
             0x62: lambda: (setlo(U, self.add(lo(U), 255, 0)), 5)[1], 0xDF: lambda: (setattr(self, 'a', self.add(self.a, 255, 0)), 5)[1],
             0x9A: lambda: (setattr(self, 'p', (lambda h, l: (h << 8) | l)(self.pop(), self.pop())), 11)[1]}
        if o in t: return t[o]()
        if o in (0x48, 0x4A, 0x58, 0x5A, 0x68, 0x6A):          # LDI
            i = self.fetch(); r = {0x4: X, 0x5: Y, 0x6: U}[o >> 4]
            (setlo if o & 2 else sethi)(r, i); return 6 if o & 2 else 5
        if o == 0xB5: ld(self.fetch()); return 6
        if o == 0xBF: self.z = 1 if (self.a & self.fetch()) == 0 else 0; return 7
        if o in (0xA5, 0xA7, 0xAE):
            ad = (self.fetch() << 8) | self.fetch()
            if o == 0xA5: ld(self.rd(ad)); return 12
            if o == 0xA7: cmp(self.rd(ad)); return 13
            self.wr(ad, self.a); return 12
        if o == 0x88:
            i = self.fetch(); old = lo(U); setlo(U, old - 1)
            if old != 0: self.p = (self.p - i) & 0xFFFF; return 11
            return 8
        if o == 0x89: return br(not self.z, 1) or 10
        if o == 0x8B: return br(self.z, 1) or 10
        if o == 0x81: return br(not self.c, 1) or 10
        if o == 0x83: return br(self.c, 1) or 10
        if o == 0x99: return br(not self.z, 0) or 11
        if o == 0x9B: return br(self.z, 0) or 11
        if o == 0x93: return br(self.c, 0) or 11
        if o == 0x8E: i = self.fetch(); self.p = (self.p + i) & 0xFFFF; return 8
        if o == 0x9E: i = self.fetch(); self.p = (self.p - i) & 0xFFFF; return 9
        if o == 0xD1:                                               # ROR
            b0 = self.a & 1; self.a = (self.a >> 1) | (0x80 if self.c else 0); self.c = b0; self.z = 1 if self.a == 0 else 0; return 8
        if o == 0xD5:                                               # SHR
            b0 = self.a & 1; self.a >>= 1; self.c = b0; self.z = 1 if self.a == 0 else 0; return 8
        if o == 0xFD:
            o2 = self.fetch()
            if o2 == 0x59: i = self.fetch(); v = self.rd1(self.y) & i; self.wr1(self.y, v); self.z = 1 if v == 0 else 0; return 17
            if o2 == 0x5B: i = self.fetch(); v = self.rd1(self.y) | i; self.wr1(self.y, v); self.z = 1 if v == 0 else 0; return 17
            if o2 == 0x5D: i = self.fetch(); self.z = 1 if (self.rd1(self.y) & i) == 0 else 0; return 14
            if o2 == 0x62: sethi(U, self.add(hi(U), 255, 0)); return 9
            if o2 == 0x5A: self.y = self.x; return 11
            if o2 == 0xC8: self.push(self.a); return 11
            if o2 == 0x8A: self.a = self.pop(); self.z = 1 if self.a == 0 else 0; return 12
            if o2 == 0xBE: self.ie = 0; return 8
            if o2 == 0x81: self.ie = 1; return 8
            raise SystemExit('FD %02X not supported at %04X' % (o2, self.p - 2))
        raise SystemExit('opcode %02X not supported at %04X' % (o, self.p - 1))

    def run(self, entry, maxc=300_000_000):
        self.p = entry
        self.s = 0x7BF0
        self.push(0xF0); self.push(0x6F)            # return address 6FF0: the low byte is pushed first (as by the CPU)
        while self.p != 0x6FF0 and self.now < maxc:
            self.now += self.step()
        return self.now


def assemble(consts=None):
    code, lab, _ = A.assemble(open(SRC).read(), consts)
    return code, lab


def prog(n, seed=1):
    random.seed(seed); b = bytearray(); ln = 10
    while len(b) < n:
        L = random.randint(1, 30)
        b += bytes([ln >> 8, ln & 255, L]) + bytes(random.randint(1, 0xEF) for _ in range(L - 1)) + b'\r'; ln += 10
    return bytes(b)


def decode(tx, baud=4800):
    bt = CLK / baud; out = []; pos = 0; issues = 0
    def lvl(t):
        l = 1
        for c, v in tx:
            if c <= t: l = v
            else: break
        return l
    while True:
        st = [c for c, v in tx if v == 0 and c >= pos]
        if not st: break
        t0 = st[0]; bits = [lvl(t0 + bt * (k + .5)) for k in range(10)]
        if bits[0] != 0 or bits[9] != 1: issues += 1
        out.append(sum(bits[1 + k] << k for k in range(8))); pos = t0 + bt * 9.5
    return bytes(out), issues


def edges(data, baud=4800, err=0.0, t0=100000, gap=0.0):
    bt = CLK / baud * (1 + err); ev = []; t = t0
    for b in data:
        for bit in [0] + [(b >> k) & 1 for k in range(8)] + [1]:
            ev.append((int(t), bit)); t += bt
        t += gap * bt
    ev.append((int(t), 1)); out = []; l = 1
    for c, v in ev:
        if v != l: out.append((c, v)); l = v
    return out


def memory(start, content, end_ptr, ramh=0x48):
    m = bytearray(65536)
    m[start:start + len(content)] = content
    m[0x7864] = ramh; m[0x7865:0x7867] = start.to_bytes(2, 'big'); m[0x7867:0x7869] = end_ptr.to_bytes(2, 'big')
    return m


FAILS = []


def check(name, ok, info=''):
    print('%-48s %s %s' % (name, 'ok' if ok else 'FAIL', info))
    if not ok: FAILS.append(name)


def main():
    code, lab = assemble()
    LOADA, SAVEA = 0x40C5, 0x40C5 + lab['SAVE']
    start = 0x4200
    # ---------------- SAVE
    for n in (0, 5, 60, 300, 700):
        p = prog(n) if n else b''
        m = memory(start, p + b'\xff', start + len(p))
        m[LOADA:LOADA + len(code)] = code
        c = Cpu(m); c.run(SAVEA)
        got, iss = decode(c.tx)
        exp = p + bytes([sum(p) & 255])
        ok = got == exp and not iss and c.x == 1 and c.c == 1 and c.ie == 1 and m[start + len(p)] == 0xFF and m[0x7867:0x7869] == (start + len(p)).to_bytes(2, 'big')
        check('SAVE %d bytes' % len(p), ok, '(%d cycles = %.2f bit times per byte)' % (c.now, c.now / max(1, len(exp)) / (CLK / 4800)))
    # ---------------- LOAD
    def load(p, f=None, ramh=0x48, consts=None, rxmask=4, edge_kw=None, extra=(), maxc=300_000_000):
        cd, lb = assemble(consts)
        f = f if f is not None else p + bytes([sum(p) & 255])
        m = memory(start, b'\xff', start, ramh); m[LOADA:LOADA + len(cd)] = cd
        c = Cpu(m, list(extra) + edges(f, **(edge_kw or {})), rxmask); c.run(LOADA, maxc)
        return c, m
    for n in (0, 1, 80, 300, 700):
        p = prog(n) if n else b''
        for kw in ({}, {'err': 0.03}, {'err': -0.03}, {'gap': 3.7}, {'t0': 123457}):
            c, m = load(p, edge_kw=kw)
            ok = c.x == 1 and bytes(m[start:start + len(p)]) == p and m[start + len(p)] == 0xFF and m[0x7867:0x7869] == (start + len(p)).to_bytes(2, 'big') and c.c == 1 and c.ie == 1
            check('LOAD %d bytes %s' % (len(p), kw or ''), ok, 'X=%d' % c.x)
    p = prog(100)
    c, m = load(p, f=p + bytes([(sum(p) + 1) & 255])); check('LOAD checksum error -> 2, memory empty', c.x == 2 and m[start] == 0xFF and m[0x7867:0x7869] == start.to_bytes(2, 'big'), 'X=%d' % c.x)
    p = prog(1700)
    c, m = load(p); check('LOAD too long -> 4, memory empty', c.x == 4 and m[start] == 0xFF and m[0x7867:0x7869] == start.to_bytes(2, 'big'), 'X=%d' % c.x)
    p = bytearray(prog(1500))
    while len(p) < 1535: p += b'\x01'
    p = bytes(p[:1535])
    c, m = load(p); check('LOAD exact fit (1536 bytes to &47FF)', c.x == 1 and m[0x7867:0x7869] == (0x47FF).to_bytes(2, 'big') and m[0x47FF] == 0xFF, 'X=%d' % c.x)
    p = prog(30)
    c, m = load(p, extra=[(60000, 0), (60040, 1)]); check('LOAD glitch before the start bit', c.x == 1 and bytes(m[start:start + len(p)]) == p)
    p = prog(80)
    c, m = load(p, consts={'DB': 0xFE, 'BM': 1}, rxmask=1); check('LOAD with PB0', c.x == 1 and bytes(m[start:start + len(p)]) == p)
    for sign in (1, -1):
        last = 0
        for e in range(10, 100, 5):
            if not all(load(prog(200), edge_kw={'err': sign * e / 1000, 't0': t})[0].x == 1 for t in (100000, 100137, 100211)): break
            last = e
        check('LOAD baud error tolerance %+d%%' % sign, last >= 30, '(up to %+.1f %%)' % (sign * last / 10))
    c, m = load(b'', f=b''); check('LOAD nothing received -> 3 after about 30 s', c.x == 3 and 25 < c.now / CLK < 35, '(%.1f s)' % (c.now / CLK))
    # ---------------- installer
    txt = open(os.path.join(ROOT, 'Basic', 'sl1500.txt')).read().splitlines()
    data = []
    for ln in txt:
        m_ = re.match(r'^(\d+) (.*)$', ln)
        if int(m_.group(1)) >= 1000 and m_.group(2).startswith('DATA '): data += [int(v) for v in m_.group(2)[5:].split(',')]
    check('installer: DATA = assembled code + checksum', bytes(data[:-1]) == code and data[-1] == sum(code) and len(data) == len(code) + 1)
    check('installer: every line <= 80 characters', max(len(l) for l in txt) <= 80, '(longest %d)' % max(len(l) for l in txt))
    c0, _ = assemble(); c1, _ = assemble({'DB': 0xFE, 'BM': 1})
    diff = [i for i in range(len(c0)) if c0[i] != c1[i]]
    pk = re.findall(r'POKE L\+(\d+),', '\n'.join(txt))
    check('installer: the PB0 patches are exactly the bytes that differ', sorted(int(v) for v in pk) == diff, '(%s)' % diff)
    print('\ncode %d bytes (LOAD %d + SAVE %d); LOAD at &C5, SAVE at &%X from the start of the RAM; ends at &%X' % (len(code), lab['SAVE'], len(code) - lab['SAVE'], 0xC5 + lab['SAVE'], 0xC5 + len(code)))
    print('ALL OK' if not FAILS else 'FAILED: ' + ', '.join(FAILS))
    return 1 if FAILS else 0


if __name__ == '__main__':
    sys.exit(main())
