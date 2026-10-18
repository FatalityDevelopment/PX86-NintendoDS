@ Minimal C runtime for Px86 test ROMs (ARM9 and ARM7).
@ Direct boot already set up stacks and CPU mode; clear .bss and call main.
    .section .text.start, "ax"
    .arm
    .global _start
_start:
    ldr r0, =__bss_start
    ldr r1, =__bss_end
    mov r2, #0
1:  cmp r0, r1
    strlo r2, [r0], #4
    blo 1b
    bic sp, sp, #7          @ AAPCS: 8-byte aligned stack at calls (64-bit varargs depend on it)
    ldr r0, =main
    mov lr, pc
    bx r0
2:  b 2b
