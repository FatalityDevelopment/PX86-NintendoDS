using Px86.NintendoDS.Common.Core;

namespace Px86.NintendoDS.EmulatorCore.Bios
{
    /// <summary>
    /// Minimal replacement BIOS images used when the user has no BIOS dumps.
    ///
    /// They contain only what the hardware needs to stay consistent with high-
    /// level emulated SWIs (<see cref="HleBios"/>): the exception vector table
    /// (every vector except IRQ is an endless loop) and the standard IRQ
    /// dispatcher, which calls the game's handler from the hard-wired pointer:
    ///
    ///   ARM9  [DTCM + 3FFCh]   (DTCM base read from CP15 C9,C1,0)
    ///   ARM7  [0380FFFCh]      (reached via 04000000h − 4, its mirror)
    ///
    ///   irq:  stmfd sp!, {r0-r3, r12, lr}
    ///         (ARM9) mrc p15,0,r0,c9,c1,0 ; r0 &amp;= ~FFFh ; r0 += 4000h
    ///         (ARM7) mov r0, #04000000h
    ///         add lr, pc, #0
    ///         ldr pc, [r0, #-4]
    ///         ldmfd sp!, {r0-r3, r12, lr}
    ///         subs pc, lr, #4
    ///
    /// Encodings were produced with llvm-mc (armv5te); the two branches are
    /// encoded by hand. A guest-call trap (an endless loop) sits at
    /// <see cref="GuestReturnOffset"/> for <see cref="HleBios"/> callbacks.
    /// </summary>
    public static class BiosStub
    {
        public const int Arm9Size = 0x1000;
        public const int Arm7Size = 0x4000;

        /// <summary>Offset of an endless loop used as the return address for HLE guest calls.</summary>
        public const uint GuestReturnOffset = 0x0F00;

        private const uint BranchSelf = 0xEAFFFFFEu;   // b .
        private const uint BranchIrq = 0xEA000000u;    // b +8 (from 18h to 20h)

        public static byte[] CreateArm9()
        {
            var bios = new byte[Arm9Size];
            uint[] code =
            {
                BranchSelf, BranchSelf, BranchSelf, BranchSelf, BranchSelf, BranchSelf, BranchIrq, BranchSelf,
                0xE92D500Fu, // push {r0-r3, r12, lr}
                0xEE190F11u, // mrc p15, 0, r0, c9, c1, 0
                0xE1A00620u, // lsr r0, r0, #12
                0xE1A00600u, // lsl r0, r0, #12
                0xE2800901u, // add r0, r0, #0x4000
                0xE28FE000u, // add lr, pc, #0
                0xE510F004u, // ldr pc, [r0, #-4]
                0xE8BD500Fu, // pop {r0-r3, r12, lr}
                0xE25EF004u, // subs pc, lr, #4
            };
            Write(bios, code);
            MemoryAccess.Write32(bios, (int)GuestReturnOffset, BranchSelf);
            return bios;
        }

        public static byte[] CreateArm7()
        {
            var bios = new byte[Arm7Size];
            uint[] code =
            {
                BranchSelf, BranchSelf, BranchSelf, BranchSelf, BranchSelf, BranchSelf, BranchIrq, BranchSelf,
                0xE92D500Fu, // push {r0-r3, r12, lr}
                0xE3A00301u, // mov r0, #0x04000000
                0xE28FE000u, // add lr, pc, #0
                0xE510F004u, // ldr pc, [r0, #-4]
                0xE8BD500Fu, // pop {r0-r3, r12, lr}
                0xE25EF004u, // subs pc, lr, #4
            };
            Write(bios, code);
            MemoryAccess.Write32(bios, (int)GuestReturnOffset, BranchSelf);
            return bios;
        }

        private static void Write(byte[] bios, uint[] code)
        {
            for (int i = 0; i < code.Length; i++)
                MemoryAccess.Write32(bios, i * 4, code[i]);
        }
    }
}
