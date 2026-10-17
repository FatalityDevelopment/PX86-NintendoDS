using Px86.NintendoDS.Common.Cpu;
using static Px86.NintendoDS.TestProject.Cpu.CpuTestHarness;

namespace Px86.NintendoDS.TestProject.Cpu
{
    [TestClass]
    public class TestArmLoadStore
    {
        private static readonly uint[] MisalignedProgram =
        {
            0xE3A01C01u, // mov r1, #0x100
            0xE3A02411u, // mov r2, #0x11000000
            0xE3822822u, // orr r2, r2, #0x220000
            0xE3822C33u, // orr r2, r2, #0x3300
            0xE3822084u, // orr r2, r2, #0x84      → r2 = 0x11223384
            0xE5812000u, // str r2, [r1]
            0xE5913001u, // ldr r3, [r1, #1]
            0xE1D140B2u, // ldrh r4, [r1, #2]
            0xE1D150D0u, // ldrsb r5, [r1]
            0xE1D160F2u, // ldrsh r6, [r1, #2]
            0xE1D170B1u, // ldrh r7, [r1, #1]
        };

        [TestMethod]
        public void LdrStr_PreAndPostIndexWithWriteback()
        {
            var cpu = CreateArm(ArmArchitecture.ARMv5TE,
                0xE3A01C01u, // mov r1, #0x100
                0xE3A00012u, // mov r0, #0x12
                0xE5810000u, // str r0, [r1]
                0xE5912000u, // ldr r2, [r1]
                0xE5E10005u, // strb r0, [r1, #5]!
                0xE5D13000u, // ldrb r3, [r1]
                0xE4114005u); // ldr r4, [r1], #-5
            Run(cpu, 7);
            Assert.AreEqual(0x12u, cpu.R[2], "LDR reads back STR");
            Assert.AreEqual(0x12u, cpu.R[3], "LDRB at the written-back base");
            Assert.AreEqual(0x12u, cpu.R[4], "misaligned LDR at 0x105 rotates the byte into bits 7–0");
            Assert.AreEqual(0x100u, cpu.R[1], "post-index writeback subtracts 5");
        }

        [TestMethod]
        public void MisalignedLoads_ArmV5()
        {
            var cpu = CreateArm(ArmArchitecture.ARMv5TE, MisalignedProgram);
            Run(cpu, MisalignedProgram.Length);
            Assert.AreEqual(0x84112233u, cpu.R[3], "LDR rotates by 8 × (addr & 3)");
            Assert.AreEqual(0x1122u, cpu.R[4], "LDRH");
            Assert.AreEqual(0xFFFFFF84u, cpu.R[5], "LDRSB sign-extends");
            Assert.AreEqual(0x1122u, cpu.R[6], "LDRSH of a positive halfword");
            Assert.AreEqual(0x3384u, cpu.R[7], "ARMv5 misaligned LDRH reads the aligned halfword");
        }

        [TestMethod]
        public void MisalignedLoads_ArmV4RotatesHalfword()
        {
            var cpu = CreateArm(ArmArchitecture.ARMv4T, MisalignedProgram);
            Run(cpu, MisalignedProgram.Length);
            Assert.AreEqual(0x84000033u, cpu.R[7], "ARMv4 misaligned LDRH rotates by 8");
        }

        [TestMethod]
        public void StmdbLdmia_PushPopRoundTrip()
        {
            var cpu = CreateArm(ArmArchitecture.ARMv4T,
                0xE3A0DC02u, // mov sp, #0x200
                0xE3A00001u, // mov r0, #1
                0xE3A01002u, // mov r1, #2
                0xE3A02003u, // mov r2, #3
                0xE92D0007u, // stmdb sp!, {r0-r2}
                0xE8BD0070u); // ldmia sp!, {r4-r6}
            Run(cpu, 5);
            Assert.AreEqual(0x1F4u, cpu.R[13], "STMDB writeback");
            Assert.AreEqual(1u, Memory(cpu).Read32(0x1F4), "lowest register at lowest address");
            Assert.AreEqual(3u, Memory(cpu).Read32(0x1FC));

            Run(cpu, 1);
            Assert.AreEqual(1u, cpu.R[4]);
            Assert.AreEqual(2u, cpu.R[5]);
            Assert.AreEqual(3u, cpu.R[6]);
            Assert.AreEqual(0x200u, cpu.R[13], "LDMIA writeback");
        }

        [TestMethod]
        public void ArmV5_LdrdStrd()
        {
            var cpu = CreateArm(ArmArchitecture.ARMv5TE,
                0xE3A06C01u, // mov r6, #0x100
                0xE3A07055u, // mov r7, #0x55
                0xE1C660F0u, // strd r6, r7, [r6]
                0xE1C680D0u); // ldrd r8, r9, [r6]
            Run(cpu, 4);
            Assert.AreEqual(0x100u, Memory(cpu).Read32(0x100));
            Assert.AreEqual(0x55u, Memory(cpu).Read32(0x104));
            Assert.AreEqual(0x100u, cpu.R[8]);
            Assert.AreEqual(0x55u, cpu.R[9]);
        }
    }
}
