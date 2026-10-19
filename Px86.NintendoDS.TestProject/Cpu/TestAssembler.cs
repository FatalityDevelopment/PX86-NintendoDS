using Px86.NintendoDS.Common.Assembler;

namespace Px86.NintendoDS.TestProject.Cpu
{
    /// <summary>
    /// Encodings checked against llvm-mc (-triple=armv5te / thumbv5te -show-encoding), plus the
    /// assembler-only features: labels, literal pools, ranges, errors and the ARMv4T limits.
    /// </summary>
    [TestClass]
    public class TestAssembler
    {
        [DataTestMethod]
        [DataRow(false, "add r0, r1, r2", "020081E0")]
        [DataRow(false, "adds r0, r1, #255", "FF0091E2")]
        [DataRow(false, "addeq r0, r1, #0x3FC00", "FF0B8102")]
        [DataRow(false, "sub r3, r4, r5, lsl #3", "853144E0")]
        [DataRow(false, "rsb r0, r0, #0", "000060E2")]
        [DataRow(false, "subs r1, r1, r2, lsr r3", "321351E0")]
        [DataRow(false, "and r0, r1, #0xFF000000", "FF0401E2")]
        [DataRow(false, "orr r2, r2, r3, ror #8", "632482E1")]
        [DataRow(false, "eor r0, r0, r0, asr #32", "400020E0")]
        [DataRow(false, "bic r0, r0, #0xF", "0F00C0E3")]
        [DataRow(false, "mov r0, #0x400", "010BA0E3")]
        [DataRow(false, "mvn r0, #0", "0000E0E3")]
        [DataRow(false, "mov r0, #0xFFFFFFFF", "0000E0E3")]
        [DataRow(false, "mov r0, r1, rrx", "6100A0E1")]
        [DataRow(false, "movs pc, lr", "0EF0B0E1")]
        [DataRow(false, "cmp r0, #1", "010050E3")]
        [DataRow(false, "cmn r0, #1", "010070E3")]
        [DataRow(false, "cmp r0, #-1", "010070E3")]
        [DataRow(false, "tst r1, #0x80000000", "020111E3")]
        [DataRow(false, "teq r2, r3", "030032E1")]
        [DataRow(false, "add r0, r0, #-4", "040040E2")]
        [DataRow(false, "sub r0, r0, #-4", "040080E2")]
        [DataRow(false, "and r0, r0, #0xFFFFFF00", "FF00C0E3")]
        [DataRow(false, "lsl r0, r1, #2", "0101A0E1")]
        [DataRow(false, "lsr r0, r1, r2", "3102A0E1")]
        [DataRow(false, "asr r0, r1, #31", "C10FA0E1")]
        [DataRow(false, "ror r0, r0, #4", "6002A0E1")]
        [DataRow(false, "mul r0, r1, r2", "910200E0")]
        [DataRow(false, "mla r0, r1, r2, r3", "913220E0")]
        [DataRow(false, "muls r0, r1, r2", "910210E0")]
        [DataRow(false, "umull r0, r1, r2, r3", "920381E0")]
        [DataRow(false, "smlal r0, r1, r2, r3", "9203E1E0")]
        [DataRow(false, "smullne r4, r5, r6, r7", "9647C510")]
        [DataRow(false, "smlabb r0, r1, r2, r3", "813200E1")]
        [DataRow(false, "smlatb r0, r1, r2, r3", "A13200E1")]
        [DataRow(false, "smulbt r0, r1, r2", "C10260E1")]
        [DataRow(false, "smultt r0, r1, r2", "E10260E1")]
        [DataRow(false, "smlawb r0, r1, r2, r3", "813220E1")]
        [DataRow(false, "smulwt r0, r1, r2", "E10220E1")]
        [DataRow(false, "smlalbb r0, r1, r2, r3", "820341E1")]
        [DataRow(false, "qadd r0, r1, r2", "510002E1")]
        [DataRow(false, "qdsub r0, r1, r2", "510062E1")]
        [DataRow(false, "clz r0, r1", "110F6FE1")]
        [DataRow(false, "ldr r0, [r1]", "000091E5")]
        [DataRow(false, "ldr r0, [r1, #4]", "040091E5")]
        [DataRow(false, "ldr r0, [r1, #-4]!", "040031E5")]
        [DataRow(false, "ldr r0, [r1], #4", "040091E4")]
        [DataRow(false, "ldr r0, [r1, r2]", "020091E7")]
        [DataRow(false, "ldr r0, [r1, -r2, lsl #2]", "020111E7")]
        [DataRow(false, "ldr r0, [r1], r2, asr #1", "C20091E6")]
        [DataRow(false, "ldrb r0, [r1, #0xFFF]", "FF0FD1E5")]
        [DataRow(false, "strb r0, [r1, #-1]", "010041E5")]
        [DataRow(false, "str r0, [sp, #-4]!", "04002DE5")]
        [DataRow(false, "ldrt r0, [r1], #4", "0400B1E4")]
        [DataRow(false, "strbt r0, [r1]", "0000E1E4")]
        [DataRow(false, "ldrh r0, [r1, #2]", "B200D1E1")]
        [DataRow(false, "ldrsh r0, [r1, #-2]", "F20051E1")]
        [DataRow(false, "ldrsb r0, [r1, r2]", "D20091E1")]
        [DataRow(false, "strh r0, [r1], #-8", "B80041E0")]
        [DataRow(false, "ldrd r0, r1, [r2, #8]", "D800C2E1")]
        [DataRow(false, "strd r2, r3, [sp, #-16]!", "F0216DE1")]
        [DataRow(false, "ldrh r0, [r1, -r2]!", "B20031E1")]
        [DataRow(false, "ldmia r0!, {r1, r2, r3}", "0E00B0E8")]
        [DataRow(false, "ldmfd sp!, {r4-r11, pc}", "F08FBDE8")]
        [DataRow(false, "stmdb sp!, {r4, lr}", "10402DE9")]
        [DataRow(false, "stmia r0, {r0, r1}", "030080E8")]
        [DataRow(false, "ldmib r0, {r1}", "020090E9")]
        [DataRow(false, "stmda r1, {r2}", "040001E8")]
        [DataRow(false, "push {r4, r5, lr}", "30402DE9")]
        [DataRow(false, "pop {r4, r5, pc}", "3080BDE8")]
        [DataRow(false, "ldm r0, {r1}^", "0200D0E8")]
        [DataRow(false, "swp r0, r1, [r2]", "910002E1")]
        [DataRow(false, "swpb r0, r1, [r2]", "910042E1")]
        [DataRow(false, "mrs r0, cpsr", "00000FE1")]
        [DataRow(false, "mrs r0, spsr", "00004FE1")]
        [DataRow(false, "msr cpsr_fc, r0", "00F029E1")]
        [DataRow(false, "msr cpsr_f, #0xF0000000", "0FF228E3")]
        [DataRow(false, "msr spsr_fsxc, r1", "01F06FE1")]
        [DataRow(false, "msr cpsr_c, #0x1F", "1FF021E3")]
        [DataRow(false, "mrc p15, 0, r0, c1, c0, 0", "100F11EE")]
        [DataRow(false, "mcr p15, 0, r0, c7, c10, 4", "9A0F07EE")]
        [DataRow(false, "mcr p15, 0, r0, c9, c1, 0", "110F09EE")]
        [DataRow(false, "cdp p14, 1, c2, c3, c4, 5", "A42E13EE")]
        [DataRow(false, "bx lr", "1EFF2FE1")]
        [DataRow(false, "blx r3", "33FF2FE1")]
        [DataRow(false, "bxeq r0", "10FF2F01")]
        [DataRow(false, "swi 0x123456", "563412EF")]
        [DataRow(false, "svc 5", "050000EF")]
        [DataRow(false, "bkpt #0x1234", "742321E1")]
        [DataRow(false, "nop", "0000A0E1")]
        [DataRow(false, "pld [r0, #32]", "20F0D0F5")]
        [DataRow(true, "lsls r0, r1, #2", "8800")]
        [DataRow(true, "lsrs r2, r3, #32", "1A08")]
        [DataRow(true, "asrs r4, r5, #1", "6C10")]
        [DataRow(true, "adds r0, r1, r2", "8818")]
        [DataRow(true, "subs r0, r1, #7", "C81F")]
        [DataRow(true, "adds r0, #200", "C830")]
        [DataRow(true, "movs r7, #255", "FF27")]
        [DataRow(true, "cmp r3, #0", "002B")]
        [DataRow(true, "subs r1, #1", "0139")]
        [DataRow(true, "ands r0, r1", "0840")]
        [DataRow(true, "eors r2, r3", "5A40")]
        [DataRow(true, "lsls r0, r1", "8840")]
        [DataRow(true, "lsrs r0, r1", "C840")]
        [DataRow(true, "asrs r0, r1", "0841")]
        [DataRow(true, "adcs r0, r1", "4841")]
        [DataRow(true, "sbcs r0, r1", "8841")]
        [DataRow(true, "rors r0, r1", "C841")]
        [DataRow(true, "tst r0, r1", "0842")]
        [DataRow(true, "negs r0, r1", "4842")]
        [DataRow(true, "cmp r0, r1", "8842")]
        [DataRow(true, "cmn r0, r1", "C842")]
        [DataRow(true, "orrs r0, r1", "0843")]
        [DataRow(true, "muls r0, r1, r0", "4843")]
        [DataRow(true, "bics r0, r1", "8843")]
        [DataRow(true, "mvns r0, r1", "C843")]
        [DataRow(true, "add r8, r9", "C844")]
        [DataRow(true, "add r0, r8", "4044")]
        [DataRow(true, "cmp r8, r0", "8045")]
        [DataRow(true, "mov r8, r0", "8046")]
        [DataRow(true, "mov sp, r0", "8546")]
        [DataRow(true, "bx lr", "7047")]
        [DataRow(true, "blx r2", "9047")]
        [DataRow(true, "ldr r0, [sp, #1020]", "FF98")]
        [DataRow(true, "str r1, [sp, #4]", "0191")]
        [DataRow(true, "ldr r0, [r1, r2]", "8858")]
        [DataRow(true, "strb r0, [r1, r2]", "8854")]
        [DataRow(true, "strh r0, [r1, r2]", "8852")]
        [DataRow(true, "ldrsb r0, [r1, r2]", "8856")]
        [DataRow(true, "ldrsh r0, [r1, r2]", "885E")]
        [DataRow(true, "ldrb r0, [r1, r2]", "885C")]
        [DataRow(true, "ldrh r0, [r1, r2]", "885A")]
        [DataRow(true, "ldr r0, [r1, #124]", "C86F")]
        [DataRow(true, "str r0, [r1]", "0860")]
        [DataRow(true, "ldrb r0, [r1, #31]", "C87F")]
        [DataRow(true, "strb r0, [r1, #1]", "4870")]
        [DataRow(true, "ldrh r0, [r1, #62]", "C88F")]
        [DataRow(true, "strh r0, [r1, #2]", "4880")]
        [DataRow(true, "add r0, sp, #8", "02A8")]
        [DataRow(true, "add sp, #508", "7FB0")]
        [DataRow(true, "sub sp, #16", "84B0")]
        [DataRow(true, "add sp, sp, #4", "01B0")]
        [DataRow(true, "push {r0, r1, lr}", "03B5")]
        [DataRow(true, "pop {r4-r7, pc}", "F0BD")]
        [DataRow(true, "push {r0}", "01B4")]
        [DataRow(true, "stmia r0!, {r1, r2}", "06C0")]
        [DataRow(true, "ldmia r1!, {r0, r2}", "05C9")]
        [DataRow(true, "swi 0x10", "10DF")]
        [DataRow(true, "svc 6", "06DF")]
        [DataRow(true, "bkpt 0xAB", "ABBE")]
        [DataRow(true, "nop", "C046")]
        public void MatchesLlvmMc(bool thumb, string text, string bytes)
        {
            var result = thumb ? ThumbAssembler.Assemble(text, 0x02000000) : ArmAssembler.Assemble(text, 0x02000000);
            Assert.IsTrue(result.Success, $"{text}: {result.Error}");
            Assert.AreEqual(bytes, Convert.ToHexString(result.Bytes), text);
        }

        private static AssemblerContext Labels(params (string Name, uint Value)[] labels) => new()
        {
            ResolveSymbol = name => labels.FirstOrDefault(l => l.Name == name) is { Name: not null } l ? l.Value : null,
        };

        [TestMethod]
        public void BranchesUseAbsoluteTargetsAndLabels()
        {
            var ctx = Labels((":loop", 0x02000000), ("far", 0x02100000));
            Assert.AreEqual(0xEAFFFFFEu, ArmAssembler.Assemble("b :loop", 0x02000000, ctx).Word, "branch to itself");
            Assert.AreEqual(0x0B03FFFEu, ArmAssembler.Assemble("bleq far", 0x02000000, ctx).Word);
            Assert.AreEqual(0xFB000000u, ArmAssembler.Assemble("blx 0x0200000A", 0x02000000).Word, "blx to Thumb code sets H");
            Assert.AreEqual(0xE7FE, ThumbAssembler.Assemble("b 0x02000000", 0x02000000).Half);
            Assert.AreEqual(0xD0FE, ThumbAssembler.Assemble("beq :loop", 0x02000000, ctx).Half);
            Assert.AreEqual("FFF7FEFF", Convert.ToHexString(ThumbAssembler.Assemble("bl 0x02000000", 0x02000000).Bytes));
            // blx from a halfword address counts from the word-aligned PC.
            var blx = ThumbAssembler.Assemble("blx 0x02000100", 0x02000002);
            Assert.AreEqual("blx 0x02000100", Common.Disassembler.ThumbDisassembler.Disassemble(blx.Half, 0x02000002, BitConverter.ToUInt16(blx.Bytes, 2)));
        }

        [TestMethod]
        public void RangeAndAlignmentErrorsExplainThemselves()
        {
            StringAssert.Contains(ThumbAssembler.Assemble("beq 0x02001000", 0x02000000).Error, "out of range");
            StringAssert.Contains(ThumbAssembler.Assemble("b 0x02000001", 0x02000000).Error, "aligned");
            StringAssert.Contains(ArmAssembler.Assemble("b 0x08000000", 0x02000000).Error, "out of range");
            StringAssert.Contains(ArmAssembler.Assemble("mov r0, #0x12345678", 0).Error, "ldr rX, =value");
            StringAssert.Contains(ArmAssembler.Assemble("ldr r0, [r1, #5000]", 0).Error, "out of range");
            StringAssert.Contains(ArmAssembler.Assemble("bogus r0", 0).Error, "Unknown ARM instruction");
            StringAssert.Contains(ArmAssembler.Assemble("b nowhere", 0).Error, "Unknown label");
            StringAssert.Contains(ThumbAssembler.Assemble("ldr r8, [r0]", 0).Error, "low register");
            StringAssert.Contains(ThumbAssembler.Assemble("add r0, r1, #9", 0).Error, "0-7");
        }

        [TestMethod]
        public void LiteralsUseThePoolOrAnImmediate()
        {
            Assert.AreEqual(0xE3E00000u, ArmAssembler.Assemble("ldr r0, =0xFFFFFFFF", 0).Word, "no pool: mvn");
            Assert.AreEqual(0x2005, ThumbAssembler.Assemble("ldr r0, =5", 0).Half, "no pool: movs");
            var pool = new List<uint>();
            var ctx = new AssemblerContext { AllocateLiteral = v => { pool.Add(v); return 0x02000100u; } };
            Assert.AreEqual(0xE59F00F8u, ArmAssembler.Assemble("ldr r0, =0x12345678", 0x02000000, ctx).Word);
            Assert.AreEqual(0x483F, ThumbAssembler.Assemble("ldr r0, =0x12345678", 0x02000000, ctx).Half);
            CollectionAssert.AreEqual(new[] { 0x12345678u, 0x12345678u }, pool);
            Assert.AreEqual(0xE59F00F8u, ArmAssembler.Assemble("ldr r0, [0x02000100]", 0x02000000).Word, "PC-relative as the disassembler prints it");
            Assert.AreEqual(0xE28F00F8u, ArmAssembler.Assemble("adr r0, 0x02000100", 0x02000000).Word);
        }

        [TestMethod]
        public void SizePassAcceptsUnknownLabels()
        {
            var ctx = new AssemblerContext { AllowUnresolved = true };
            Assert.IsTrue(ArmAssembler.Assemble("bl :later", 0x02000000, ctx).Success);
            Assert.AreEqual(4, ThumbAssembler.Assemble("bl :later", 0x02000000, ctx).Bytes.Length);
        }

        [TestMethod]
        public void TheArm7RefusesArmV5Instructions()
        {
            var v4 = new AssemblerContext { IsArmV5 = false };
            foreach (string line in new[] { "clz r0, r1", "blx r0", "qadd r0, r1, r2", "smulbb r0, r1, r2", "ldrd r0, r1, [r2]", "bkpt 1" })
                StringAssert.Contains(ArmAssembler.Assemble(line, 0, v4).Error, "ARMv5", line);
            StringAssert.Contains(ThumbAssembler.Assemble("blx r0", 0, v4).Error, "ARMv5");
            Assert.IsTrue(ArmAssembler.Assemble("bx lr", 0, v4).Success);
        }

        [TestMethod]
        public void SuffixOrderAndSpellingsAreFlexible()
        {
            Assert.AreEqual(ArmAssembler.Assemble("addseq r0, r1, r2", 0).Word, ArmAssembler.Assemble("addeqs r0, r1, r2", 0).Word);
            Assert.AreEqual(ArmAssembler.Assemble("ldrbeq r0, [r1]", 0).Word, ArmAssembler.Assemble("ldreqb r0, [r1]", 0).Word);
            Assert.AreEqual(ArmAssembler.Assemble("ldmiaeq r0, {r1}", 0).Word, ArmAssembler.Assemble("ldmeqia r0, {r1}", 0).Word);
            Assert.AreEqual(ArmAssembler.Assemble("mov r0, #0x10", 0).Word, ArmAssembler.Assemble("MOV R0, $10", 0).Word);
            Assert.AreEqual(ArmAssembler.Assemble("add r0, r0, #4", 0).Word, ArmAssembler.Assemble("add r0, #4", 0).Word);
            Assert.AreEqual(0xE12FFF1Cu, ArmAssembler.Assemble("bx ip", 0).Word);
            Assert.AreEqual(0xE7FFDEFEu, ArmAssembler.Assemble("undefined 0xE7FFDEFE", 0).Word);
        }
    }
}
