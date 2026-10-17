using Px86.NintendoDS.Common.Cpu;
using Px86.NintendoDS.Common.Cpu.Coprocessors;
using static Px86.NintendoDS.TestProject.Cpu.CpuTestHarness;

namespace Px86.NintendoDS.TestProject.Cpu
{
    [TestClass]
    public class TestArmBranchesAndExceptions
    {
        [TestMethod]
        public void Bl_SetsLinkRegister()
        {
            var cpu = CreateArm(ArmArchitecture.ARMv4T,
                0xE3A00020u, // mov r0, #0x20
                0xEB000008u); // bl (pc + 8 + 32)
            Run(cpu, 2);
            Assert.AreEqual(0x2Cu, cpu.Pc, "target = 4 + 8 + 32");
            Assert.AreEqual(8u, cpu.R[14], "LR = address of the next instruction");
        }

        [TestMethod]
        public void Bx_SwitchesToThumb()
        {
            var cpu = CreateArm(ArmArchitecture.ARMv4T,
                0xE3A00011u, // mov r0, #0x11
                0xE12FFF10u); // bx r0
            Run(cpu, 2);
            Assert.IsTrue(cpu.IsThumb);
            Assert.AreEqual(0x10u, cpu.Pc);
        }

        [TestMethod]
        public void Swi_HleHandlerReceivesFunctionNumber()
        {
            uint? received = null;
            var cpu = CreateArm(ArmArchitecture.ARMv5TE, 0xEF060000u); // swi 0x060000
            cpu.SwiHandler = (_, number) => { received = number; return true; };
            Run(cpu, 1);
            Assert.AreEqual(6u, received, "ARM SWI number is bits 23–16");
            Assert.AreEqual(4u, cpu.Pc, "handled SWI continues with the next instruction");
            Assert.AreEqual(ProcessorMode.Supervisor, cpu.Mode);
        }

        [TestMethod]
        public void Swi_WithoutHandlerEntersSupervisorVector()
        {
            var cpu = CreateArm(ArmArchitecture.ARMv4T, 0xEF060000u);
            cpu.SetCpsr(0x1F); // System, IRQs enabled
            Run(cpu, 1);
            Assert.AreEqual(ProcessorMode.Supervisor, cpu.Mode);
            Assert.AreEqual(ArmCpu.VECTOR_SWI, cpu.Pc);
            Assert.AreEqual(4u, cpu.R[14], "LR = next instruction");
            Assert.AreEqual(0x1Fu, cpu.Spsr, "SPSR = old CPSR");
            Assert.AreNotEqual(0u, cpu.Cpsr & ArmCpu.FLAG_I, "IRQs disabled in the handler");
        }

        [TestMethod]
        public void Irq_IsTakenAndReturnsWithSubsPc()
        {
            var cpu = CreateArm(ArmArchitecture.ARMv4T,
                0xE3A00001u, // 0x00: mov r0, #1
                0xE3A01002u); // 0x04: mov r1, #2
            Memory(cpu).Write32(0x18, 0xE25EF004u); // IRQ vector: subs pc, lr, #4
            cpu.SetCpsr(0x1F);
            cpu.IrqLine = true;

            Run(cpu, 1);
            Assert.AreEqual(ProcessorMode.Irq, cpu.Mode);
            Assert.AreEqual(0x18u, cpu.Pc);
            Assert.AreEqual(4u, cpu.R[14], "LR_irq = interrupted instruction + 4");

            cpu.IrqLine = false;
            Run(cpu, 1);
            Assert.AreEqual(ProcessorMode.System, cpu.Mode, "SUBS PC restores CPSR from SPSR");
            Assert.AreEqual(0u, cpu.Pc, "returns to the interrupted instruction");

            Run(cpu, 1);
            Assert.AreEqual(1u, cpu.R[0]);
        }

        [TestMethod]
        public void Irq_MaskedByCpsrIBit()
        {
            var cpu = CreateArm(ArmArchitecture.ARMv4T, 0xE3A00001u);
            cpu.IrqLine = true; // reset state has I set
            Run(cpu, 1);
            Assert.AreEqual(1u, cpu.R[0], "instruction executes; IRQ stays pending");
            Assert.AreEqual(ProcessorMode.Supervisor, cpu.Mode);
        }

        [TestMethod]
        public void ArmV5_SaturationClzAndHalfwordMultiply()
        {
            var cpu = CreateArm(ArmArchitecture.ARMv5TE,
                0xE3A0047Fu, // mov r0, #0x7F000000
                0xE1001050u, // qadd r1, r0, r0
                0xE16F2F10u, // clz r2, r0
                0xE3A03801u, // mov r3, #0x10000
                0xE1640383u, // smulbb r4, r3, r3
                0xE16503E3u); // smultt r5, r3, r3
            Run(cpu, 6);
            Assert.AreEqual(0x7FFFFFFFu, cpu.R[1], "QADD saturates");
            Assert.IsTrue(cpu.FlagQ, "Q is sticky after saturation");
            Assert.AreEqual(1u, cpu.R[2], "CLZ");
            Assert.AreEqual(0u, cpu.R[4], "SMULBB of the low halves");
            Assert.AreEqual(1u, cpu.R[5], "SMULTT of the high halves");
        }

        [TestMethod]
        public void ArmV4_V5OnlyInstructionsAreUndefined()
        {
            var cpu = CreateArm(ArmArchitecture.ARMv4T, 0xE16F2F10u); // clz r2, r0
            uint? unknown = null;
            cpu.UnknownInstruction += (_, _, op) => unknown = op;
            Run(cpu, 1);
            Assert.AreEqual(0xE16F2F10u, unknown);
            Assert.AreEqual(ProcessorMode.Undefined, cpu.Mode);
            Assert.AreEqual(ArmCpu.VECTOR_UNDEFINED, cpu.Pc);
        }

        [TestMethod]
        public void Cp15_WaitForInterruptHaltsAndMainIdReads()
        {
            var cpu = CreateArm(ArmArchitecture.ARMv5TE,
                0xE3A00000u, // mov r0, #0
                0xEE070F90u, // mcr p15, 0, r0, c7, c0, 4
                0xEE101F10u); // mrc p15, 0, r1, c0, c0, 0
            cpu.Cp15 = new Cp15(cpu);
            Run(cpu, 2);
            Assert.IsTrue(cpu.Halted, "C7,C0,4 is wait-for-interrupt");

            cpu.Halted = false;
            Run(cpu, 1);
            Assert.AreEqual(Cp15.MAIN_ID, cpu.R[1]);
        }

        [TestMethod]
        public void Cp15_DtcmRegionDecoding()
        {
            var cpu = CreateArm(ArmArchitecture.ARMv5TE);
            var cp15 = new Cp15(cpu);
            int changes = 0;
            cp15.TcmConfigurationChanged += () => changes++;

            cp15.Write(0, 9, 1, 0, 0x0080000Au); // base 0x00800000, size code 5
            cp15.Write(0, 9, 1, 1, 0x00000020u); // ITCM size code 16
            cp15.Write(0, 1, 0, 0, Cp15.CTRL_DTCM_ENABLE | Cp15.CTRL_ITCM_ENABLE | Cp15.CTRL_HIGH_VECTORS);

            Assert.AreEqual(0x00800000u, cp15.DtcmBase);
            Assert.AreEqual(16u * 1024, cp15.DtcmSize, "512 << 5");
            Assert.AreEqual(32u * 1024 * 1024, cp15.ItcmSize, "512 << 16");
            Assert.IsTrue(cp15.DtcmEnabled && cp15.ItcmEnabled);
            Assert.AreEqual(0xFFFF0000u, cpu.ExceptionBase, "control bit 13 selects high vectors");
            Assert.AreEqual(3, changes);
            Assert.AreEqual(0x78u, cp15.Control & 0x78u, "bits 3–6 read as 1");
        }
    }
}
