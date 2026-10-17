using Px86.NintendoDS.Common.Cpu;
using static Px86.NintendoDS.TestProject.Cpu.CpuTestHarness;

namespace Px86.NintendoDS.TestProject.Cpu
{
    [TestClass]
    public class TestArmDataProcessing
    {
        // ── Barrel shifter ────────────────────────────────────────────────────

        [TestMethod]
        public void Shifts_ImmediateForms_IncludingLsr32AndRrx()
        {
            var cpu = CreateArm(ArmArchitecture.ARMv5TE,
                0xE3A004FFu, // mov r0, #0xFF000000
                0xE1A01200u, // lsl r1, r0, #4
                0xE1B02020u, // lsrs r2, r0, #32
                0xE1A03240u, // asr r3, r0, #4
                0xE3A04001u, // mov r4, #1
                0xE1B05064u); // rrxs r5, r4
            Run(cpu, 6);

            Assert.AreEqual(0xF0000000u, cpu.R[1], "LSL #4");
            Assert.AreEqual(0u, cpu.R[2], "LSR #32 yields 0");
            Assert.AreEqual(0xFFF00000u, cpu.R[3], "ASR #4 sign-extends");
            Assert.AreEqual(0x80000000u, cpu.R[5], "RRX shifts the old carry (set by LSRS #32) into bit 31");
            Assert.IsTrue(cpu.FlagC, "RRX carry-out is bit 0 of the source");
            Assert.IsTrue(cpu.FlagN, "result is negative");
        }

        [TestMethod]
        public void Shifts_RegisterSpecifiedAmount()
        {
            var cpu = CreateArm(ArmArchitecture.ARMv4T,
                0xE3A00001u, // mov r0, #1
                0xE3A01004u, // mov r1, #4
                0xE1A02110u, // lsl r2, r0, r1
                0xE1A03210u); // lsl r3, r0, r2
            Run(cpu, 4);
            Assert.AreEqual(16u, cpu.R[2]);
            Assert.AreEqual(0x10000u, cpu.R[3]);
        }

        // ── Arithmetic flags ──────────────────────────────────────────────────

        [TestMethod]
        public void Adds_SetsCarryAndOverflow_AdcConsumesCarry()
        {
            var cpu = CreateArm(ArmArchitecture.ARMv5TE,
                0xE3A00102u, // mov r0, #0x80000000
                0xE0901000u); // adds r1, r0, r0
            Run(cpu, 2);
            Assert.AreEqual(0u, cpu.R[1]);
            Assert.IsTrue(cpu.FlagZ, "Z");
            Assert.IsTrue(cpu.FlagC, "C: unsigned overflow");
            Assert.IsTrue(cpu.FlagV, "V: signed overflow");

            cpu = CreateArm(ArmArchitecture.ARMv5TE, 0xE3A00102u, 0xE0901000u, 0xE2B12005u); // … adcs r2, r1, #5
            Run(cpu, 3);
            Assert.AreEqual(6u, cpu.R[2], "0 + 5 + carry");
            Assert.IsFalse(cpu.FlagC);
            Assert.IsFalse(cpu.FlagV);
        }

        [TestMethod]
        public void Subs_Rsbs_Sbc_BorrowSemantics()
        {
            var cpu = CreateArm(ArmArchitecture.ARMv5TE,
                0xE3A00003u, // mov r0, #3
                0xE2501005u); // subs r1, r0, #5
            Run(cpu, 2);
            Assert.AreEqual(0xFFFFFFFEu, cpu.R[1]);
            Assert.IsFalse(cpu.FlagC, "borrow clears C");
            Assert.IsTrue(cpu.FlagN);

            cpu = CreateArm(ArmArchitecture.ARMv5TE,
                0xE3A00003u, 0xE2501005u,
                0xE270200Au, // rsbs r2, r0, #10
                0xE2C03001u); // sbc r3, r0, #1
            Run(cpu, 4);
            Assert.AreEqual(7u, cpu.R[2]);
            Assert.AreEqual(2u, cpu.R[3], "SBC with C=1 subtracts no borrow");
        }

        [TestMethod]
        public void LogicalOperations()
        {
            var cpu = CreateArm(ArmArchitecture.ARMv4T,
                0xE3A000F0u, // mov r0, #0xF0
                0xE3A0100Fu, // mov r1, #0x0F
                0xE1802001u, // orr r2, r0, r1
                0xE0003001u, // and r3, r0, r1
                0xE22040FFu, // eor r4, r0, #0xFF
                0xE3C05030u, // bic r5, r0, #0x30
                0xE3E06000u); // mvn r6, #0
            Run(cpu, 7);
            Assert.AreEqual(0xFFu, cpu.R[2], "ORR");
            Assert.AreEqual(0u, cpu.R[3], "AND");
            Assert.AreEqual(0x0Fu, cpu.R[4], "EOR");
            Assert.AreEqual(0xC0u, cpu.R[5], "BIC");
            Assert.AreEqual(0xFFFFFFFFu, cpu.R[6], "MVN");
        }

        [TestMethod]
        public void ConditionCodes_SkipFailedInstructions()
        {
            var cpu = CreateArm(ArmArchitecture.ARMv4T,
                0xE3A00007u, // mov r0, #7
                0xE3500007u, // cmp r0, #7
                0x03A01001u, // moveq r1, #1
                0x13A02001u, // movne r2, #1
                0xE3500008u, // cmp r0, #8
                0xB3A03001u, // movlt r3, #1
                0xC3A04001u); // movgt r4, #1
            Run(cpu, 7);
            Assert.AreEqual(1u, cpu.R[1], "EQ taken");
            Assert.AreEqual(0u, cpu.R[2], "NE skipped");
            Assert.AreEqual(1u, cpu.R[3], "LT taken");
            Assert.AreEqual(0u, cpu.R[4], "GT skipped");
            Assert.AreEqual(28u, cpu.Pc, "PC advances past skipped instructions");
        }

        // ── Multiply ──────────────────────────────────────────────────────────

        [TestMethod]
        public void Multiply_MulMlaUmullSmull()
        {
            var cpu = CreateArm(ArmArchitecture.ARMv4T,
                0xE3A00006u, // mov r0, #6
                0xE3A01007u, // mov r1, #7
                0xE0020190u, // mul r2, r0, r1
                0xE0230190u, // mla r3, r0, r1, r0
                0xE3E04000u, // mvn r4, #0
                0xE0865494u, // umull r5, r6, r4, r4
                0xE0C87494u); // smull r7, r8, r4, r4
            Run(cpu, 7);
            Assert.AreEqual(42u, cpu.R[2], "MUL");
            Assert.AreEqual(48u, cpu.R[3], "MLA");
            Assert.AreEqual(1u, cpu.R[5], "UMULL lo");
            Assert.AreEqual(0xFFFFFFFEu, cpu.R[6], "UMULL hi");
            Assert.AreEqual(1u, cpu.R[7], "SMULL lo (-1 × -1)");
            Assert.AreEqual(0u, cpu.R[8], "SMULL hi");
        }

        // ── PSR transfer / mode banking ───────────────────────────────────────

        [TestMethod]
        public void Msr_SwitchesModeAndBanksStackPointer()
        {
            var cpu = CreateArm(ArmArchitecture.ARMv5TE,
                0xE10F0000u, // mrs r0, cpsr
                0xE3C0001Fu, // bic r0, r0, #0x1F
                0xE3800012u, // orr r0, r0, #0x12  (IRQ)
                0xE121F000u, // msr cpsr_c, r0
                0xE3A0DC03u, // mov sp, #0x300
                0xE10F1000u, // mrs r1, cpsr
                0xE381101Fu, // orr r1, r1, #0x1F  (System)
                0xE121F001u); // msr cpsr_c, r1
            Run(cpu, 5);
            Assert.AreEqual(ProcessorMode.Irq, cpu.Mode);
            Assert.AreEqual(0x300u, cpu.R[13]);

            Run(cpu, 3);
            Assert.AreEqual(ProcessorMode.System, cpu.Mode);
            Assert.AreEqual(0u, cpu.R[13], "System mode sees the User-bank SP");

            cpu.SwitchMode(ProcessorMode.Irq);
            Assert.AreEqual(0x300u, cpu.R[13], "IRQ bank kept its SP");
        }
    }
}
