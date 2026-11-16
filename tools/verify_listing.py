#!/usr/bin/env python3
"""Checks Basic/uart1600.txt in a Z-80 emulator (pip install z80).

It does what the BASIC loader does (reads the DATA lines, stores the code at a trial address, patches the
pointers and the timing bytes from the tables of the listing, using the TT() values of lines 110-160) and
then runs SEROUT and SERIN against an ideal 19200 bps partner. The timing is counted in Z-80 T-states
(CPU 3.0 MHz as measured on a real PC-1600, standard Zilog timing). This is a simulation: it says nothing about the real PC-1600.

Usage:  python3 tools/verify_listing.py [Basic/uart1600.txt] [baud]
"""
import bisect, os, random, re, sys
import z80

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
FCLK = 3.0e6            # measured on a real PC-1600: about 2.9-3.06 MHz (not 3.58 MHz)
SERIN = 0x54            # offset of SERIN in the code
FRAME = 100000          # the emulator's run() returns every 100000 T-states


def load_listing(path):
    lines = {}
    for l in open(path).read().splitlines():
        m = re.match(r'(\d+) (.*)', l)
        lines[int(m.group(1))] = m.group(2)
    return lines


class Stream:
    """READ / RESTORE of the BASIC listing: the DATA values from a line onward, REM lines are skipped"""
    def __init__(self, lines, first):
        self.v = []
        for n in sorted(k for k in lines if k >= first):
            t = lines[n]
            if t.startswith('DATA '):
                self.v += [int(x) for x in t[5:].split(',')]
            elif t.startswith('REM'):
                continue
            else:
                break
        self.p = 0

    def read(self, k=1):
        r = self.v[self.p:self.p + k]; self.p += k
        assert len(r) == k, 'out of data'
        return r if k > 1 else r[0]


def datas(lines, first):
    return Stream(lines, first).v


def read_set(lines, first):
    """what the loader does in lines 230-265: [(offset, bytes)] and the sum check"""
    st = Stream(lines, first)
    n = st.read(); chunks = []; c = 0
    for _ in range(n):
        o, l = st.read(2)
        bs = st.read(l) if l > 1 else [st.read()]
        assert all(0 <= x <= 255 for x in bs), 'byte out of range'
        chunks.append((o, bs)); c += sum(bs)
    assert st.read() == c, 'checksum of the DATA lines'
    return chunks


def floor(x):
    import math
    return int(math.floor(x))


def loader_tt(bd):
    """lines 105-192 of the loader: returns (TT dict, slow, turbo)"""
    assert 300 <= bd <= 38400 and not (28800 < bd < 38400)
    tt = {7: 0, 8: 0, 9: 0, 10: 30, 11: 130, 12: 1, 13: 1, 21: 0}
    b = 3000000.0 / bd
    sl = 1 if bd < 750 else 0
    tu = 1 if bd == 38400 else 0
    if tu:
        tt.update({1: 1, 2: 8, 3: 1, 4: 8, 5: 1, 6: 8})
        return tt, sl, tu

    def sub(q):
        u = floor((q + 3) / 4)
        if u < 1:
            u = 1
        v = 4 * u - q
        if v > 8:
            v = 8
        return u, v
    if sl == 0:
        tt[1], tt[2] = sub(floor((b - 100) / 4 + .5))
        tt[3], tt[4] = sub(floor((b - 99) / 4 + .5))
        tt[5], tt[6] = sub(floor((b / 2 - 105) / 4 + .5))
    else:
        m = floor((b - 92) / 4000) + 1
        tt[13] = m; tt[2] = tt[4] = tt[6] = 4
        tt[1] = floor(((b - 92) / m - 15) / 16 + .5)
        tt[3] = floor(((b - 91) / m - 15) / 16 + .5)
        tt[5] = floor(((b / 2 - 97) / m - 15) / 16 + .5)
    return tt, sl, tu


def make_image(lines, bd, A=49349, P=0xC0C5, restore=0):
    """the code as the loader builds it for the speed bd"""
    tt, sl, tu = loader_tt(bd)
    tt[21] = restore                                     # menu 5 of the loader sets TT(21)
    for k, w in ((14, P + 256), (16, P + 512)):         # lines 430 and 440 of the loader
        tt[k] = w & 255; tt[k + 1] = (w >> 8) & 255
    for k in (1, 3, 5):
        assert 1 <= tt[k] <= 255, ('N out of range', bd, tt)
    for k in (2, 4, 6):
        assert 0 <= tt[k] <= 8, ('D out of range', bd, tt)
    img = bytearray(0x300)
    for off, bs in read_set(lines, 2000 if sl else 1000) + read_set(lines, 3000):
        img[off:off + len(bs)] = bytes(bs)
    st = Stream(lines, 910 if sl else 900)
    n = st.read()
    for _ in range(n):
        o, k, f = st.read(3)
        v = tt[k] * f
        assert 0 <= v <= 255
        img[o] = v
    if tu:
        t = Stream(lines, 940)
        for _ in range(4):
            o, d = t.read(2)
            img[o] = 24; img[o + 1] = d
    return img, tt, sl, tu


class Rig:
    def __init__(self, img, entry, count, P, txdata=b'', wave=None, wait=0):
        m = self.m = z80.Z80Machine()
        mem = bytearray(65536)
        mem[P:P + len(img)] = img
        mem[P + 256:P + 256 + len(txdata)] = txdata
        stub = 0x0100
        e = P + entry
        mem[stub:stub + 7] = bytes([0x11, count & 255, 0, 0xCD, e & 255, e >> 8, 0x76])
        m.set_memory_block(0, bytes(mem))
        m.pc = stub; m.sp = 0xF000
        self.off = 0; self.last = 0
        self.outs = []; self.ins = []
        self.wave = wave; self.wait = wait
        self.opc = 0x5A                      # the OPC register: readable, returns the last value written
        m.set_output_callback(self.on_out); m.set_input_callback(self.on_in)

    def now(self):
        ft = self.m.frame_tick
        if ft < self.last - FRAME // 2:
            self.off += FRAME
        self.last = ft
        return self.off + ft

    def on_out(self, a, v):
        if a & 255 == 0x18:
            self.opc = v
            self.outs.append((self.now(), v))

    def on_in(self, a):
        if a & 255 == 0x1F:
            t = self.now(); self.ins.append(t)
            return 0x04 if self.wave(t) else 0
        if a & 255 == 0x18:
            return self.opc
        return 0xFF

    def run(self, limit=80_000_000):
        self.m.ticks_to_stop = limit
        for _ in range(limit // FRAME):
            self.m.run()
            if self.m.halted:
                return
        raise RuntimeError('did not finish')


def waveform(data, bit, start=20000, phase=0.0, gap=0.0, pol=0):
    seg = []; t = start + phase
    for b in data:
        for lv in [0] + [(b >> k) & 1 for k in range(8)] + [1]:
            seg.append((t, lv)); t += bit
        t += gap * bit
    seg.append((t, 1))
    ts = [s[0] for s in seg]; ls = [s[1] for s in seg]
    def f(tick):
        i = bisect.bisect_right(ts, tick) - 1
        return (1 if i < 0 else ls[i]) ^ pol
    return f


def decode(outs, bit, pol=0):
    """ideal receiver: finds the falling edges and samples at 1.5, 2.5... bit times"""
    ev = [(t, ((v >> 7) & 1) ^ pol) for t, v in outs]
    def at(t):
        cur = 1
        for tt, l in ev:
            if tt <= t:
                cur = l
            else:
                break
        return cur
    res = []; prev = -1
    while True:
        e = next((tt for tt, l in ev if tt > prev and l == 0 and at(tt - 1) == 1), None)
        if e is None:
            break
        v = sum(at(e + (1.5 + k) * bit) << k for k in range(8))
        res.append((v, at(e + 9.5 * bit)))
        prev = e + 9.6 * bit
    return res


def probe_tests(lines):
    """the search routine PROBE: finds the Z-80 address of the code in a RAM full of garbage and decoys"""
    def pairs(first):
        st = Stream(lines, first)               # 8 x (value, site), then the two sites
        pr = [tuple(st.read(2)) for _ in range(8)]
        return pr, st.read(), st.read()
    good = True
    rnd = random.Random(3)
    for P in (0xC0C5, 0xE1C5, 0xFC05, 0x8EC5, 0xB005):
        res = []
        for first in (920, 930):
            sig, su, sv = pairs(first)
            img, tt, sl, tu = make_image(lines, 19200, P=P)
            for v, o in sig:
                img[o] = v
            found = 0
            for G in (192, 128):
                if found:
                    break
                im = bytearray(img); im[su] = G; im[sv] = 64
                mem = bytearray(rnd.randrange(256) for _ in range(65536))
                for k, q in enumerate((0xD321, 0xE777, 0x9A00, 0xC400, 0xF123, 0xCC00, 0x8800)):
                    for j in range(k + 1):
                        mem[q + j] = sig[j][0]
                mem[P:P + len(im)] = im
                for j, (v, o) in enumerate(sig):
                    mem[P + 256 + j] = v
                m = z80.Z80Machine()
                e = P + 0x200
                mem[0x100:0x107] = bytes([0x11, 0, 0, 0xCD, e & 255, e >> 8, 0x76])
                m.set_memory_block(0, bytes(mem)); m.pc = 0x100; m.sp = 0xF000
                m.ticks_to_stop = 5_000_000
                for _ in range(50):
                    m.run()
                    if m.halted:
                        break
                found = m.de
            res.append(found)
        okp = res[0] == P and res[1] == P
        print('PROBE finds the code at %04X (garbage RAM, decoys): %s' % (P, 'ok' if okp else 'FAILED %r' % (res,)))
        good &= okp
    return good


def opc_test(lines):
    """SEROUT with TT(21) = 1 puts OPC (the buzzer / TX register) back to the value it had before"""
    ok = True
    for bd in (300, 2400, 19200, 38400):
        for rs in (1, 0):
            img = make_image(lines, bd, restore=rs)[0]
            r = Rig(img, 0, 3, 0xC0C5, txdata=b'ABC'); r.opc = 0x5A; r.run()
            want = 0x5A if rs else 0x80
            good = r.opc == want and r.m.sp == 0xF000
            print('OPC after SEROUT, %5d bps, TT(21)=%d: %02X (expected %02X) %s' % (bd, rs, r.opc, want, 'ok' if good else 'FAILED'))
            ok &= good
    return ok


def check_speed(lines, bd, quick=False):
    img, tt, sl, tu = make_image(lines, bd)
    P = 0xC0C5
    bit = FCLK / bd
    random.seed(7)
    data = bytes(random.randrange(256) for _ in range(30))
    r = Rig(img, 0, len(data), P, txdata=data)
    r.m.alt_bc = 0x1234; r.m.alt_de = 0x5678; r.m.alt_hl = 0x9ABC; r.m.bc = 0x0F0E; r.m.hl = 0x2468
    r.run()
    regs_ok = (r.m.alt_bc, r.m.alt_de, r.m.alt_hl, r.m.bc, r.m.hl) == (0x1234, 0x5678, 0x9ABC, 0x0F0E, 0x2468)
    dec = decode(r.outs, bit)
    spac = sorted({r.outs[i + 1][0] - r.outs[i][0] for i in range(len(r.outs) - 1)})
    txok = bytes(v for v, s in dec) == data and all(s == 1 for v, s in dec)
    txtol = [m for m in (0.96, 0.98, 1.0, 1.02, 1.04) if
             (lambda d: bytes(v for v, s in d) == data and all(s == 1 for v, s in d))(decode(r.outs, bit * m))]
    rx = []; tot = 0; good = 0
    for ph in ((0, 20, 40) if quick else range(0, 46, 5)):
        for gap in (0, 2):
            r = Rig(img, SERIN, 255, P, wave=waveform(data[:20], bit, phase=ph, gap=gap))
            r.m.alt_bc = 0x1234; r.m.alt_de = 0x5678; r.m.alt_hl = 0x9ABC; r.m.bc = 0x0F0E; r.m.hl = 0x2468
            r.run()
            regs_ok &= (r.m.alt_bc, r.m.alt_de, r.m.alt_hl, r.m.hl) == (0x1234, 0x5678, 0x9ABC, 0x2468)
            n = r.m.memory[P + 0x1FF]
            good += bytes(r.m.memory[P + 0x200:P + 0x200 + n]) == data[:20]; tot += 1
    tol = []
    for m in (0.96, 0.98, 1.0, 1.02, 1.04):
        o = t = 0
        for ph in (0, 15, 30):
            r = Rig(img, SERIN, 255, P, wave=waveform(data[:20], bit * m, phase=ph, gap=0)); r.run()
            n = r.m.memory[P + 0x1FF]
            o += bytes(r.m.memory[P + 0x200:P + 0x200 + n]) == data[:20]; t += 1
        tol.append('x%.2f:%d/%d' % (m, o, t))
    mode = 'turbo' if tu else ('slow' if sl else 'fast')
    ttl = [tt[k] for k in range(1, 7)] + ([tt[13]] if sl else [])
    print('%5d bps %-5s TT=%s bit %.0f T (TX %s): TX %s, TX-tol %s | RX %d/%d | RX-tol %s | registers kept: %s' % (
        bd, mode, ttl, bit, spac, 'ok' if txok else 'FAILED', txtol, good, tot, ' '.join(tol), 'yes' if regs_ok else 'NO'))
    return txok and good == tot and regs_ok


def main():
    path = sys.argv[1] if len(sys.argv) > 1 else os.path.join(ROOT, 'Basic', 'uart1600.txt')
    only = [int(x) for x in sys.argv[2:]]
    lines = load_listing(path)
    ok = True
    # every speed that the loader accepts must give legal values (N 1..255, D 0..8)
    bad = []
    for bd in list(range(300, 28801)) + [38400]:
        try:
            tt, sl, tu = loader_tt(bd)
            if not tu:
                assert all(1 <= tt[k] <= 255 for k in (1, 3, 5)) and all(0 <= tt[k] <= 8 for k in (2, 4, 6)), tt
        except AssertionError as e:
            bad.append((bd, e))
    print('every speed 300-28800 and 38400 gives legal timing values:', 'yes' if not bad else 'NO %r' % bad[:3])
    ok &= not bad
    for bd in (only or (300, 600, 749, 750, 1200, 2400, 4800, 9600, 14400, 19200, 28800, 38400)):
        good = check_speed(lines, bd, quick=bd < 1000)
        if bd >= 28800:
            print('        (from 28800 bps the simulation is only informative: the real PC-1600 timing decides)')
        else:
            ok &= good
    ok &= probe_tests(lines)
    ok &= opc_test(lines)
    r = Rig(make_image(lines, 19200)[0], SERIN, 3, 0xC0C5, wave=waveform(b'ABCDE', FCLK / 19200)); r.run()
    print('RX limit of 3 bytes:', bytes(r.m.memory[0xC0C5 + 0x200:0xC0C5 + 0x203]), 'DE =', r.m.de)
    ok &= r.m.de == 3
    # idle line: the 30 s timeout is too long to simulate, so it is shortened by patching the timeout bytes
    img = make_image(lines, 19200)[0]
    tab = Stream(lines, 900); n = tab.read(); sites = {}
    for _ in range(n):
        o, k, f = tab.read(3); sites[k] = o
    img[sites[9]] = 10; img[sites[10]] = 1
    r = Rig(img, SERIN, 255, 0xC0C5, wave=lambda t: 1); r.run()
    print('RX idle line: timeout after %.3f s, DE=%d, carry=%d' % (r.ins[-1] / FCLK, r.m.de, r.m.f & 1))
    ok &= r.m.de == 0 and r.m.f & 1 == 1
    print('ALL OK' if ok else 'FAILED')
    sys.exit(0 if ok else 1)


if __name__ == '__main__':
    main()
