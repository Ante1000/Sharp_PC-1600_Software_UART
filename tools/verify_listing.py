#!/usr/bin/env python3
"""Checks Basic/uart1600.txt in a Z-80 emulator (pip install z80).

It does what the BASIC loader does (reads the DATA lines, stores the code at a trial address, patches the
pointers and the timing bytes from the tables of the listing, using the TT() values of lines 110-160) and
then runs SEROUT and SERIN against an ideal 19200 bps partner. The timing is counted in Z-80 T-states
(CPU 3.579545 MHz, standard Zilog timing, I/O cycles WITHOUT the extra wait state of the PC-1600 - the
sweep shows how much margin there is). This is a simulation: it says nothing about the real PC-1600.

Usage:  python3 tools/verify_listing.py [Basic/uart1600.txt] [baud]
"""
import bisect, os, random, re, sys
import z80

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
FCLK = 3579545.0
FRAME = 100000          # the emulator's run() returns every 100000 T-states


def load_listing(path):
    lines = {}
    for l in open(path).read().splitlines():
        m = re.match(r'(\d+) (.*)', l)
        lines[int(m.group(1))] = m.group(2)
    return lines


def datas(lines, first):
    """values of the DATA lines first, first+1... (or +10 steps) up to the next non-DATA line"""
    out = []
    for n in sorted(k for k in lines if k >= first):
        t = lines[n]
        if t.startswith('DATA '):
            out += [int(x) for x in t[5:].split(',')]
        elif t.startswith('REM'):
            continue
        else:
            break
    return out


def image(lines, tt):
    """the code image as the loader builds it (before the pointers are patched)"""
    d = datas(lines, 1000)
    nseg = d[0]; pos = 1; chunks = []; total = 0
    for _ in range(nseg):                 # the same order as the BASIC loader reads: offset, length, bytes
        off, ln = d[pos], d[pos + 1]; pos += 2
        chunks.append((off, d[pos:pos + ln])); pos += ln; total += sum(d[pos - ln:pos])
        assert all(0 <= x <= 255 for x in d[pos - ln:pos]), 'byte out of range'
    chk = d[pos]; assert pos + 1 == len(d), 'DATA left over'
    assert total == chk, 'checksum of the DATA lines'
    img = bytearray(0x300)
    for off, bs in chunks:
        img[off:off + len(bs)] = bytes(bs)
    t = datas(lines, 900)
    n = t[0]
    for i in range(n):
        o, k, f = t[1 + 3 * i:4 + 3 * i]
        img[o] = (tt[k] * f) & 255
    return img


def patch_pointers(lines, img, A, P):
    h = [int(x) for x in re.findall(r'H=A\+(\d+)', lines[430] + ':' + lines[440])]
    for site, w in ((h[0], P + 256), (h[1], P + 512)):
        img[site] = w & 255; img[site + 1] = (w >> 8) & 255


def timing(lines):
    tt = {}
    for n in (110, 120, 130, 140, 150, 160):
        for i, v in re.findall(r'TT\((\d+)\)=(\d+)', lines[n].split('REM')[0]):
            tt[int(i)] = int(v)
    return tt


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
        m.set_output_callback(self.on_out); m.set_input_callback(self.on_in)

    def now(self):
        ft = self.m.frame_tick
        if ft < self.last - FRAME // 2:
            self.off += FRAME
        self.last = ft
        return self.off + ft

    def on_out(self, a, v):
        if a & 255 == 0x18:
            self.outs.append((self.now(), v))

    def on_in(self, a):
        if a & 255 == 0x1F:
            t = self.now(); self.ins.append(t)
            return 0x04 if self.wave(t) else 0
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


def params(bit):
    """TT(1..6) for another speed (the formulas of the README): TX 100+16N-4D, RX 99+16N-4D, check 83+16N-4D"""
    def nd(target, base):
        q = round((target - base) / 4.0)
        n = max(1, -(-q // 4))
        return n, 4 * n - q
    tn, td = nd(bit, 100); rn, rd = nd(bit, 99); hn, hd = nd(bit / 2 - 22, 83)
    for v in (tn, rn, hn):
        assert 1 <= v <= 255, 'speed out of range'
    for v in (td, rd, hd):
        assert 0 <= v <= 8, 'speed out of range'
    return {1: tn, 2: td, 3: rn, 4: rd, 5: hn, 6: hd}


def probe_tests(lines, tt):
    """the search routine PROBE: finds the Z-80 address of the code in a RAM full of garbage and decoys"""
    def pairs(first):
        d = datas(lines, first)           # 8 x (value, site), then the two sites
        return [(d[2 * i], d[2 * i + 1]) for i in range(8)], d[16], d[17]
    good = True
    rnd = random.Random(3)
    for P, expect_second_range in ((0xC0C5, False), (0xE1C5, False), (0xFC05, False), (0x8EC5, True), (0xB005, True)):
        res = []
        for first in (920, 930):
            sig, su, sv = pairs(first)
            img = image(lines, tt)
            patch_pointers(lines, img, 197, P)
            for v, o in sig:
                img[o] = v
            found = 0
            for G in (192, 128):
                if found:
                    break
                im = bytearray(img); im[su] = G; im[sv] = 64
                mem = bytearray(rnd.randrange(256) for _ in range(65536))
                # decoys: the first 1..7 bytes of the signature elsewhere
                for k, q in enumerate((0xD321, 0xE777, 0x9A00, 0xC400, 0xF123, 0xCC00, 0x8800)):
                    for j in range(k + 1):
                        mem[q + j] = sig[j][0]
                mem[P:P + len(im)] = im
                for j, (v, o) in enumerate(sig):
                    mem[P + 256 + j] = v
                m = z80.Z80Machine()
                stub = 0x0100
                e = P + 0x200
                mem[stub:stub + 7] = bytes([0x11, 0, 0, 0xCD, e & 255, e >> 8, 0x76])
                m.set_memory_block(0, bytes(mem)); m.pc = stub; m.sp = 0xF000
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
    # signature not present: must answer 0
    sig, su, sv = pairs(920)
    img = image(lines, tt)
    for v, o in sig:
        img[o] = v
    img[su] = 192; img[sv] = 64
    mem = bytearray(rnd.randrange(256) for _ in range(65536))
    P = 0x0C00
    mem[P:P + len(img)] = img
    m = z80.Z80Machine(); e = P + 0x200
    mem[0x100:0x107] = bytes([0x11, 0, 0, 0xCD, e & 255, e >> 8, 0x76])
    m.set_memory_block(0, bytes(mem)); m.pc = 0x100; m.sp = 0xF000
    m.ticks_to_stop = 5_000_000
    for _ in range(50):
        m.run()
        if m.halted:
            break
    print('PROBE with no signature in the searched range answers 0:', m.de == 0)
    return good and m.de == 0


def main():
    path = sys.argv[1] if len(sys.argv) > 1 else os.path.join(ROOT, 'Basic', 'uart1600.txt')
    baud = float(sys.argv[2]) if len(sys.argv) > 2 else 19200.0
    bit = FCLK / baud
    lines = load_listing(path)
    tt = timing(lines)
    if baud != 19200.0:
        tt.update(params(bit))
        print('speed %d bps: TT(1..6) =' % baud, [tt[i] for i in range(1, 7)])
    A, P = 197, 0xC0C5
    img = image(lines, tt); patch_pointers(lines, img, A, P)
    random.seed(7)
    data = bytes(random.randrange(256) for _ in range(60))
    ok = True

    r = Rig(img, 0, len(data), P, txdata=data); r.run()
    dec = decode(r.outs, bit)
    spac = sorted({r.outs[i + 1][0] - r.outs[i][0] for i in range(len(r.outs) - 1)})
    good = bytes(v for v, s in dec) == data and all(s == 1 for v, s in dec)
    print('TX  60 bytes decoded correctly:', good, '| spacing of the bits (T-states):', spac, '(ideal %.1f)' % bit)
    ok &= good

    for ph in (0, 15, 30, 44):
        r = Rig(img, 0x50, 255, P, wave=waveform(data, bit, phase=ph, gap=0)); r.run()
        n = r.m.memory[P + 0x1FF]; got = bytes(r.m.memory[P + 0x200:P + 0x200 + n])
        g = got == data and r.m.de == n
        print('RX  60 bytes back to back, phase %2d T: %s (length %d, DE=%d)' % (ph, g, n, r.m.de))
        ok &= g

    print('RX  tolerance for the speed of the partner (20 bytes, 4 phases, gaps 0 and 3 bits):')
    for mult in (0.94, 0.96, 0.98, 1.0, 1.02, 1.04, 1.06):
        good = tot = 0
        for ph in (0, 11, 22, 33):
            for gap in (0, 3):
                r = Rig(img, 0x50, 255, P, wave=waveform(data[:20], bit * mult, phase=ph, gap=gap)); r.run()
                n = r.m.memory[P + 0x1FF]
                good += bytes(r.m.memory[P + 0x200:P + 0x200 + n]) == data[:20]; tot += 1
        print('    partner x%.2f: %d/%d' % (mult, good, tot))

    print('TX  tolerance for the speed of an ideal receiver (30 bytes):')
    for mult in (0.94, 0.96, 0.98, 1.0, 1.02, 1.04, 1.06):
        r = Rig(img, 0, 30, P, txdata=data[:30]); r.run()
        dec = decode(r.outs, bit * mult)
        print('    receiver x%.2f: %s' % (mult, bytes(v for v, s in dec) == data[:30] and all(s == 1 for v, s in dec)))
    # idle line: the 30 s timeout would take too long to simulate, so it is shortened here (TT(9)=10, TT(10)=1)
    tt2 = dict(tt); tt2[9] = 10; tt2[10] = 1
    img2 = image(lines, tt2); patch_pointers(lines, img2, A, P)
    r = Rig(img2, 0x50, 255, P, wave=lambda t: 1); r.run()
    print('RX  idle line: timeout after %.3f s (10*256*45 T expected), DE=%d, carry=%d' % (r.ins[-1] / FCLK, r.m.de, r.m.f & 1))
    ok &= r.m.de == 0 and r.m.f & 1 == 1
    print('    full timeouts: first byte %.1f s, end of packet %.2f s' % (45 * 256 * 256 * tt[10] / FCLK * (1 if tt[9] == 0 else tt[9] / 256), 45 * 256 * tt[11] * tt[12] / FCLK))
    r = Rig(img, 0x50, 3, P, wave=waveform(b'ABCDE', bit)); r.run()
    print('RX  limit of 3 bytes:', bytes(r.m.memory[P + 0x200:P + 0x203]), 'DE =', r.m.de)
    ok &= r.m.de == 3
    ok &= probe_tests(lines, tt)
    print('ALL OK' if ok else 'FAILED')
    sys.exit(0 if ok else 1)


if __name__ == '__main__':
    main()
