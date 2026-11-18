; UART1500 SAVER / LOADER for the Sharp PC-1500(A): BASIC programs through the software UART, 4800 bps 8N1 (fixed).
; TX = PC7 (port C &F008 bit 7, 60-pin connector pin 10), RX = PB2 (CMT IN, pin 27) by default or PB0 (pin 9); CPU LH5801 1.3 MHz.
; The bit routines are those of UART7.3 (SEROUT/SERIN v6.2 by Alex F4VTS, tested on a real PC-1500A), only the buffer
; handling is replaced: the BASIC program is sent / received directly from / into the program memory.
; The file is the bytes of the BASIC program as they lie in the memory (tokens) followed by one byte: the sum of all the
; bytes before it, modulo 256 (the same file as for the PC-1600). The end of a received file is 0.5 s without a byte.
; Pointers of the BASIC (from the ROM, NEW): &7865/&7866 = start of the program (high byte first), &7867/&7868 = the
; address of the end marker FFH after the last line, &7864 = the high byte of the end of the RAM + 1.
; CALL SAVE,X sends the program (X = 1 when done). CALL LOAD,X waits up to 30 s for a file, stores it from the start of the
; program, puts the end marker FFH after the last line and sets the end pointer:
; X = 1 ok, 2 checksum error, 3 nothing received, 4 does not fit. After an error 2 or 4 the memory is left empty (as after NEW).
; Type NEW before LOAD (an old program would stay behind the received one only as garbage; NEW also clears the variables).
; Without a variable (CALL LOAD) it works too. The code has no buffers and no absolute jumps: it can be placed at any address.
; Constants of 4800 bps: KB = 17, KS = KB+2, KI = KB+7, KT = 26, KH = 12 (as in UART7.3 v6.2).
; DB = &FB, BM = 4 (PB2) or DB = &FE, BM = 1 (PB0): the installer patches them.
KB = 17
KS = KB+2
KI = KB+7
KT = 26
KH = 12
DB = &FB
BM = 4
PS = &7865
PE = &7867
RAMH = &7864
;---------------------------------------------------------------- LOAD
LOAD:
 RIE                 ; no interrupts (timing!)
 LDA (PS)
 STA XH
 LDA (PS+1)
 STA XL              ; X = start of the program
 LDI YH,&F0
 LDI YL,&0D          ; Y = &F00D (DDB, direction register of port B)
 ANI #(Y),DB         ; PB0 or PB2 = input
 INC Y
 INC Y               ; Y = &F00F (port B)
 LDI UH,0            ; time-out of the 1st character: 18*256*256 loops ~ 30 s
 LDI A,18
WH: BII #(Y),BM      ; wait for the idle state (1 = mark)
 BZR+ WL             ; line = mark -> wait for the start bit
 LOP UL,WH
 DEC UH
 BZR- WH
 DEC A
 BZR- WH
 BCH+ DONE           ; time-out
WL: BII #(Y),BM      ; wait for the edge 1->0 (start bit)
 BZS+ START          ; line = space -> start bit
 LOP UL,WL
 DEC UH
 BZR- WL
 DEC A
 BZR- WL
 BCH+ DONE           ; time-out
START: LDI UL,KH     ; ~half of the start bit
HALF: LOP UL,HALF
 BII #(Y),BM         ; middle of the start bit: still 0?
 BZR- WL             ; no -> noise, keep waiting
 LDI UH,8            ; 8 data bits
BIT: LDI UL,KB       ; bit loop
RDLY: LOP UL,RDLY
 NOP                 ; (fine tuning: +10 cycles)
 NOP
 BII #(Y),BM
 SEC
 BZR+ RONE           ; mark -> bit = 1
 REC
RONE: ROR             ; LSB first
 DEC UH
 BZR- BIT
 SIN X               ; store the character, X++
 LDA XH
 CPA (RAMH)
 BCS+ FULL           ; the next byte would not fit into the RAM
 LDI UH,77           ; time-out between characters: 77*256 loops ~ 0.5 s
 LDI A,1
 BCH- WH             ; wait for the stop bit and the next character
FULL: LDI A,4        ; if the checksum is wrong: 4 = does not fit
 BCH+ D2
DONE: LDI A,2        ; if the checksum is wrong: 2 = checksum error
D2: PSH A
 LDA XL
 CPA (PS+1)
 BZR+ GOT
 LDA XH
 CPA (PS)
 BZS+ NONE           ; X = start: nothing received
GOT: STX Y           ; Y = end of the file
 DEC Y               ; Y = the checksum byte
 LDA (PS)
 STA XH
 LDA (PS+1)
 STA XL              ; X = start
 LDI UH,0
SUM: LDA XL
 CPA YL
 BZR+ SN
 LDA XH
 CPA YH
 BZS+ SEND
SN: LIN X
 REC
 ADC UH
 STA UH              ; UH = sum
 BCH- SUM
SEND: LDA (X)        ; the checksum byte of the file (X = Y)
 CPA UH
 BZS+ OK
 POP A
 STA UL              ; UL = 2 or 4
 LDA (PS)
 STA YH
 LDA (PS+1)
 STA YL              ; Y = start: the memory is left empty
 BCH+ COM
OK: POP A            ; the error code is not needed
 LDI A,1
 STA UL              ; UL = 1
COM: LDI A,&FF
 STA (Y)             ; the end marker after the last line (Y = start after an error)
 LDA YH
 STA (PE)
 LDA YL
 STA (PE+1)          ; end pointer
 LDA UL
 STA XL
 BCH+ FIN
NONE: POP A
 LDI XL,3
FIN: LDI XH,0        ; X = the code, C=1: BASIC writes X into the variable of CALL LOAD,X
 SEC
 SIE                 ; interrupts on again for BASIC
 RTN
;---------------------------------------------------------------- SAVE
SAVE:
 RIE                 ; no interrupts (timing!)
 LDA (PS)
 STA XH
 LDA (PS+1)
 STA XL              ; X = start of the program
 LDI UH,0
PRE: LDA XL          ; sum of the bytes from the start to the end marker
 CPA (PE+1)
 BZR+ PB
 LDA XH
 CPA (PE)
 BZS+ PEND
PB: LIN X
 REC
 ADC UH
 STA UH
 BCH- PRE
PEND: LDA UH
 STA (X)             ; the checksum replaces the end marker FFH for the time of the transmission (X = end)
 LDA (PS)
 STA XH
 LDA (PS+1)
 STA XL              ; X = start
 LDI YH,&F0
 LDI YL,&08          ; Y = &F008 (port C)
 ORI #(Y),&80        ; PC7 = idle (mark) ...
 LDI UL,KI           ; ... for ~1 bit before the first start bit
IDLE: LOP UL,IDLE
CHAR: LDA (X)        ; A = character
 ANI #(Y),&7F        ; start bit (space)
 SEC
 ROR                 ; C = bit 0, sentinel 1 -> bit 7 of A
 LDI UL,KS           ; length of the start bit
SDLY: LOP UL,SDLY
 NOP
OUT: BCS+ SONE       ; send the bit in C
 REC                 ; (alignment: the write in the same cycle)
 ANI #(Y),&7F        ; bit 0 (space)
 BCH+ BDLY
SONE: ORI #(Y),&80   ; bit 1 (mark)
 NOP                 ; (equal branch lengths)
 SEC
BDLY: LDI UL,KB      ; length of a data bit
DLY: LOP UL,DLY
 REC                 ; (fine tuning: +4 cycles)
 SHR                 ; C = next bit
 BII A,&FF           ; A = 0 -> 8 bits sent
 BZR- OUT
 NOP                 ; (alignment of the last data bit)
 NOP
 REC
 ORI #(Y),&80        ; stop bit (mark)
 LDI UL,KT           ; length of the stop bit (about 1.1 bits)
TDLY: LOP UL,TDLY
 LDA XL              ; the byte at the end pointer (the checksum) sent?
 CPA (PE+1)
 BZR+ NXT
 LDA XH
 CPA (PE)
 BZS+ SEND2
NXT: INC X
 BCH- CHAR           ; next character
SEND2: LDI A,&FF
 STA (X)             ; the end marker FFH back (X = end)
 LDI XH,0
 LDI XL,1
 SEC                 ; C=1: BASIC writes X = 1 into the variable of CALL SAVE,X
 SIE                 ; interrupts on again for BASIC
 RTN
