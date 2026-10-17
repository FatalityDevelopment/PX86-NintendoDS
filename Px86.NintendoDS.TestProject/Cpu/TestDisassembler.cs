using Px86.NintendoDS.Common.Disassembler;

namespace Px86.NintendoDS.TestProject.Cpu
{
    [TestClass]
    public class TestDisassembler
    {
        [DataTestMethod]
        [DataRow(0xE0901000u, "adds r1, r0, r0")]
        [DataRow(0x03A01001u, "moveq r1, #0x1")]
        [DataRow(0xE1B02020u, "movs r2, r0, lsr #32")]
        [DataRow(0xE5E10005u, "strb r0, [r1, #0x5]!")]
        [DataRow(0xE4114005u, "ldr r4, [r1], #-0x5")]
        [DataRow(0xE92D0007u, "push {r0-r2}")]
        [DataRow(0xE12FFF10u, "bx r0")]
        [DataRow(0xE16F2F10u, "clz r2, r0")]
        [DataRow(0xE0865494u, "umull r5, r6, r4, r4")]
        [DataRow(0xE1640383u, "smulbb r4, r3, r3")]
        [DataRow(0xE121F000u, "msr cpsr_c, r0")]
        [DataRow(0xEE101F10u, "mrc p15, 0, r1, c0, c0, 0")]
        [DataRow(0xE1C680D0u, "ldrd r8, [r6]")]
        public void Arm(uint opcode, string expected)
        {
            Assert.AreEqual(expected, ArmDisassembler.Disassemble(opcode, 0));
        }

        [TestMethod]
        public void Arm_BranchTargetsArePcRelative()
        {
            Assert.AreEqual("bl 0x0000002C", ArmDisassembler.Disassemble(0xEB000008u, 4));
        }

        [DataTestMethod]
        [DataRow((ushort)0x1842, "adds r2, r0, r1")]
        [DataRow((ushort)0x4246, "negs r6, r0")]
        [DataRow((ushort)0xB503, "push {r0, r1, lr}")]
        [DataRow((ushort)0x5F0D, "ldrsh r5, [r1, r4]")]
        [DataRow((ushort)0xDF05, "swi 0x5")]
        [DataRow((ushort)0xD002, "beq 0x0000000C")]
        public void Thumb(ushort opcode, string expected)
        {
            Assert.AreEqual(expected, ThumbDisassembler.Disassemble(opcode, 4));
        }

        [TestMethod]
        public void Thumb_BlPairResolvesTarget()
        {
            Assert.AreEqual("bl 0x00000044", ThumbDisassembler.Disassemble(0xF000, 0, 0xF820));
            Assert.AreEqual("blx 0x00000044", ThumbDisassembler.Disassemble(0xF000, 0, 0xE820));
        }
    }
}
