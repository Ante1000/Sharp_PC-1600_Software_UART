# UART1600: Software UART for Sharp PC-1600

**UART1600: SEROUT and SERIN routines** for the Sharp PC-1600 pocket computer, a Z-80 machine-language
bit-bang UART (8N1, **19200 bps**) on two lines of the 60-pin system connector (CN9), without the RS-232C port and
without any modification of the computer. It is the PC-1600 relative of
[UART7.3 for the PC-1500(A)](https://github.com/Ante1000/Sharp_PC-1500_Software_UART): the same `CALL` interface
(one `CALL` sends, one `CALL` receives, packet mode, buffers filled with `POKE`/`PEEK`) and the same pins, PC7 and PB2.

> ## Status: NOT TESTED ON A REAL PC-1600
> The code was assembled and run in a Z-80 emulator against an ideal 19200 bps partner (`tools/verify_listing.py`).
> It sends and receives correctly there. Nothing of it has been run on a PC-1600 yet, so the following is
> **unconfirmed** and may need changes (see [What is not known](#what-is-not-known-and-how-the-loader-deals-with-it)):
> the Z-80 timing of the real machine, the buzzer, the other bits of port C, the memory addresses and the signal
> levels at pins 27 and 29. **Do not connect anything but a CMOS/TTL level device.**

## What it is

- **SEROUT** (send) and **SERIN** (receive) in about 200 bytes of Z-80 code, plus two 255-byte buffers (TX, RX).
- **19200 bps only**, format **8N1** (8 data bits, no parity, 1 stop bit), no handshake. The timing is set by variables
  of the loader (`TT(1)` ... `TT(12)`), so other speeds can be added later; see [Other speeds](#other-speeds).
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
- A USB-UART adapter with TTL levels (FTDI cable, CP2102, CH340) works as the partner, with the terminal at 19200 baud, 8N1.
- Pin 11 and pin 41 of CN9 are VCC, 39 is VGG, 45/46 are VBAT: **do not short them**. Use a 1.27 mm dual-row header
  (2 x 30 pins) or an adapter; the pin numbering is that of the PC-1600 service documents.

## Files

| File | Contents |
|---|---|
| `Basic/uart1600.txt` | **UART1600 loader**: BASIC program that stores SEROUT/SERIN in RAM, finds the right addresses and patches the timing |
| `Basic/uart1600_test.txt` | TX/RX test (run it after UART1600) |
| `wav/uart1600.wav` | tape recording of the loader for `LOAD "CAS:UART1600"` (about 2.5 minutes) |
| `wav/uart1600_test.wav` | tape recording of the test program for `LOAD "CAS:UARTTEST"` (about 1 minute) |
| `tools/pc1600_bas2wav.py` | BASIC text -> tape recording (WAV) and decoder check (from the PC-1600 game repository); lines must not exceed 80 characters |
| `asm/uart1600.asm` | source of SEROUT, SERIN and PROBE (assembler: `z80asm`) with all comments |
| `tools/build_loader.py` | makes `Basic/uart1600.txt` from `asm/uart1600.asm` (needs `z80asm`) |
| `tools/verify_listing.py` | emulator test of the loader listing (needs `pip install z80` and `z80asm`) |

## How to use

1. Switch the PC-1600 to **MODE 0** (the default). Type `NEW 1000` [ENTER]. This reserves the machine-language area
   197 to 1000 (the manual of the PC-1600: `NEW <address>` reserves the addresses 197 to `<address>` for machine
   language). UART1600 needs 767 bytes from its start address (197 by default).
2. Load `Basic/uart1600.txt`: from tape (CE-1600P, EAR input; MODE 0: `LOAD "CAS:UART1600"`, play `wav/uart1600.wav`), through the serial port, or type it in. The recordings were only checked with the decoder of the tool, not on a real PC-1600.
   The listing has 82 lines of at most 95 characters; the code is in `DATA` lines and has a checksum.
3. `RUN`. The loader asks `ML ADDRESS (ENTER=197)`: ENTER if you did `NEW 1000`. It then
   - stores the code with `POKE`s and checks the `DATA` checksum (`DATA ERROR` = a typing or transfer error),
   - **finds the Z-80 address** that belongs to the BASIC address (see below),
   - patches the buffer addresses and the timing bytes from the variables in lines 110 to 160,
   - checks that the code reads the buffer where BASIC wrote its marker, and shows
     `UART1600 READY, 19200 BPS`, the `CALL` addresses and the buffer addresses.
4. Connect the other device as shown above.
5. To test, type `NEW` [ENTER] (without an address: the machine-language area stays), load
   `Basic/uart1600_test.txt` and `RUN` it. Menu: `1` sends `HELLO PC-1600`, `2` receives packets (optional echo),
   `3` sends 255 bytes of 55H again and again (a square wave of one bit per half period, for an oscilloscope).
6. When you no longer need the test, `NEW` again: the routines stay in memory for your own programs.

Run the loader again to change a variable (speed, polarity). The routines are lost when the memory is cleared
(`NEW 0`, ALL RESET, or a `NEW` with another address that moves the area).

## Commands

Addresses for the default start address `A` = 197 (the loader shows them for your address; all offsets are from `A`):

| Command | Action |
|---|---|
| `CALL A, N` | **send** `N` bytes (1 to 255) from the TX buffer |
| `CALL A+80, N` | **receive** at most `N` bytes (0 = 255) into the RX buffer; the number received is returned in `N` |

| Address | Contents |
|---|---|
| `A+256` | TX buffer: put the data with `POKE A+256, 72, 69, ...` |
| `A+512` | RX buffer: read the data with `PEEK` |
| `A+511` | length of the last received packet |
| `A+240` ... `A+242` | description written by the loader: 85 (`"U"`), Z-80 address of `A` low, high |
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
10 A=197 : POKE A+256, 72, 69, 76, 76, 79, 13, 10
20 N=7 : CALL A, N
```

Example (receive a packet and print it):

```
10 A=197 : N=0 : CALL A+80, N
20 FOR J=0 TO N-1 : PRINT CHR$(PEEK(A+512+J)); : NEXT J
```

## How the loader finds the addresses

The code needs the buffer addresses as Z-80 addresses, but BASIC (`POKE`, `PEEK`, `CALL`) uses the addresses of
the memory block 0, and in MODE 0 the manual only says that the addresses of the machine-language area are "relative to
the top of the memory". The mapping is not given in text form, so the loader does not assume it:

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

## Timing and the speed variables

The loader (lines 110 to 160) holds all the timing as variables, in CPU T-states (the Z-80 runs at 3.579545 MHz;
one bit at 19200 bps = 186.4 T-states):

| Variable | Meaning | 19200 bps |
|---|---|---|
| `TT(1)`, `TT(2)` | TX bit = 100 + 16 `TT(1)` - 4 `TT(2)` T-states (`TT(2)` = 0 ... 8) | 6, 2 (188 T) |
| `TT(3)`, `TT(4)` | RX bit = 99 + 16 `TT(3)` - 4 `TT(4)` T-states | 6, 2 (187 T) |
| `TT(5)`, `TT(6)` | check of the start bit after 83 + 16 `TT(5)` - 4 `TT(6)` T-states from its detection | 1, 8 |
| `TT(7)`, `TT(8)` | polarity of TX and RX: 0 normal (idle high), 1 inverted | 0, 0 |
| `TT(9)`, `TT(10)` | wait for the first byte: 45 x 256 x `TT(9)` x `TT(10)` T-states (`TT(9)` = 0 means 256) | 0, 36 (29.7 s) |
| `TT(11)`, `TT(12)` | end of the packet: 45 x 256 x `TT(11)` x `TT(12)` T-states | 154, 1 (0.5 s) |

A step of `TT(2)` or `TT(4)` changes the bit time by 4 T-states (2.1 % at 19200), a step of `TT(1)` or `TT(3)` by 16.

### Checking and adjusting the speed on the real machine

The calculated values assume the standard Z-80 timing. If the PC-1600 inserts a wait state into I/O cycles (my reading of the
Technical Reference, not verified), each `OUT` and `IN` takes one T-state more, which makes a bit about 0.5 % longer; the
tolerance in the simulation (below) is larger than that, but the real machine may differ. Check it:

1. TX: run the test, menu `3`, and measure a bit of the 55H stream (a square wave; one bit = half a period) with an
   oscilloscope or a logic analyzer. The target is 52.1 µs (the first value to change is `TT(2)`). Without an
   instrument: send `HELLO PC-1600` (menu `1`) to a terminal at 19200 baud; step `TT(2)` up and down until the text
   becomes garbage on both sides and use the middle of the working range.
2. RX: send text from the terminal (menu `2`); do the same with `TT(4)`, `TT(5)` and `TT(6)`.
3. Edit lines 110 to 140 of the loader and run it again.

### Other speeds

The loader does not offer them yet, but the code can do them. The formulas for the variables, with `bit` =
3579545 / baud T-states: choose `TT(1)`, `TT(2)` for `bit` - 100, `TT(3)`, `TT(4)` for `bit` - 99 and `TT(5)`, `TT(6)`
for `bit` / 2 - 22 - 83, each in units of 4 T-states (`TT(n)` x 4 - `TT(n+1)` = the target / 4, `TT(n+1)` = 0 ... 8).
`tools/verify_listing.py Basic/uart1600.txt <baud>` computes these values (function `params`) and checks the
transfer in the emulator. In the emulator this works for 9600, 4800, 2400 and 1200 bps; 28800 bps and more are out
of range of the code (the poll loop takes 45 T-states). That is a simulation only; none of them has been tried on the machine.

### Tolerance (simulation)

Against an ideal partner at 19200 bps the receiver reads correctly when the partner is between about -2 % and +4 %
off the nominal speed (the sampling point is set for a 187-T bit; the bytes may follow each other without a gap).
The sender is read correctly by an ideal receiver that is between about -4 % and +6 % off the nominal speed. With the wait state (+1 T per bit) the bit
times grow by about 0.5 %.

## What is not known, and how the loader deals with it

| Open point | What the code does |
|---|---|
| Z-80 timing of the PC-1600 (clock, wait states) | all timing in variables, see above; to be measured |
| Z-80 address of the BASIC address `A` | PROBE searches for a signature in the RAM (read only), manual input as a fallback |
| Port C | SEROUT writes the whole OPC register (18H): bit 7 = TX, **all other bits 0**. Bit 6 of OPC is inverted PC6 = "beep disable" according to the Technical Reference, written as 0. It is not known what the other bits of port C do on the PC-1600 and whether PC7 clicks the buzzer. If another bit matters, change `and 0x80` in `asm/uart1600.asm` |
| Levels and buffers at CN9 pins 27 and 29 | the schematic shows no buffer, but this is unchecked on a real machine |
| Interrupts | `DI` at the start, `EI` at the end |
| `CALL` register contract of the PC-1600 BASIC | the manual: the numeric variable goes in DE, and comes back in DE if the carry is set. SERIN and PROBE set the carry; SEROUT clears it and keeps AF, BC, DE and HL; SERIN keeps AF, BC and HL. Alternate registers and IX, IY are not used |
| The code needs a `NEW 1000` area | the loader cannot check it; without it the code may overwrite the BASIC program or variables |
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
