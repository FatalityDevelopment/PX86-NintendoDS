#!/usr/bin/env python3
"""Pack ARM9/ARM7 flat binaries into a minimal .nds image.

usage: mkrom.py arm9.bin arm7.bin out.nds [GAMECODE] [TITLE]
ARM9 is placed at ROM 4000h (loaded to 02000000h), ARM7 after it on a 1000h
boundary (loaded to 02380000h). Header and logo CRCs are filled in.
"""
import struct, sys

def crc16(data, crc=0xFFFF):
    for b in data:
        crc ^= b
        for _ in range(8):
            crc = (crc >> 1) ^ 0xA001 if crc & 1 else crc >> 1
    return crc

arm9 = open(sys.argv[1], 'rb').read()
arm7 = open(sys.argv[2], 'rb').read()
out = sys.argv[3]
code = (sys.argv[4] if len(sys.argv) > 4 else 'PXTS').encode()[:4]
title = (sys.argv[5] if len(sys.argv) > 5 else 'PX86TEST').encode()[:12]

arm9_off = 0x4000
arm7_off = max(0x8000, (arm9_off + len(arm9) + 0xFFF) & ~0xFFF)
size = (arm7_off + len(arm7) + 0x1FF) & ~0x1FF
rom = bytearray(max(size, 0x20000))

rom[0:len(title)] = title
rom[0x0C:0x10] = code
rom[0x14] = max(0, (len(rom) - 1).bit_length() - 17)
struct.pack_into('<IIII', rom, 0x20, arm9_off, 0x02000000, 0x02000000, len(arm9))
struct.pack_into('<IIII', rom, 0x30, arm7_off, 0x02380000, 0x02380000, len(arm7))
struct.pack_into('<II', rom, 0x60, 0x00586000, 0x001808F8)
struct.pack_into('<II', rom, 0x80, len(rom), 0x4000)
rom[arm9_off:arm9_off + len(arm9)] = arm9
rom[arm7_off:arm7_off + len(arm7)] = arm7
struct.pack_into('<H', rom, 0x15C, 0xCF56)
struct.pack_into('<H', rom, 0x15E, crc16(rom[0:0x15E]))
open(out, 'wb').write(rom)
