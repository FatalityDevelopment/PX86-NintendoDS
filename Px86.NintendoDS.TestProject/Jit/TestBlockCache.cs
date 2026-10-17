using Px86.NintendoDS.EmulatorCore;
using Px86.NintendoDS.EmulatorCore.Jit;
using Px86.NintendoDS.EmulatorCore.Video;
using Px86.NintendoDS.TestProject.Emulator;

namespace Px86.NintendoDS.TestProject.Jit
{
    /// <summary>
    /// The block cache must not change anything: every test ROM run with
    /// cached blocks must stay bit-identical to the interpreter, frame by frame.
    /// </summary>
    [TestClass]
    public class TestBlockCache
    {
        private static NdsEmulator Boot(string rom, CpuMode mode)
        {
            var nds = TestRomRunner.Boot(rom);
            nds.CpuMode = mode;
            return nds;
        }

        private static void AssertSame(NdsEmulator a, NdsEmulator b, string what)
        {
            Assert.AreEqual(a.Scheduler.Now, b.Scheduler.Now, $"{what}: time");
            Assert.AreEqual(a.Arm9.ToString(), b.Arm9.ToString(), $"{what}: ARM9");
            Assert.AreEqual(a.Arm7.ToString(), b.Arm7.ToString(), $"{what}: ARM7");
            Assert.AreEqual(a.Arm9.TotalCycles, b.Arm9.TotalCycles, $"{what}: ARM9 cycles");
            Assert.AreEqual(a.Arm7.TotalCycles, b.Arm7.TotalCycles, $"{what}: ARM7 cycles");
            Assert.IsTrue(a.MainRam.AsSpan().SequenceEqual(b.MainRam), $"{what}: main RAM");
            Assert.IsTrue(a.Arm7Wram.AsSpan().SequenceEqual(b.Arm7Wram), $"{what}: ARM7 WRAM");
        }

        [DataTestMethod]
        [DataRow("systest", 30)]
        [DataRow("gfxtest", 10)]
        [DataRow("gfx3d", 10)]
        [DataRow("tex3d", 10)]
        [DataRow("sound", 20)]
        [DataRow("touch", 10)]
        [DataRow("bench", 20)]
        public void CachedRunsMatchTheInterpreter(string rom, int frames) => Lockstep(rom, frames, CpuMode.Cached);

        [DataTestMethod]
        [DataRow("systest", 30)]
        [DataRow("gfxtest", 10)]
        [DataRow("gfx3d", 10)]
        [DataRow("tex3d", 10)]
        [DataRow("sound", 20)]
        [DataRow("touch", 10)]
        [DataRow("bench", 20)]
        public void JitRunsMatchTheInterpreter(string rom, int frames)
        {
            var jit = Lockstep(rom, frames, CpuMode.Jit);
            Assert.IsTrue(jit.JitCompiles > 0, "blocks were actually compiled");
        }

        private static NdsEmulator Lockstep(string rom, int frames, CpuMode mode)
        {
            var interpreter = Boot(rom, CpuMode.Interpreter);
            var cached = Boot(rom, mode);
            for (int frame = 0; frame < frames; frame++)
            {
                interpreter.RunFrame();
                cached.RunFrame();
                AssertSame(interpreter, cached, $"{rom} frame {frame}");
            }
            CollectionAssert.AreEqual(ScreenCapture.Compose(interpreter), ScreenCapture.Compose(cached), $"{rom}: screens");
            Assert.IsTrue(cached.BlocksCompiled > 0, "blocks were actually used");
            Assert.AreEqual(0, interpreter.BlocksCompiled, "the interpreter built none");
            return cached;
        }

        [TestMethod]
        public void OverwrittenCodeIsRebuilt()
        {
            // Let bench run so its loop gets cached, then patch the instruction under the PC
            // ("mov r0, #42; b ." in ARM state) and check the patched code is what runs.
            foreach (var mode in new[] { CpuMode.Interpreter, CpuMode.Cached, CpuMode.Jit })
            {
                var nds = Boot("bench", mode);
                for (int i = 0; i < 3; i++) nds.RunFrame();
                const uint patch = 0x023F8000;
                nds.Arm9Bus.BusWrite32(patch, 0xE3A0002A);       // mov r0, #42
                nds.Arm9Bus.BusWrite32(patch + 4, 0xEAFFFFFE);   // b .
                nds.RunCycles(100);                               // cache the patch area? not yet executed
                nds.Arm9.SetPc(patch);
                nds.RunFrame();
                Assert.AreEqual(42u, nds.Arm9.R[0], $"{mode}: first version ran");

                nds.Arm9Bus.BusWrite32(patch, 0xE3A00007);       // overwrite: mov r0, #7
                nds.Arm9.SetPc(patch);
                nds.RunFrame();
                Assert.AreEqual(7u, nds.Arm9.R[0], $"{mode}: the overwritten instruction is the one that runs");
            }
        }

        /// <summary>
        /// Random code (every condition, opcode, shift and flag case the JIT
        /// inlines, loads and stores of every size through SP, plus MUL and
        /// conditional branches) looped often enough to be compiled, compared
        /// with the interpreter after every slice of time.
        /// </summary>
        [DataTestMethod]
        [DataRow(false, 1)]
        [DataRow(false, 2)]
        [DataRow(false, 3)]
        [DataRow(false, 4)]
        [DataRow(false, 5)]
        [DataRow(true, 1)]
        [DataRow(true, 2)]
        [DataRow(true, 3)]
        [DataRow(true, 4)]
        [DataRow(true, 5)]
        public void RandomCodeMatchesTheInterpreter(bool thumb, int seed)
        {
            var interpreter = Boot("bench", CpuMode.Interpreter);
            var jit = Boot("bench", CpuMode.Jit);
            const uint start = 0x023F0000;
            var random = new Random(seed);
            var code = thumb ? RandomThumb(random, 48, start) : RandomArm(random, 48, start);
            foreach (var nds in new[] { interpreter, jit })
            {
                for (int i = 0; i < code.Count; i++)
                {
                    if (thumb) nds.Arm9Bus.BusWrite16(start + (uint)(i * 2), (ushort)code[i]);
                    else nds.Arm9Bus.BusWrite32(start + (uint)(i * 4), code[i]);
                }
                for (int r = 0; r < 13; r++)
                    nds.Arm9.R[r] = (uint)new Random(seed * 100 + r).Next() * 3u;
                nds.Arm9.R[13] = 0x02300000; // loads and stores go through SP
                nds.Arm9.R[14] = 0x40;       // ...and SP + LR
                nds.Arm9.SetPc(start | (thumb ? 1u : 0u));
            }
            for (int step = 0; step < 400; step++)
            {
                interpreter.RunCycles(997);
                jit.RunCycles(997);
                AssertSame(interpreter, jit, $"slice {step}");
            }
            Assert.IsTrue(jit.JitCompiles > 0, "the loop was compiled");
        }

        private static readonly uint[] Conditions = { 0x0, 0x1, 0x2, 0x3, 0x4, 0x5, 0x6, 0x7, 0x8, 0x9, 0xA, 0xB, 0xC, 0xD, 0xE, 0xE, 0xE, 0xE };

        private static List<uint> RandomArm(Random random, int count, uint start)
        {
            var code = new List<uint>();
            while (code.Count < count)
            {
                uint cond = Conditions[random.Next(Conditions.Length)];
                int kind = random.Next(13);
                uint rd = (uint)random.Next(13), rn = (uint)random.Next(16), rm = (uint)random.Next(16);
                if (kind < 7)
                {
                    uint opcode = (uint)random.Next(16);
                    uint s = (uint)random.Next(2);
                    if (opcode is >= 8 and <= 11) s = 1; // compares only exist with S
                    uint operand = random.Next(3) switch
                    {
                        0 => (1u << 25) | ((uint)random.Next(16) << 8) | (uint)random.Next(256),     // rotated immediate
                        1 => ((uint)random.Next(32) << 7) | ((uint)random.Next(4) << 5) | rm,           // shift by immediate
                        _ => ((uint)random.Next(13) << 8) | ((uint)random.Next(4) << 5) | (1u << 4) | rm, // shift by register (handler)
                    };
                    code.Add((cond << 28) | (opcode << 21) | (s << 20) | (rn << 16) | (rd << 12) | operand);
                }
                else if (kind == 10 || kind == 11)
                {
                    // LDR/STR{B} [sp, #±imm] or LDRH/STRH/LDRSB/LDRSH [sp, #±imm], pre-indexed, no writeback.
                    uint up = (uint)random.Next(2) << 23;
                    uint load = (uint)random.Next(2) << 20;
                    if (kind == 10)
                        code.Add((cond << 28) | 0x05000000u | up | ((uint)random.Next(2) << 22) | load | (13u << 16) | (rd << 12) | (uint)random.Next(4096));
                    else
                    {
                        uint sh = load != 0 ? (uint)random.Next(1, 4) : 1u;
                        uint imm = (uint)random.Next(256);
                        code.Add((cond << 28) | 0x01400090u | up | load | (13u << 16) | (rd << 12) | ((imm & 0xF0) << 4) | (sh << 5) | (imm & 0xF));
                    }
                }
                else if (kind == 12)
                {
                    // LDR/STR with a shifted register offset from SP (small offsets only).
                    uint load = (uint)random.Next(2) << 20;
                    code.Add((cond << 28) | 0x07800000u | load | (13u << 16) | (rd << 12) | ((uint)random.Next(2) << 7) | 14u); // [sp, lr, lsl #0/1]
                }
                else if (kind == 7)
                {
                    code.Add((cond << 28) | 0x00100090u | (rd << 16) | ((uint)random.Next(13) << 8) | (uint)random.Next(13)); // MULS
                }
                else
                {
                    // Forward conditional branch skipping 0–2 instructions.
                    code.Add((cond << 28) | 0x0A000000u | (uint)random.Next(3));
                }
            }
            // Room for the last branches to land on, then loop back: b start.
            code.AddRange(new[] { 0xE1A00000u, 0xE1A00000u, 0xE1A00000u }); // mov r0, r0
            uint here = start + (uint)(code.Count * 4);
            code.Add(0xEA000000u | (((start - (here + 8)) >> 2) & 0xFFFFFF));
            return code;
        }

        private static List<uint> RandomThumb(Random random, int count, uint start)
        {
            var code = new List<uint>();
            while (code.Count < count)
            {
                uint rd = (uint)random.Next(8), rs = (uint)random.Next(8);
                switch (random.Next(12))
                {
                    case 0: code.Add(((uint)random.Next(3) << 11) | ((uint)random.Next(32) << 6) | (rs << 3) | rd); break;       // LSL/LSR/ASR #imm
                    case 1: code.Add(0x1800u | ((uint)random.Next(4) << 9) | ((uint)random.Next(8) << 6) | (rs << 3) | rd); break; // ADD/SUB reg/imm3
                    case 2: code.Add(0x2000u | ((uint)random.Next(4) << 11) | (rd << 8) | (uint)random.Next(256)); break;         // MOV/CMP/ADD/SUB #imm8
                    case 3: case 4: code.Add(0x4000u | ((uint)random.Next(16) << 6) | (rs << 3) | rd); break;                      // ALU (incl. MUL)
                    case 5:
                    {
                        uint kind = (uint)random.Next(3);          // ADD / CMP / MOV with hi registers, never writing PC
                        uint hd = (uint)random.Next(15), hs = (uint)random.Next(16);
                        if (kind != 1 && hd == 13) hd = 12;          // keep SP sane
                        code.Add(0x4400u | (kind << 8) | ((hd & 8) << 4) | (hs << 3) | (hd & 7));
                        break;
                    }
                    case 6: code.Add(0xA000u | ((uint)random.Next(2) << 11) | (rd << 8) | (uint)random.Next(256)); break;         // ADD Rd, PC/SP
                    case 7: code.Add(0xB000u | ((uint)random.Next(2) << 7) | (uint)random.Next(8)); break;                        // ADD SP, #±imm
                    case 9: code.Add(0x9000u | ((uint)random.Next(2) << 11) | (rd << 8) | (uint)random.Next(256)); break;         // LDR/STR Rd, [SP, #imm]
                    case 10: code.Add(0x4800u | (rd << 8) | (uint)random.Next(256)); break;                                       // LDR Rd, [PC, #imm]
                    default: code.Add(0xD000u | ((uint)random.Next(14) << 8) | (uint)random.Next(3)); break;                       // Bcond +0..2
                }
            }
            code.AddRange(new[] { 0x46C0u, 0x46C0u, 0x46C0u }); // mov r8, r8
            uint here = start + (uint)(code.Count * 2);
            code.Add(0xE000u | (((start - (here + 4)) >> 1) & 0x7FF)); // b start
            return code;
        }

        [TestMethod]
        public void SaveStatesWorkWithCachedBlocks()
        {
            var a = Boot("systest", CpuMode.Cached);
            for (int i = 0; i < 5; i++) a.RunFrame();
            using var state = new MemoryStream();
            a.SaveState(state);
            for (int i = 0; i < 5; i++) a.RunFrame();

            var b = Boot("systest", CpuMode.Cached);
            b.RunFrame();
            state.Position = 0;
            b.LoadState(state);
            for (int i = 0; i < 5; i++) b.RunFrame();
            AssertSame(a, b, "after load");
        }
    }
}
