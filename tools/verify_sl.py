#!/usr/bin/env python3
"""Emulator test of Basic/uart1600_sl.txt (the Saver/Loader): stores the code as the BASIC installer does, then runs SAVE
and LOAD against an ideal 19200 bps partner, with a sample program in a memory that has the BASIC pointers at FE3CH/FE3FH.
The code is read from the DATA lines (z80asm is not needed). pip install z80
Usage: python3 tools/verify_sl.py"""
import os, random, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import verify_listing as V
ROOT = V.ROOT
lines = V.load_listing(os.path.join(ROOT, 'Basic', 'uart1600_sl.txt'))
st = V.Stream(lines, 1000)
n = st.read(); img = bytearray(0x200); tot = 0
for _ in range(n):
    o, l = st.read(2)
    bs = st.read(l); assert all(0 <= x <= 255 for x in bs)
    img[o:o + l] = bytes(bs); tot += sum(bs)
assert st.read() == tot, 'checksum of the DATA lines'
code = bytes(img)
lab = {'SAVE': 0, 'LOAD': 176, 'sv_fin': 0x74, 'ld_fin': 0x173}
P = 0xC0C5
img = bytearray(code)
rnd=random.Random(5)
prog=bytearray(bytes([0,10,70,241,171,32])+b'MYPROG.TEST REM TEXT'+b'\r')
for n in range(20,400,10): prog+=bytes([n>>8,n&255,12])+bytes(rnd.randrange(32,126) for _ in range(10))+b'\r'
prog[100]=0xFF; prog[101]=0xFF; prog[102]=0
START=0x80C5; END=START+len(prog)
def setup(r, pb=prog, start=START, sentinel=True):
    mem=r.m.memory
    mem[start:start+len(pb)]=bytes(pb)
    mem[0xFE3C]=start&255; mem[0xFE3D]=start>>8; mem[0xFE3E]=4
    e=start+len(pb); mem[0xFE3F]=e&255; mem[0xFE40]=e>>8; mem[0xFE41]=4
bit=V.FCLK/19200
print('code from the DATA lines; program %d bytes'%len(prog))
r=V.Rig(img,lab['SAVE'],0,P); setup(r); r.opc=0x80
r.m.alt_bc=0x1234; r.m.alt_de=0x5678; r.m.alt_hl=0x9abc; r.m.bc=0x0f0e; r.m.hl=0x2468; r.m.af=0x1122
r.run()
dec=V.decode(r.outs,bit); got=bytes(v for v,s in dec)
exp=bytes(prog)+bytes([sum(prog)&255])
print('SAVE: sent %d bytes (expected %d): %s; stop bits ok %s; DE=%d; OPC %02X; SP %04X'%(len(got),len(exp),got==exp,all(s==1 for v,s in dec),r.m.de,r.opc,r.m.sp))
stream=exp
def load(data,start=START,newprog=bytes(40),mult=1.0,gap=0,phase=0.0,shorten=False,regs=False):
    r=V.Rig(img,lab['LOAD'],0,P,wave=V.waveform(data,bit*mult,phase=phase,gap=gap)); setup(r,newprog,start)
    if regs:
        r.m.alt_af=0x3344; r.m.alt_bc=0x1234; r.m.alt_de=0x5678; r.m.alt_hl=0x9abc; r.m.bc=0x0f0e; r.m.hl=0x2468
    if shorten:
        i=P+lab['LOAD']; j=bytes(r.m.memory[i:i+60]).find(bytes([0x0E,0x00,0x16,30])); r.m.memory[i+j+3]=1
    r.run(); mem=r.m.memory
    return r, mem[0xFE3F]+256*mem[0xFE40]
ok=True
for ph,gap,mult in ((0,0,1.0),(20,0,1.0),(40,0,1.0),(10,0,0.98),(10,0,1.02),(30,0,1.03),(0,3,1.0)):
    r,pe=load(stream,phase=ph,gap=gap,mult=mult,regs=True)
    good=r.m.de==1 and pe==END and bytes(r.m.memory[START:START+len(prog)])==bytes(prog)
    regs=(r.m.alt_af,r.m.alt_bc,r.m.alt_de,r.m.alt_hl,r.m.bc,r.m.hl)==(0x3344,0x1234,0x5678,0x9abc,0x0f0e,0x2468)
    print('LOAD phase %2d gap %d speed x%.2f: status %d, end %04X (expected %04X), memory %s, registers kept %s'%(ph,gap,mult,r.m.de,pe,END,'ok' if good else 'WRONG',regs))
    ok&=good
bad=bytearray(stream); bad[-1]^=0x55
r,pe=load(bytes(bad)); print('wrong checksum: status',r.m.de,'(expect 2); end pointer unchanged',pe==START+40)
r,pe=load(b'',shorten=True); print('nothing received: status',r.m.de,'(expect 3)')
r,pe=load(bytes([7])); print('one byte: status',r.m.de,'(expect 2)')
r,pe=load(bytes(stream),start=0xBF00,newprog=bytes(40)); print('does not fit (start BF00): status',r.m.de,'(expect 4)')
r,pe=load(bytes([5,5]),newprog=bytes(40)); print('empty program with the checksum byte (2 bytes 5,5): status',r.m.de,'end',hex(pe))

print('ALL OK' if ok else 'FAILED')
sys.exit(0 if ok else 1)
