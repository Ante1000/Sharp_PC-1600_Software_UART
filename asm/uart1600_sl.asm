; UART1600 SAVER / LOADER for the Sharp PC-1600: BASIC programs through the UART (19200 bps, 8N1, fixed timing)
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
PE2: equ 0xF868          ; a second copy of the end pointer (the BASIC takes the end of the program from it)

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
 ld hl,(PS)
 ld de,(PE)
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
 ld d,4               ; 19200 bps: bit = 156 T-states
sv_dl:
 dec d
 jr nz,sv_dl
 jr $+4               ; skips 2 of the 8 NOPs
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
 ld d,1               ; half bit: 83 + 16 - 32 = 67 T-states
ld_hl:
 dec d
 jr nz,ld_hl
 jr ld_hp              ; 12 T-states (the same time as before: the 8 NOPs behind it were never run)
ld_hp:
 in a,(OPB)           ; middle of the start bit
 and 0x04
 xor 0x00
 jr nz,ld_next        ; line high again: it was a glitch
 ld b,8
 jr $+2               ; calibrated pad for the first bit: 16 T-states = JR (12) + NOP (4)
 nop
ld_bit:
 ld d,4
ld_dd:
 dec d
 jr nz,ld_dd
 jr $+2               ; 36 T-states in 6 bytes (before: JR + 6 NOPs run)
 jr $+2
 jr $+2
 in a,(OPB)           ; sample in the middle of the bit
 and 0x04
 xor 0x00
 add a,0xFC           ; carry = bit
 rr c
 djnz ld_bit
 ld d,4               ; middle of the stop bit
ld_d2:
 dec d
 jr nz,ld_d2
 jr $+2               ; 36 T-states
 jr $+2
 jr $+2
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
 ld (PE2),hl
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
