# UART1600: Software UART for Sharp PC-1600

**UART1600: SEROUT and SERIN routines** for the Sharp PC-1600 pocket computer, a Z-80 machine-language
bit-bang UART (8N1, **300 to 38400 bps**) on two lines of the 60-pin system connector (CN9), without the RS-232C port and
without any modification of the computer. It is the PC-1600 relative of
[UART7.3 for the PC-1500(A)](https://github.com/Ante1000/Sharp_PC-1500_Software_UART): the same `CALL` interface
(one `CALL` sends, one `CALL` receives, packet mode, buffers filled with `POKE`/`PEEK`) and the same pins, PC7 and PB2.

> ## Status
> Tried on a real PC-1600 (MODE 0) with a USB-UART (TTL) and a terminal program, in both directions (TX and RX):
> **2400, 19200, 28800 and 38400 bps**. The other speeds (300, 600, 1200, 4800, 9600, 14400 ...) are calculated by the loader
> with the same formulas and were checked in a Z-80 emulator (`tools/verify_listing.py`), not on the machine.
> Not yet tried: long transfers, signal levels with devices other than a TTL USB-UART, the effect of the unknown port C bits.
> **Do not connect anything but a CMOS/TTL level device.**
>
> **The buzzer shares the line of pin 29.** Every `BEEP` (and every error beep) sends a ~4 kHz tone to the TX pin,
> which a terminal shows as garbage such as `ĆĆĆÎÎÎ`. Use `BEEP OFF` in programs that use the UART (the loader and the
> test program do).
> The CPU of the PC-1600 was measured at about 3.0 MHz (2.9-3.06 MHz in two loops timed with a stopwatch), not 3.58 MHz,
> so all timing values are for 3.0 MHz.

## What it is

- **SEROUT** (send) and **SERIN** (receive) in about 200 bytes of Z-80 code, plus two 255-byte buffers (TX, RX).
- **300 to 38400 bps**, format **8N1** (8 data bits, no parity, 1 stop bit), no handshake. The loader asks for the speed
  (`BAUD 300-38400`), calculates the delay values from it and patches them into the code; see [Speeds](#speeds).
- Packet mode like UART7.3: SEROUT sends `m` bytes from the TX buffer; SERIN waits up to about 30 s for the first byte
  and returns when no further byte arrives within about 0.5 s (or when the buffer count is reached).
- An inversion option (`TT(7)`, `TT(8)`) for devices with inverted levels.
- The code has no `CALL`s and no absolute jumps, so it runs at any address; only the buffer addresses are patched by
  the loader.

## Connection

The UART uses the **60-pin system connector CN9** (the "system bus" connector of the PC-1600):

| Signal | CN9 pin | Port | Use |
|---|---|---|---|
| CMTout | **29** | PC7 (`OUT 18H`, bit 7) | **TX**: PC-1600 output |
| CMTin | **27** | PB2 (`IN 1FH`, bit 2) | **RX**: PC-1600 input |
| GND | 52, 53, 54 | | ground, connect to the ground of the other device |

Pins 27 and 29 are the cassette (CMT) lines of the CE-1600P interface, so they are free only while the CE-1600P is not
used. According to the PC-1600 schematic and the Technical Reference the levels are CMOS (about 0 V to the logic supply,
4.0 to 4.7 V). **This is NOT RS-232.** The 15-pin RS-232C connector (CN4) is not used by UART1600; its levels are
±6 V ... ±8.5 V and are of no use here.

- Connect TX of one side to RX of the other and vice versa, through resistors of 1 to 10 kΩ, and join the grounds.
- A device with 3.3 V logic (ESP32 and the like) can drive PB2 directly if its high level reaches the input threshold
  of PB2 (not measured); in the PC-1600 -> 3.3 V direction use a divider (e.g. 47 kΩ in series and 100 kΩ to GND),
  because the output swings up to about 4.5 V.
- A USB-UART adapter with TTL levels (FTDI cable, CP2102, CH340) works as the partner, with the terminal at the speed chosen in the loader, 8N1.
- Pin 11 and pin 41 of CN9 are VCC, 39 is VGG, 45/46 are VBAT: **do not short them**. Use a 1.27 mm dual-row header
  (2 x 30 pins) or an adapter; the pin numbering is that of the PC-1600 service documents.

## Files

| File | Contents |
|---|---|
| `Basic/uart1600.txt` | **UART1600**: one BASIC program: installs SEROUT/SERIN in RAM (speed 300-38400 bps), finds the right addresses, patches the timing, and has a **TX/RX test menu** |
| `wav/uart1600.wav` | tape recording of the program for `LOAD "CAS:UART1600"` (about 5 minutes) |
| `tools/pc1600_bas2wav.py` | BASIC text -> tape recording (WAV) and decoder check (from the PC-1600 game repository); lines must not exceed 80 characters |
| `asm/uart1600.asm` | source of SEROUT, SERIN and PROBE (assembler: `z80asm`) with all comments |
| `tools/build_loader.py` | makes `Basic/uart1600.txt` from `asm/uart1600.asm` (needs `z80asm`) |
| `tools/verify_listing.py` | emulator test of the loader listing (needs `pip install z80` and `z80asm`) |

## How to use

1. Switch the PC-1600 to **MODE 0** (the default) and to RUN mode. The BASIC addresses of `POKE`, `PEEK` and `CALL` are
   the Z-80 addresses. According to the memory map of the PC-1600 manual (Appendix D, bank 0, internal RAM seen from the Z-80):
   `C000H`-`C007H` header, `C008H`-`C0C4H` reserve program area, **from `C0C5H` the machine-language area** (its size is set
   with `NEW`), then the BASIC program area and the variables up to `EFFFH`, and `F000H`-`FFFFH` the **work area of the
   system: never write there**. UART1600 puts its 767 bytes at **`C0C5H` = 49349** (up to `C3C3H`), so the machine-language area
   must be at least 767 bytes long. On the machine used for the tests `STATUS 2` showed 6342, which suggests that about
   6 KB are reserved already (197 + 6145); check it yourself with `POKE &C0C5,123 : PRINT PEEK(&C0C5)` on an empty
   machine before loading anything (`NEW` without an address keeps the reservation). The low addresses (197, 27000) are ROM:
   `POKE` does nothing there. `NEW <address>` gave errors 25 and 26 on that machine (25: invalid address, 26: invalid mode:
   wrong mode or not in RUN mode), see below.
2. Load `Basic/uart1600.txt`: from tape (CE-1600P, EAR input; MODE 0: `LOAD "CAS:UART1600"`, play `wav/uart1600.wav`), through the serial port, or type it in. The recordings were only checked with the decoder of the tool, not on a real PC-1600.
   The listing has about 150 lines of at most 80 characters; the code is in `DATA` lines and has a checksum.
3. `RUN`. The loader asks `BAUD 300-38400 (ENTER=19200)` (any value from 300 to 28800, or 38400; ENTER = 19200) and then
   `ML ADDRESS (ENTER=49349)`: ENTER takes &C0C5. It then
   - stores the code with `POKE`s and checks the `DATA` checksum (`DATA ERROR` = a typing or transfer error),
   - **finds the Z-80 address** that belongs to the BASIC address (see below),
   - calculates the timing for the speed and patches the buffer addresses, the timing and the polarity (tables in lines 900 to 940),
   - checks that the code reads the buffer where BASIC wrote its marker, and shows
     `UART1600 READY, <speed> BPS`, the `CALL` addresses and the buffer addresses.
4. Connect the other device as shown above.
5. After the installation the **test menu** appears (`1TX 2RX 3U 4BAUD 0END`):
   - `1` sends `HELLO PC-1600` with CR LF to the terminal,
   - `2` receives packets and shows them (with `ECHO (1=YES)` it sends every packet back); after 30 s without data it
     returns to the menu,
   - `3` sends 255 bytes of 55H again and again (a square wave, one bit per half period, for an oscilloscope; stop with BREAK),
   - `4` goes back to the question about the speed and installs the code again for the new speed (the ML address is not asked
     again), then the menu appears again,
   - `0` ends the program; the routines stay in memory.
6. The routines stay in memory (RAM at `&C0C5`) for your own programs until the memory is cleared or something else writes there.

To change the speed or the polarity, use `4` in the menu, or run the program again with `RUN` (the variables in lines 111 to 114). The routines are lost when the memory is cleared
(ALL RESET, or when something overwrites the area).

## Commands

Addresses for the default start address `A` = 49349 (the loader shows them for your address; all offsets are from `A`):

| Command | Action |
|---|---|
| `CALL A, N` | **send** `N` bytes (1 to 255) from the TX buffer |
| `CALL A+80, N` | **receive** at most `N` bytes (0 = 255) into the RX buffer; the number received is returned in `N` |

| Address | Contents |
|---|---|
| `A+256` | TX buffer: put the data with `POKE A+256, 72, 69, ...` |
| `A+512` | RX buffer: read the data with `PEEK` |
| `A+511` | length of the last received packet |
| `A+250` ... `A+254` | description written by the loader: 85 (`"U"`), Z-80 address of `A` low, high, speed (bps) low, high |
| `A+512` ... `A+591` | the search routine PROBE: the loader needs it only once; the first received packet overwrites it |

- **Always pass a variable** (`CALL A,N`). A `CALL` without a variable leaves DE undefined and SEROUT would send a
  random number of bytes. `N = 0` sends one byte.
- SERIN returns `N` = number of bytes received; 0 means that nothing arrived in about 30 s.
- **While SEROUT and SERIN run, the interrupts are off** (the bit timing would be disturbed): the keyboard, `BREAK` and the
  clock do not respond. SERIN waits up to about 30 s for the first byte. Both routines end with `EI` (the routines
  assume that BASIC runs with the interrupts on).
- SEROUT sends one idle (high) bit before the first start bit, so the first start bit is always a falling edge. Between
  the bytes the stop bit is about 0.2 bit longer than 1 bit (harmless for a UART receiver).
- Byte order: the bytes go out in the order of the buffer, least significant bit first.

Example (text to a terminal):

```
10 A=49349 : POKE A+256, 72, 69, 76, 76, 79, 13, 10
20 N=7 : CALL A, N
```

Example (receive a packet and print it):

```
10 A=49349 : N=0 : CALL A+80, N
20 FOR J=0 TO N-1 : PRINT CHR$(PEEK(A+512+J)); : NEXT J
```

## How the loader finds the addresses

The code needs the buffer addresses as Z-80 addresses, but BASIC (`POKE`, `PEEK`, `CALL`) uses the addresses of
the memory block 0. The addresses of the internal RAM are the Z-80 addresses (`C000H` ...), but the loader does not rely on it, and checks:

1. It stores the small routine **PROBE** at `A+512` (in the RX buffer, which is free at load time) and `POKE`s an 8-byte
   signature into the TX buffer (`A+256`).
2. PROBE searches the Z-80 memory for that signature (first the internal RAM `C000H`-`FFFFH`, then `8000H`-`BFFFH`; the
   search takes about 0.1 s) and returns in `X` the Z-80 address of the code (the address of the signature minus 256).
   It only **reads** memory. The signature is held in PROBE as the operands of `CP` instructions, so PROBE does not
   find its own copy.
3. The search is repeated with a second signature and both answers must agree, otherwise `Z-80 ADDRESS UNSURE`.
4. If nothing is found, the loader asks for the Z-80 address of the ML area (`Z-80 ADDRESS OF ML AREA?`, decimal).

The Z-80 address is returned as a number that may be shown as negative above 32767; the loader corrects that
(`IF X<0 THEN LET X=X+65536`), which assumes that the `CALL` returns DE as a signed 16-bit number.
The search was tested in the emulator in RAM full of garbage and with partial copies of the signature as decoys.

## Speeds

The loader asks for the speed and calculates the timing in CPU T-states (the Z-80 of the PC-1600 runs at about **3.0 MHz**,
so one bit = 3,000,000 / bps T-states, e.g. 156 T-states at 19200 bps). It works with the variables `TT(1)` ... `TT(17)`
(lines 105 to 192 of the loader; you can change them in the listing, for example the polarity in line 111):

| Variable | Meaning |
|---|---|
| `TT(1)`, `TT(2)` | TX bit = 100 + 16 `TT(1)` - 4 `TT(2)` T-states (fast code; `TT(2)` = 0 ... 8) |
| `TT(3)`, `TT(4)` | RX bit = 99 + 16 `TT(3)` - 4 `TT(4)` T-states |
| `TT(5)`, `TT(6)` | check of the start bit after 83 + 16 `TT(5)` - 4 `TT(6)` T-states from its detection |
| `TT(7)`, `TT(8)` | polarity of TX and RX: 0 normal (idle high), 1 inverted |
| `TT(9)`, `TT(10)` | wait for the first byte: 45 x 256 x `TT(9)` x `TT(10)` T-states (`TT(9)` = 0 means 256); 30 s |
| `TT(11)`, `TT(12)` | end of the packet: 45 x 256 x `TT(11)` x `TT(12)` T-states; 0.5 s |
| `TT(13)` | repeat count M of the delays in the slow code (bit = about 108 + M (15 + 16 `TT(1)`) - 4 `TT(2)` for TX) |
| `TT(14)` ... `TT(17)` | the buffer pointers (low and high bytes), calculated by the loader |

**Code variants** (made from the same source, `asm/uart1600.asm`):
- **fast** (750 to 28800 bps): the loops of the table above; the loader calculates `TT(1)` ... `TT(6)` from the speed.
  Examples: 2400 bps = 72, 0, 72, 0, 33, 2; 9600 bps = 14, 3, 14, 3, 4, 3; 19200 bps = 4, 2, 4, 2, 1, 8; 28800 bps = 1, 3, 1, 3, 1, 8.
- **slow** (300 to 749 bps): every delay is repeated M times in an `EXX` loop (the loop of the fast code is limited to
  about 4000 T-states per bit, i.e. 750 bps). The alternate registers BC' and DE' are saved and restored.
- **38400 bps ("turbo")**: the fast code with four `JR` instructions in place of the delay loops (a bit of 82 T-states, 4 `NOP`s).
  The check of the start bit is left out in this mode, so noise on the line can produce false bytes. `TU` in the loader.

### Checking and adjusting the speed

The clock was measured with two machine-language loops, 10 calls each, timed with a stopwatch: a loop of `dec bc / ld a,b / or c / jr nz`
(17.04 million T-states) took 5.9 s = 2.89 MHz, and a loop of `out (18H),a / djnz` (15.8 million T-states) took 5.15 s = 3.06 MHz.
With the interrupts off, `OUT` is not slower than the other instructions. The accuracy is about 5 %, so a bit may be
one step of `TT(2)` or `TT(4)` (4 T-states, 2.6 % at 3 MHz) off. To adjust:

1. TX: test menu `1` (`HELLO PC-1600`) or `3` (a stream of 55H bytes: a square wave, one bit = half a period, for an oscilloscope
   the target at 19200 bps is 52.1 µs) with the terminal at the same speed; step `TT(2)` up and down.
2. RX: send text from the terminal (menu `2`); do the same with `TT(4)`, `TT(5)` and `TT(6)`.
3. At 38400 bps the length of the bit is set by the displacement bytes of the `JR` instructions (lines 940 / `TURBO_J` in
   `tools/build_loader.py`): the value 9 in the first and third POKE gives 82 T-states; 10 gives 78 and 8 gives 86.
4. You can also poke the new bytes directly (the code is at `A` = 49349) to try a setting without loading the loader again.

### Tolerance (simulation)

In the emulator (3.0 MHz) the receiver reads correctly when the partner is between about -2 % and +4 % off the nominal speed
and the sender is read correctly by an ideal receiver between about -4 % and +4 %, at all speeds up to 19200 bps
(see the output of `tools/verify_listing.py`). From 28800 bps the simulation is only informative; the machine decides.

## What is not known, and how the loader deals with it

| Open point | What the code does |
|---|---|
| Z-80 timing of the PC-1600 | about 3.0 MHz (measured with a stopwatch, +-5 %); the loader calculates the timing, see Speeds |
| Z-80 address of the BASIC address `A` | PROBE searches for a signature in the RAM (read only), manual input as a fallback |
| Port C | SEROUT writes the whole OPC register (18H): bit 7 = TX, **all other bits 0**. Bit 6 of OPC is inverted PC6 = "beep disable" according to the Technical Reference, written as 0. It is not known what the other bits of port C do on the PC-1600 and whether PC7 clicks the buzzer. If another bit matters, change `and 0x80` in `asm/uart1600.asm` |
| Levels and buffers at CN9 pins 27 and 29 | the schematic shows no buffer, but this is unchecked on a real machine |
| Interrupts | `DI` at the start, `EI` at the end |
| `CALL` register contract of the PC-1600 BASIC | the manual: the numeric variable goes in DE, and comes back in DE if the carry is set. SERIN and PROBE set the carry; SEROUT clears it and keeps AF, BC, DE and HL; SERIN keeps AF, BC and HL. Alternate registers and IX, IY are not used |
| The machine-language area at `C0C5H` is at least 767 bytes long | to be checked with `STATUS 2` / `POKE` (see How to use); if it is shorter, the code overwrites the BASIC program |
| Whether memory reads of `8000H`-`FFFFH` have side effects | assumed not (reads of ordinary RAM, ROM and card windows) |

## Rebuilding

```
python3 tools/build_loader.py        # asm/uart1600.asm -> Basic/uart1600.txt (needs z80asm)
python3 tools/verify_listing.py      # emulator test of the listing (pip install z80)
```

## Author

I created UART1600 with help from Claude Code, from the concept of UART7.3 for the PC-1500(A) (which in turn is based
on the idea of the SAVER/LOADER program published in the Japanese magazine I/O, 1983). Comments, reports and ideas
are welcome: alex.f4vts@gmail.com.
