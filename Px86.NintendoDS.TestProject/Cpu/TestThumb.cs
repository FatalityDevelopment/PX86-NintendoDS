using Px86.NintendoDS.Common.Cpu;
using static Px86.NintendoDS.TestProject.Cpu.CpuTestHarness;

namespace Px86.NintendoDS.TestProject.Cpu
{
    [TestClass]
    public class TestThumb
    {
        [TestMethod]
        public void Alu_ArithmeticAndShifts()
        {
            var cpu = CreateThumb(ArmArchitecture.ARMv4T,
                0x200A, // movs r0, #10
                0x2103, // movs r1, #3
                0x1842, // adds r2, r0, r1
                0x1A0B, // subs r3, r1, r0
                0x0084, // lsls r4, r0, #2
                0x105D, // asrs r5, r3, #1
                0x4341, // muls r1, r0, r1
                0x4246, // negs r6, r0
                0x280A); // cmp r0, #10
            Run(cpu, 9);
            Assert.AreEqual(13u, cpu.R[2], "ADD");
            Assert.AreEqual(0xFFFFFFF9u, cpu.R[3], "SUB");
            Assert.AreEqual(40u, cpu.R[4], "LSL");
            Assert.AreEqual(0xFFFFFFFCu, cpu.R[5], "ASR");
            Assert.AreEqual(30u, cpu.R[1], "MUL");
            Assert.AreEqual(0xFFFFFFF6u, cpu.R[6], "NEG");
            Assert.IsTrue(cpu.FlagZ && cpu.FlagC, "CMP equal sets Z and C");
            Assert.AreEqual(18u, cpu.Pc);
        }

        [TestMethod]
        public void LoadStore_ImmediateRegisterAndSignExtended()
        {
            var cpu = CreateThumb(ArmArchitecture.ARMv5TE,
                0x2080, // movs r0, #0x80
                0x0200, // lsls r0, r0, #8
                0x2120, // movs r1, #0x20
                0x6208, // str r0, [r1, #0x20]
                0x6A0A, // ldr r2, [r1, #0x20]
                0x8208, // strh r0, [r1, #0x10]
                0x7C4B, // ldrb r3, [r1, #0x11]
                0x2410, // movs r4, #0x10
                0x5F0D); // ldrsh r5, [r1, r4]
            Run(cpu, 9);
            Assert.AreEqual(0x8000u, cpu.R[2]);
            Assert.AreEqual(0x80u, cpu.R[3]);
            Assert.AreEqual(0xFFFF8000u, cpu.R[5]);
        }

        [TestMethod]
        public void PushPop_AndSpRelativeAdd()
        {
            var cpu = CreateThumb(ArmArchitecture.ARMv4T,
                0x2001, // movs r0, #1
                0x2102, // movs r1, #2
                0xB503, // push {r0, r1, lr}
                0xBC0C, // pop {r2, r3}
                0xAC00); // add r4, sp, #0
            cpu.R[13] = 0x200;
            cpu.R[14] = 0xABCD;
            Run(cpu, 5);
            Assert.AreEqual(0xABCDu, Memory(cpu).Read32(0x1FC), "LR pushed last (highest address)");
            Assert.AreEqual(1u, cpu.R[2]);
            Assert.AreEqual(2u, cpu.R[3]);
            Assert.AreEqual(0x1FCu, cpu.R[13]);
            Assert.AreEqual(0x1FCu, cpu.R[4]);
        }

        [TestMethod]
        public void ConditionalBranch_Taken()
        {
            var cpu = CreateThumb(ArmArchitecture.ARMv4T,
                0x2005, // movs r0, #5
                0x2805, // cmp r0, #5
                0xD002, // beq +4
                0x2101, // movs r1, #1
                0x2202); // movs r2, #2
            Run(cpu, 3);
            Assert.AreEqual(12u, cpu.Pc, "target = 4 + 4 + 4");
            Assert.AreEqual(0u, cpu.R[1]);
        }

        [TestMethod]
        public void BlPair_SetsThumbLinkRegister()
        {
            var cpu = CreateThumb(ArmArchitecture.ARMv4T, 0xF000, 0xF820); // bl +0x40
            Run(cpu, 2);
            Assert.AreEqual(0x44u, cpu.Pc);
            Assert.AreEqual(5u, cpu.R[14], "LR = return address | 1");
            Assert.IsTrue(cpu.IsThumb);
        }

        [TestMethod]
        public void BlxPair_ArmV5SwitchesToArm()
        {
            var cpu = CreateThumb(ArmArchitecture.ARMv5TE, 0xF000, 0xE820); // blx +0x40
            Run(cpu, 2);
            Assert.AreEqual(0x44u, cpu.Pc);
            Assert.IsFalse(cpu.IsThumb, "BLX suffix switches to ARM");
            Assert.AreEqual(5u, cpu.R[14]);
        }

        [TestMethod]
        public void Stmia_BaseInListStoresOldOrNewBaseByArchitecture()
        {
            ushort[] program =
            {
                0x20C0, // movs r0, #0xC0
                0x2180, // movs r1, #0x80
                0xC103, // stmia r1!, {r0, r1}
                0x3908, // subs r1, #8
                0xC90C, // ldmia r1!, {r2, r3}
            };

            var v4 = CreateThumb(ArmArchitecture.ARMv4T, program);
            Run(v4, 5);
            Assert.AreEqual(0xC0u, v4.R[2]);
            Assert.AreEqual(0x88u, v4.R[3], "ARMv4 stores the new base when Rb isn't first");
            Assert.AreEqual(0x88u, v4.R[1]);

            var v5 = CreateThumb(ArmArchitecture.ARMv5TE, program);
            Run(v5, 5);
            Assert.AreEqual(0x80u, v5.R[3], "ARMv5 always stores the old base");
        }

        [TestMethod]
        public void Swi_EntersArmSupervisor()
        {
            var cpu = CreateThumb(ArmArchitecture.ARMv4T, 0xDF05); // swi 5
            uint? number = null;
            cpu.SwiHandler = (_, n) => { number = n; return false; };
            Run(cpu, 1);
            Assert.AreEqual(5u, number, "Thumb SWI number is the low byte");
            Assert.IsFalse(cpu.IsThumb);
            Assert.AreEqual(ArmCpu.VECTOR_SWI, cpu.Pc);
            Assert.AreEqual(2u, cpu.R[14]);
        }
    }
}
