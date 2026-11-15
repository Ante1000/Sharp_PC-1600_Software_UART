; UART1600 v1.0: software UART (bit-bang) for the Sharp PC-1600, 8N1, 19200 bps (the delay parameters allow other speeds)
; CPU: Z-80 compatible (SC7852), 3.58 MHz. Assembler: z80asm (Debian/Ubuntu package z80asm).
;   TX = PC7 : OPC register, I/O 18H bit 7  -> CN9 pin 29 (CMTout)
;   RX = PB2 : OPB register, I/O 1FH bit 2  -> CN9 pin 27 (CMTin)
; The code contains no CALLs and no absolute jumps: it can be loaded at any address. The only
; absolute operands are the buffer addresses TXPTR and RXPTR, which the BASIC loader patches.
; Layout (offsets from the start of the code):
;   +000 SEROUT   +050 SERIN   +100 TXBUF (255 bytes)   +1FF RXLEN   +200 RXBUF (255 bytes); PROBE (loader only) at +200
OPC: equ 0x18
DDB: equ 0x1D
OPB: equ 0x1F

 org 0
;---------------------------------------------------------------- SEROUT
; CALL base,m : sends m (1..255; 0 or no value = 1 byte) bytes from TXBUF. DE = m on entry (E is used).
SEROUT:
 push af
 push bc
 push de
 push hl
 ld a,e
 or a
 jr nz,so_n
 inc e
so_n:
so_tp:
 ld hl,0              ; PATCH TXPTR (16 bit): address of TXBUF
 di
 inc e                ; one extra pass through the loop: an idle (high) bit before the first start bit
 ld b,1
 scf
 jr so_loop
so_byte:
 ld c,(hl)
 inc hl
 ld b,10              ; start + 8 data + stop
 or a                 ; carry = 0 -> start bit
so_loop:
 sbc a,a              ; A = FF if carry else 00
 and 0x80             ; bit 7 = PC7 (bit 6 = 0: PC6, beep disable)
so_pol:
 xor 0x00             ; PATCH TX_POL: 00 = normal, 80 = inverted
 out (OPC),a
 scf
 rr c                 ; next bit -> carry, 1 shifted in (stop bit)
so_dn:
 ld d,6               ; PATCH TX_N: delay loop count (16 T each)
so_dl:
 dec d
 jr nz,so_dl
so_jd:
 jr so_pad            ; PATCH TX_D: displacement 0..8 = number of NOPs skipped (4 T each)
 nop
 nop
 nop
 nop
 nop
 nop
 nop
 nop
so_pad:
 djnz so_loop
 dec e
 jr nz,so_byte
 ei
 pop hl
 pop de
 pop bc
 pop af
 or a                 ; carry clear
 ret
so_end:
 defs 0x50-$

;---------------------------------------------------------------- SERIN
; CALL base+50H,m : receives up to m (1..255, 0 = 255) bytes into RXBUF.
; Waits about 30 s for the first byte, ends when no byte arrives within about 0.5 s.
; Returns DE = number of bytes (also in RXLEN = RXBUF-1), carry set.
 org 0x50
SERIN:
 push af
 push bc
 push hl
 ld a,e
 or a
 jr nz,si_n
 ld e,255
si_n:
si_rp:
 ld hl,0              ; PATCH RXPTR (16 bit): address of RXBUF
 push hl              ; remember its start
 di
 in a,(DDB)
 and 0xFB
 out (DDB),a          ; PB2 = input
si_t1m:
 ld c,0               ; PATCH TOM1 (0 = 256)
si_t1f:
 ld d,36              ; PATCH TOF1 : about 30 s
 jr si_w
si_next:
si_t2m:
 ld c,154             ; PATCH TOM2
si_t2f:
 ld d,1               ; PATCH TOF2 : about 0.5 s
si_w:
 ld b,0
si_poll:
 in a,(OPB)
 and 0x04
si_p1:
 xor 0x00             ; PATCH RX_POL: 00 = normal, 04 = inverted
 jr z,si_st           ; line low = start edge
 djnz si_poll
 dec c
 jr nz,si_poll
 dec d
 jr nz,si_poll
 jr si_end            ; timeout
si_st:
si_hn:
 ld d,10              ; PATCH RX_NH: half bit delay count
si_hl:
 dec d
 jr nz,si_hl
si_hj:
 jr si_hp             ; PATCH RX_DH: pad
 nop
 nop
 nop
 nop
 nop
 nop
 nop
 nop
si_hp:
 in a,(OPB)           ; middle of the start bit
 and 0x04
si_p2:
 xor 0x00             ; PATCH RX_POL
 jr nz,si_next        ; line high again: it was a glitch
 ld b,8
si_fx:
 nop                  ; calibrated pad for the first bit (4 NOPs = 16 T-states, see tools/build_loader.py)
 nop
 nop
 nop
si_bit:
si_dn:
 ld d,6               ; PATCH RX_N: bit delay loop count
si_dd:
 dec d
 jr nz,si_dd
si_dj:
 jr si_dp             ; PATCH RX_D: pad
 nop
 nop
 nop
 nop
 nop
 nop
 nop
 nop
si_dp:
 in a,(OPB)           ; sample in the middle of the bit
 and 0x04
si_p3:
 xor 0x00             ; PATCH RX_POL
 add a,0xFC           ; carry = bit
 rr c
 djnz si_bit
si_dn2:
 ld d,6               ; PATCH RX_N (second copy): middle of the stop bit
si_dd2:
 dec d
 jr nz,si_dd2
si_dj2:
 jr si_dp2            ; PATCH RX_D (second copy)
 nop
 nop
 nop
 nop
 nop
 nop
 nop
 nop
si_dp2:
 ld (hl),c
 inc hl
 dec e
 jr nz,si_next
si_end:
 pop de               ; DE = start of the buffer
 ld a,l
 sub e                ; A = number of bytes received
 dec de
 ld (de),a            ; RXLEN
 ld e,a
 ld d,0
 ei
 pop hl
 pop bc
 pop af
 scf                  ; carry set: DE is returned to BASIC
 ret
si_fin:
 defs 0x200-$

;---------------------------------------------------------------- PROBE
; Only used by the BASIC loader, at load time. It is stored in the RX buffer (+200H), which is free then.
; CALL base+200H,x : looks in the Z-80 memory for an 8-byte signature that the loader has POKEd into TXBUF and
; returns in x the Z-80 address that belongs to the BASIC address of the code (= the address of the signature
; minus 100H), or 0 if the signature was not found. Read only. The signature is stored in the code as the operands
; of the CP instructions (not as 8 contiguous bytes), so the code does not find itself.
; PATCH: the 8 signature bytes (pr_s0 ... pr_s7), the high byte of the first address (pr_sh), the number of 256-byte pages (pr_np).
 org 0x200
PROBE:
 push hl
 push bc
pr_sh:
 ld h,0xC0            ; PATCH: high byte of the start address of the search (0xC0: the internal RAM)
 ld l,0
pr_np:
 ld b,0x40            ; PATCH: number of 256-byte pages to search
 ld c,0
pr_scan:
pr_s0:
 ld a,0               ; PATCH signature byte 0
 cpir                 ; search for byte 0
 jr nz,pr_none
 push hl              ; HL = address of byte 0 + 1
 ld a,(hl)
pr_s1:
 cp 0                 ; PATCH signature byte 1
 jr nz,pr_back
 inc hl
 ld a,(hl)
pr_s2:
 cp 0                 ; PATCH signature byte 2
 jr nz,pr_back
 inc hl
 ld a,(hl)
pr_s3:
 cp 0                 ; PATCH signature byte 3
 jr nz,pr_back
 inc hl
 ld a,(hl)
pr_s4:
 cp 0                 ; PATCH signature byte 4
 jr nz,pr_back
 inc hl
 ld a,(hl)
pr_s5:
 cp 0                 ; PATCH signature byte 5
 jr nz,pr_back
 inc hl
 ld a,(hl)
pr_s6:
 cp 0                 ; PATCH signature byte 6
 jr nz,pr_back
 inc hl
 ld a,(hl)
pr_s7:
 cp 0                 ; PATCH signature byte 7
 jr nz,pr_back
 pop hl               ; found: HL = address of the signature + 1
 ld de,0x0101
 or a
 sbc hl,de            ; HL = address of the signature - 100H = Z-80 address of the code
 ex de,hl
 jr pr_end
pr_back:
 pop hl
 ld a,b
 or c
 jr nz,pr_scan
pr_none:
 ld de,0
pr_end:
 pop bc
 pop hl
 scf                  ; carry set: DE is returned to BASIC
 ret
pr_fin:
