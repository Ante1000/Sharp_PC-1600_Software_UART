; UART1600 SAVER / LOADER V3.2 for the Sharp PC-1600: BASIC programs through the UART (1200 to 19200 bps, 8N1; the BASIC installer patches the delays)
; TX = PC7 (OPC, I/O 18H bit 7, CN9 pin 29), RX = PB2 (OPB, I/O 1FH bit 2, CN9 pin 27); CPU about 3.0 MHz.
; The BASIC program is copied as it lies in the memory (tokens). The file is the bytes of the program followed by one
; byte: the sum of all the bytes before it, modulo 256. The end of a received file is 0.5 s without a byte.
; Pointers of the BASIC (found with the memory finder): FE3CH = start of the program (2 bytes + 1), FE3FH = its end.
; CALL SAVE,X sends the program (X = 1 when done). CALL LOAD,X waits up to 30 s for a file, stores it from the start
; of the program, puts the end marker FFH after the last line and sets both end pointers (FE3FH and F868H):
; X = 1 ok, 2 checksum error, 3 nothing received, 4 does not fit. After an error 2 or 4 the memory is left empty (as after NEW).
; Use them as direct commands (RUN mode), not in a program. The code needs no buffer, has no CALLs, no absolute jumps
; and no absolute operands except the two pointers of the BASIC: it can be placed at any address.
OPC: equ 0x18
DDB: equ 0x1D
OPB: equ 0x1F
PS:  equ 0xFE3C
PE:  equ 0xFE3F
PE2H: equ 0xF867         ; the end of the program for the BASIC (F867H/F868H = the PC-1500 notation: the HIGH byte first, bit 15 inverted,
PE2L: equ 0xF868         ; like F865H/F866H = its start; the BASIC makes FE3FH/FE40H from them: STATUS 1 was negative when only F868H was set)

 org 0
;---------------------------------------------------------------- SAVE
SAVE:
 push af
 push bc
 push hl
 ex af,af'
 push af              ; AF' of the BASIC is kept
 ex af,af'
 in a,(OPC)           ; OPC before the transmission, restored at the end
 push af
 ld hl,(PS)           ; HL = start of the program
 ld d,h
 ld e,l
sv_scan:                ; the end of the program is found from the lines, not from the pointer FE3FH (it can be wrong: it
 ld a,d                 ; was found 5 bytes BEFORE the start with a large program loaded as text)
 cp 0xF0                ; safety: never beyond the work area (F000H)
 jr nc,sv_endf
 ld a,(de)              ; the high byte of the line number: FFH = the end marker of the program
 cp 0xFF
 jr z,sv_endf
 inc de
 inc de
 ld a,(de)              ; the length of the line (from the token to the CR)
 inc de
 add a,e
 ld e,a
 jr nc,sv_scan
 inc d
 jr sv_scan
sv_endf:
 ld a,e
 sub l
 ld c,a
 ld a,d
 sbc a,h
 ld b,a               ; BC = length of the program, HL = its start
 xor a
 ex af,af'            ; A' = checksum
 di
 ld a,0x80
 out (OPC),a          ; idle (high) for a moment
 ld d,12
sv_id:
 dec d
 jr nz,sv_id
 ld e,0               ; E = block number (0 = the program, 1 = the checksum)
sv_blk:
 ld a,b
 or c
 jr z,sv_next
sv_byte:
 push bc
 ld c,(hl)
 inc hl
 ex af,af'
 add a,c
 ex af,af'
 ld b,10              ; start + 8 data + stop
 or a                 ; carry = 0 -> start bit
sv_loop:
 sbc a,a
 and 0x80
 xor 0x00
 out (OPC),a
 scf
 rr c
sv_dn:
 ld d,4               ; PATCH N (delay loop count, 16 T-states each); 19200 bps as assembled: bit = 156 T-states
sv_dl:
 dec d
 jr nz,sv_dl
sv_jd:
 jr $+4               ; PATCH D: displacement 0..8 = the number of NOPs skipped (4 T-states each); BIT = 100 + 16*N - 4*D
 nop
 nop
 nop
 nop
 nop
 nop
 nop
 nop
 djnz sv_loop
 pop bc
 dec bc
 ld a,b
 or c
 jr nz,sv_byte
sv_next:
 inc e
 ld a,e
 cp 1
 jr nz,sv_end
 ex af,af'
 push af              ; the checksum byte is sent from the stack (A is at SP+1)
 ex af,af'
 ld hl,1
 add hl,sp
 ld bc,1
 jr sv_blk
sv_end:
 pop af               ; the checksum byte put on the stack is dropped
 pop af
 out (OPC),a          ; OPC as before
 ei
 ex af,af'
 pop af
 ex af,af'
 ld de,1
 pop hl
 pop bc
 pop af
 scf                  ; carry set: DE (= 1) is returned to BASIC
 ret
sv_fin:
 defs 0xB0-$

;---------------------------------------------------------------- LOAD
 org 0xB0
LOAD:
 push af
 push bc
 push hl
 ex af,af'
 push af              ; AF' of the BASIC is kept
 ex af,af'
 di
 in a,(DDB)
 and 0xFB
 out (DDB),a          ; PB2 = input
 xor a
 ex af,af'            ; A' = checksum
 ld hl,(PS)           ; the program is stored from its start
 ld e,0xC0            ; the limit: the end of the 16 KB window of a module (8000H-BFFFH) ...
 ld a,h
 cp 0xC0
 jr c,ld_lim
 ld e,0xF0            ; ... or the work area (F000H) if the program is in the internal RAM
ld_lim:
 ld c,0
 ld d,30              ; about 30 s for the first byte
 jr ld_w
ld_next:
 ld c,130
 ld d,1               ; about 0.5 s between the bytes: the end of the file
ld_w:
 ld b,0
ld_poll:
 in a,(OPB)
 and 0x04
 xor 0x00
 jr z,ld_st           ; line low = start edge
 djnz ld_poll
 dec c
 jr nz,ld_poll
 dec d
 jr nz,ld_poll
 jr ld_tmo            ; no byte: the end of the file
ld_st:
ld_hn:
 ld d,1               ; PATCH N: half bit delay
ld_hl:
 dec d
 jr nz,ld_hl
ld_hj:
 jr ld_hp              ; PATCH D: displacement 0..8 (the 8 NOPs are skipped as assembled)
 nop
 nop
 nop
 nop
 nop
 nop
 nop
 nop
ld_hp:
 in a,(OPB)           ; middle of the start bit
 and 0x04
 xor 0x00
 jr nz,ld_next        ; line high again: it was a glitch
 ld b,8
 jr $+2               ; calibrated pad for the first bit: 16 T-states = JR (12) + NOP (4)
 nop
ld_bit:
ld_bn:
 ld d,4               ; PATCH N
ld_dd:
 dec d
 jr nz,ld_dd
ld_bj:
 jr $+4               ; PATCH D: BIT = 99 + 16*N - 4*D
 nop
 nop
 nop
 nop
 nop
 nop
 nop
 nop
 in a,(OPB)           ; sample in the middle of the bit
 and 0x04
 xor 0x00
 add a,0xFC           ; carry = bit
 rr c
 djnz ld_bit
ld_sn:
 ld d,4               ; PATCH N (the same value as for the bits): middle of the stop bit
ld_d2:
 dec d
 jr nz,ld_d2
ld_sj:
 jr $+4               ; PATCH D (the same value as for the bits)
 nop
 nop
 nop
 nop
 nop
 nop
 nop
 nop
 ld (hl),c
 inc hl
 ex af,af'
 add a,c
 ex af,af'
 ld a,h
 cp e
 jr c,ld_next         ; still inside the memory
 ld e,4               ; it does not fit
 jr ld_bad
ld_tmo:
 ld de,(PS)
 push hl
 or a
 sbc hl,de            ; HL = number of bytes received (the checksum included)
 ld a,h
 or l
 ld e,3
 jr z,ld_exit0        ; nothing was received
 ld a,h
 or a
 jr nz,ld_cnt
 ld a,l
 cp 2
 ld e,2
 jr c,ld_exit0        ; only one byte: no checksum
ld_cnt:
 pop hl
 dec hl               ; HL = address of the checksum byte
 ld c,(hl)
 ex af,af'
 sub c
 sub c                ; the sum of all minus twice the checksum byte = 0 when the checksum is right
 ld e,2
 jr nz,ld_bad         ; wrong checksum
 ld e,1
 jr ld_fix            ; HL = the checksum byte: the end marker of the program replaces it
ld_exit0:
 pop hl
 ld a,e
 cp 3
 jr z,ld_exit         ; nothing was received: the memory is untouched
ld_bad:
 ld hl,(PS)           ; an error: the program area is left empty, as after NEW
ld_fix:
 ld (hl),0xFF         ; the end marker
 ld (PE),hl           ; the end of the program = the place of the marker
 ld a,h
 xor 0x80             ; bit 15 inverted
 ld (PE2H),a
 ld a,l
 ld (PE2L),a
ld_exit:
 ld d,0
 ei
 ex af,af'
 pop af
 ex af,af'
 pop hl
 pop bc
 pop af
 scf                  ; carry set: DE (the result) is returned to BASIC
 ret
ld_fin:
