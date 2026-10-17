using Px86.NintendoDS.EmulatorCore.Hosting;
using Px86.NintendoDS.EmulatorCore.Remote;
using Px86.NintendoDS.TestProject.Emulator;
using static Px86.NintendoDS.TestProject.Remote.TestPineClient;

namespace Px86.NintendoDS.TestProject.Remote
{
    [TestClass]
    public class TestPineSocket
    {
        private const byte Ok = PineOpcodes.IpcOk;
        private const byte Fail = PineOpcodes.IpcFail;

        private static (HostFixture Fixture, PineSocket Pine, TestPineClient Client) Start(string rom = "bench", bool paused = false)
        {
            var fixture = new HostFixture(rom, paused);
            var pine = new PineSocket(fixture.Host, port: 0);
            pine.Start();
            return (fixture, pine, new TestPineClient(pine.Port));
        }

        private static void WaitForDone(HostFixture f) =>
            f.WaitFor(() => TestRomRunner.Result9(f.Host.Emulator!, 0) == TestRomRunner.DoneMagic, "the ROM to start");

        [TestMethod]
        public void GameInfoAndStatus()
        {
            var (f, pine, c) = Start();
            using (f) using (pine) using (c)
            {
                var version = c.Send(Cmd(PineOpcodes.Version));
                Assert.AreEqual(Ok, version[4], "version ok");
                Assert.AreEqual(PineSocket.VersionString, Str(version, 5), "version string");

                var status = c.Send(Cmd(PineOpcodes.Status));
                Assert.AreEqual(9, status.Length, "status reply is fixed size");
                Assert.AreEqual((uint)HostStatus.Running, U32(status, 5), "running");

                var id = c.Send(Cmd(PineOpcodes.Id), Cmd(PineOpcodes.Title));
                Assert.AreEqual(Ok, id[4], "id + title ok");
                string code = Str(id, 5);
                Assert.AreEqual(f.Host.Emulator!.Rom!.Header.GameCode, code, "game code");
                Assert.AreEqual(f.Host.Emulator!.Rom!.Header.Title, Str(id, 5 + 4 + code.Length + 1), "title follows in the same reply");
            }
        }

        [TestMethod]
        public void MemoryReadWriteAndBatching()
        {
            var (f, pine, c) = Start();
            using (f) using (pine) using (c)
            {
                WaitForDone(f);
                var done = c.Send(Cmd(PineOpcodes.Read32, TestRomRunner.ResultBase9));
                Assert.AreEqual(9, done.Length, "Read32 reply size");
                Assert.AreEqual(TestRomRunner.DoneMagic, U32(done, 5), "RESULT9(0) through PINE");

                const uint scratch = 0x023F0000;
                var write = c.Send(Cmd(PineOpcodes.Write32, scratch, 0xCAFEBABEu), Cmd(PineOpcodes.Write16, scratch + 4, (ushort)0x1234),
                                   Cmd(PineOpcodes.Write8, scratch + 6, (byte)0x56));
                Assert.AreEqual(5, write.Length, "writes return no data");
                Assert.AreEqual(Ok, write[4], "writes ok");

                var read = c.Send(Cmd(PineOpcodes.Read32, scratch), Cmd(PineOpcodes.Read16, scratch + 4), Cmd(PineOpcodes.Read8, scratch + 6));
                Assert.AreEqual(5 + 4 + 2 + 1, read.Length, "batched reply concatenates");
                Assert.AreEqual(0xCAFEBABEu, U32(read, 5), "u32");
                Assert.AreEqual((ushort)0x1234, U16(read, 9), "u16");
                Assert.AreEqual((byte)0x56, read[11], "u8");

                var block = Enumerable.Range(0, 37).Select(i => (byte)(i * 7)).ToArray();
                Assert.AreEqual(Ok, c.Send(Cmd(PineOpcodes.WriteBuffer, scratch + 0x101, (uint)block.Length, block))[4], "bulk write");
                var back = c.Send(Cmd(PineOpcodes.ReadBuffer, scratch + 0x101, (uint)block.Length));
                CollectionAssert.AreEqual(block, back[5..], "bulk read returns the bulk write");
            }
        }

        [TestMethod]
        public void FailuresReturnAHeaderOnlyReply()
        {
            var (f, pine, c) = Start();
            using (f) using (pine) using (c)
            {
                var threads = c.Send(Cmd(PineOpcodes.GetEEThreads));
                CollectionAssert.AreEqual(new byte[] { 5, 0, 0, 0, Fail }, threads, "no PS2 threads on a DS");

                // A failing command poisons the whole packet, earlier results included.
                var mixed = c.Send(Cmd(PineOpcodes.Read32, 0x02000000u), Cmd(PineOpcodes.Step));
                CollectionAssert.AreEqual(new byte[] { 5, 0, 0, 0, Fail }, mixed, "step while running fails the packet");

                var truncated = c.Send(new byte[] { PineOpcodes.Read32, 0x00, 0x00 });
                Assert.AreEqual(Fail, truncated[4], "truncated arguments fail");

                Assert.AreEqual(Ok, c.Send(Cmd(PineOpcodes.Status))[4], "the connection still works");
            }
        }

        [TestMethod]
        public void PauseStepAndRegisters()
        {
            var (f, pine, c) = Start();
            using (f) using (pine) using (c)
            {
                WaitForDone(f);
                Assert.AreEqual(Ok, c.Send(Cmd(PineOpcodes.Pause))[4], "pause");
                f.WaitFor(() => f.Host.IsPaused, "pause");

                var pc = c.Send(Cmd(PineOpcodes.GetProgramCounter));
                Assert.AreEqual(10, pc.Length, "GetProgramCounter reply size");
                Assert.AreEqual(1, pc[9], "reports paused");

                var regs = c.Send(Cmd(PineOpcodes.GetRegisters));
                Assert.AreEqual(5 + 18 * 4, regs.Length, "r0–r15, CPSR, SPSR");
                Assert.AreEqual(U32(pc, 5), U32(regs, 5 + 15 * 4), "r15 = PC");

                var step = c.Send(Cmd(PineOpcodes.Step));
                Assert.AreEqual(9, step.Length, "Step reply size");
                Assert.AreEqual(f.Host.Invoke(() => f.Host.Emulator!.Arm9.Pc), U32(step, 5), "new PC");

                // Write r0 through SetRegister, read it back through GetRegister.
                var set = c.Send(Cmd(PineOpcodes.SetRegister, (byte)0, PineOpcodes.CategoryGpr, (byte)0, 0x12345678u, new byte[12]));
                Assert.AreEqual(Ok, set[4], "set r0");
                var get = c.Send(Cmd(PineOpcodes.GetRegister, (byte)0, PineOpcodes.CategoryGpr, (byte)0));
                Assert.AreEqual(21, get.Length, "GetRegister reply is a u128");
                Assert.AreEqual(0x12345678u, U32(get, 5), "r0");

                var cp15 = c.Send(Cmd(PineOpcodes.GetRegister, (byte)0, PineOpcodes.CategoryCp15, (byte)3));
                Assert.AreEqual(0x41059461u, U32(cp15, 5), "CP15 main ID");
                Assert.AreEqual(Fail, c.Send(Cmd(PineOpcodes.GetRegister, (byte)1, PineOpcodes.CategoryCp15, (byte)0))[4], "the ARM7 has no CP15");

                // The ARM7 is reachable after SelectCpu.
                var arm7 = c.Send(Cmd(PineOpcodes.SelectCpu, (byte)1), Cmd(PineOpcodes.GetProgramCounter));
                Assert.AreEqual(f.Host.Invoke(() => f.Host.Emulator!.Arm7.Pc), U32(arm7, 5), "ARM7 PC");

                Assert.AreEqual(Ok, c.Send(Cmd(PineOpcodes.Resume))[4], "resume");
                f.WaitFor(() => !f.Host.IsPaused, "resume");
            }
        }

        [TestMethod]
        public void BreakpointsAndWatchpoints()
        {
            var (f, pine, c) = Start();
            using (f) using (pine) using (c)
            {
                WaitForDone(f);
                c.Send(Cmd(PineOpcodes.Pause));
                f.WaitFor(() => f.Host.IsPaused, "pause");
                uint loopPc = U32(c.Send(Cmd(PineOpcodes.GetProgramCounter)), 5);

                Assert.AreEqual(Ok, c.Send(Cmd(PineOpcodes.SetBreakpoint, loopPc))[4], "set breakpoint");
                var list = c.Send(Cmd(PineOpcodes.ListBreakpoints, (byte)0xFF));
                Assert.AreEqual(1u, U32(list, 5), "one breakpoint");
                Assert.AreEqual(loopPc, U32(list, 9), "its address");

                // Resume runs the instruction under the breakpoint, then the loop comes back to it.
                c.Send(Cmd(PineOpcodes.Resume));
                f.WaitFor(() => f.Host.IsPaused && f.Host.LastBreak != null, "the breakpoint");
                var info = c.Send(Cmd(PineOpcodes.GetBreakInfo));
                Assert.AreEqual(1, info[5], "a break happened");
                Assert.AreEqual(0, info[7], "reason: breakpoint");
                Assert.AreEqual(loopPc, U32(info, 8), "at the breakpoint");

                Assert.AreEqual(Ok, c.Send(Cmd(PineOpcodes.ClearBreakpoint, loopPc))[4], "clear");
                Assert.AreEqual(Fail, c.Send(Cmd(PineOpcodes.ClearBreakpoint, loopPc))[4], "clearing again fails");

                // Write watch on RESULT9(1) (the round counter).
                uint target = TestRomRunner.ResultBase9 + 4;
                Assert.AreEqual(Ok, c.Send(Cmd(PineOpcodes.AddWatch, (byte)0, target, target + 4, (byte)2))[4], "add watch");
                var watches = c.Send(Cmd(PineOpcodes.ListWatches, (byte)0xFF));
                Assert.AreEqual(1u, U32(watches, 5), "one watch");
                Assert.AreEqual(1, watches[9 + 10], "PCSX2-style CPU id 1 = ARM9");

                c.Send(Cmd(PineOpcodes.Resume));
                f.WaitFor(() => f.Host.IsPaused && f.Host.LastBreak?.Reason == EmulatorCore.Debugging.BreakReason.Watchpoint, "the watchpoint", 30_000);
                info = c.Send(Cmd(PineOpcodes.GetBreakInfo));
                Assert.AreEqual(1, info[7], "reason: watchpoint");
                Assert.AreEqual(target, U32(info, 12), "accessed address");
                Assert.AreEqual(1, info[16], "write");

                Assert.AreEqual(Ok, c.Send(Cmd(PineOpcodes.ClearAllWatches), Cmd(PineOpcodes.Resume))[4], "clear + resume");
            }
        }

        [TestMethod]
        public void FrameAdvanceRunsExactlyThatManyFrames()
        {
            var (f, pine, c) = Start(paused: true);
            using (f) using (pine) using (c)
            {
                long before = f.Host.Invoke(() => f.Host.Emulator!.FrameCount);
                Assert.AreEqual(Ok, c.Send(Cmd(PineOpcodes.FrameAdvance, (byte)5))[4], "frame advance");
                f.WaitFor(() => f.Host.IsPaused && f.Host.Emulator!.FrameCount >= before + 5, "five frames");
                Thread.Sleep(50);
                var frames = c.Send(Cmd(PineOpcodes.GetFrameCount));
                Assert.AreEqual((ulong)(before + 5), BitConverter.ToUInt64(frames, 5), "stopped after exactly five");
            }
        }

        [TestMethod]
        public void DisassembleInputAndStates()
        {
            var (f, pine, c) = Start(paused: true);
            using (f) using (pine) using (c)
            {
                uint entry = f.Host.Invoke(() => f.Host.Emulator!.Rom!.Header.Arm9EntryAddress);
                var dis = c.Send(Cmd(PineOpcodes.Disassemble, (byte)0x10, entry, (ushort)4));
                Assert.AreEqual(Ok, dis[4], "disassemble");
                Assert.AreEqual((ushort)4, U16(dis, 5), "four lines");
                Assert.AreEqual(entry, U32(dis, 7), "first line address");
                Assert.IsTrue(dis[11] > 0, "first line has text");

                Assert.AreEqual(Ok, c.Send(Cmd(PineOpcodes.PadSetButton, (byte)0, (byte)0, 1.0f))[4], "press A");
                var pad = c.Send(Cmd(PineOpcodes.PadGetState, (byte)0));
                Assert.AreEqual(29, pad.Length, "PadGetState reply size");
                Assert.AreEqual(1u, U32(pad, 5), "A is pressed");
                Assert.AreEqual(Ok, c.Send(Cmd(PineOpcodes.SetTouch, (byte)1, (ushort)100, (ushort)50))[4], "touch");
                Assert.AreEqual(Fail, c.Send(Cmd(PineOpcodes.SetTouch, (byte)1, (ushort)300, (ushort)50))[4], "off-screen touch");

                string statePath = Path.Combine(f.Folder, "pine.state");
                Assert.AreEqual(Ok, c.Send(Cmd(PineOpcodes.SaveStateFile, statePath))[4], "save state file");
                Assert.IsTrue(File.Exists(statePath), "state written");
                Assert.AreEqual(Ok, c.Send(Cmd(PineOpcodes.LoadStateFile, statePath))[4], "load state file");
                Assert.AreEqual(Ok, c.Send(Cmd(PineOpcodes.SaveState, (byte)2), Cmd(PineOpcodes.LoadState, (byte)2))[4], "slot save + load");
            }
        }

        [TestMethod]
        public void CheatOpcodesManageTheList()
        {
            var (f, pine, c) = Start();
            using (f) using (pine) using (c)
            {
                WaitForDone(f);
                var add = c.Send(Cmd(PineOpcodes.CheatAdd, (byte)0, (byte)1, "Value", "023F0000 0000BEEF"));
                Assert.AreEqual(Ok, add[4], "add AR cheat");
                Assert.AreEqual(0u, U32(add, 5), "index 0");
                Assert.AreEqual(Fail, c.Send(Cmd(PineOpcodes.CheatAdd, (byte)0, (byte)1, "Bad", "zzz"))[4], "invalid code rejected");
                Assert.AreEqual(Ok, c.Send(Cmd(PineOpcodes.CheatAdd, (byte)1, (byte)0, "Raw", "023F0010 12"))[4], "add raw cheat, disabled");

                var list = c.Send(Cmd(PineOpcodes.CheatList));
                Assert.AreEqual(2u, U32(list, 5), "two cheats");
                Assert.AreEqual(1, list[9], "first enabled");
                Assert.AreEqual("Value", System.Text.Encoding.UTF8.GetString(list, 12, list[11]), "first name");

                f.WaitFor(() => f.Host.Emulator!.Arm9Bus.BusRead32(0x023F0000) == 0xBEEF, "the cheat to apply");
                Assert.AreEqual(Ok, c.Send(Cmd(PineOpcodes.CheatEnable, 1u, (byte)1))[4], "enable the raw cheat");
                f.WaitFor(() => f.Host.Emulator!.Arm9Bus.BusRead8(0x023F0010) == 0x12, "the raw cheat to apply");

                Assert.AreEqual(Ok, c.Send(Cmd(PineOpcodes.CheatRemove, 0u))[4], "remove");
                Assert.AreEqual(1u, U32(c.Send(Cmd(PineOpcodes.CheatList)), 5), "one left");
                Assert.IsTrue(File.Exists(Path.Combine(f.Folder, "cheats", EmulatorCore.Cheats.CheatList.GameId(f.Host.Emulator!.Rom!) + ".json")), "list saved per game");
                Assert.AreEqual(Ok, c.Send(Cmd(PineOpcodes.CheatClear))[4], "clear");
                Assert.AreEqual(0u, U32(c.Send(Cmd(PineOpcodes.CheatList)), 5), "empty");
            }
        }
    }
}
