using System.Collections.Concurrent;
using Px86.NintendoDS.EmulatorCore;
using Px86.NintendoDS.EmulatorCore.Hardware.Wifi;
using Px86.NintendoDS.EmulatorCore.Network;
using Px86.NintendoDS.TestProject.Emulator;

namespace Px86.NintendoDS.TestProject.Hardware
{
    /// <summary>
    /// The wifi test ROM drives the MAC the way a driver does: serial chip ports,
    /// µs counter and beacon IRQs, a transmit that comes back into the RX ring,
    /// and — with two consoles on a hub — beacons plus a multiplay CMD/REPLY/ACK
    /// round trip.
    /// </summary>
    [TestClass]
    public class TestWifi
    {
        private const uint RoleAddress = 0x023003F0;

        /// <summary>Everything sent comes straight back, as if another console repeated it.</summary>
        private sealed class EchoMedium : IWifiMedium
        {
            private readonly ConcurrentQueue<WifiPacket> _queue = new();

            public void Send(WifiPacket packet) =>
                _queue.Enqueue(new WifiPacket { Frame = packet.Frame, Rate = packet.Rate, Sender = -1, ReplyAid = packet.ReplyAid });

            public bool TryReceive(long receiver, TimeSpan timeout, out WifiPacket packet) => _queue.TryDequeue(out packet!);

            public int PeerCount(long self) => 0;
        }

        private static NdsEmulator Boot(int role, IWifiMedium medium)
        {
            var nds = TestRomRunner.Boot("wifi");
            nds.Arm9Bus.BusWrite32(RoleAddress, (uint)role);
            nds.Wifi.Medium = medium;
            return nds;
        }

        private static uint R7(NdsEmulator nds, int i) => TestRomRunner.Result7(nds, i);

        private static void RunUntilDone(NdsEmulator nds, int maxFrames)
        {
            for (int frame = 0; frame < maxFrames && R7(nds, 0) != TestRomRunner.DoneMagic; frame++)
                nds.RunFrame();
            Assert.AreEqual(TestRomRunner.DoneMagic, R7(nds, 0), $"ARM7 did not finish: {nds.Arm7}");
        }

        [TestMethod]
        public void ChipPortsTimersAndLoopback()
        {
            var nds = Boot(0, new EchoMedium());
            RunUntilDone(nds, 120);

            Assert.AreEqual(0xC340u, R7(nds, 1), "W_ID (DS-Lite firmware)");
            Assert.AreEqual(0x5Au, R7(nds, 2), "BB register write/read");
            Assert.AreEqual(0x6Du, R7(nds, 3), "BB chip ID");
            Assert.AreEqual(0x77u, R7(nds, 4), "RF type-3 register write/read");
            Assert.AreEqual(1u, R7(nds, 5), "µs counter runs");
            Assert.IsTrue(R7(nds, 6) is >= 4 and <= 6, $"IRQ14 every 2 ms over 10 ms, got {R7(nds, 6)}");
            Assert.AreEqual(1u, R7(nds, 7), "IF.24 raised by W_IF & W_IE");
            Assert.AreEqual(0x0001u, R7(nds, 8), "W_TXSTAT: LOC1 done, no error");
            Assert.AreEqual(1u, R7(nds, 12), "W_TX_SEQNO incremented once");
            Assert.AreEqual(28u, R7(nds, 9), "RX length = IEEE header + body, no FCS");
            Assert.AreEqual(0x36385850u, R7(nds, 10), "body \"PX86\"");
            Assert.AreEqual((12u + 28u) / 2, R7(nds, 11), "WRCSR advanced past header + frame");
            Assert.AreEqual(0x0040_0018u, R7(nds, 17), "RX header via W_RXBUF_RD_DATA: data frame flags, 0040h");
            Assert.IsTrue(nds.Wifi.FramesSent >= 1 && nds.Wifi.FramesReceived >= 1);
        }

        [TestMethod]
        [DoNotParallelize] // waits in real time for the other console
        public void TwoConsolesBeaconAndMultiplay()
        {
            var hub = new WifiHub();
            var master = Boot(1, hub);
            var slave = Boot(2, hub);
            hub.Join(master.Wifi.Id);
            hub.Join(slave.Wifi.Id);
            RunMultiplay(master, slave);
        }

        [TestMethod]
        [DoNotParallelize]
        public void TwoConsolesOverUdpMulticast()
        {
            int port = 20000 + Random.Shared.Next(20000);
            LocalWifiMedium a, b;
            try
            {
                a = new LocalWifiMedium(port);
                b = new LocalWifiMedium(port);
            }
            catch (System.Net.Sockets.SocketException ex)
            {
                Assert.Inconclusive($"No loopback multicast here: {ex.Message}");
                return;
            }
            using (a)
            using (b)
            {
                var master = Boot(1, a);
                var slave = Boot(2, b);
                RunMultiplay(master, slave);
                Assert.IsTrue(a.DatagramsReceived > 0 && b.DatagramsReceived > 0, "datagrams went both ways");
            }
        }

        /// <summary>Run a master and a slave on their own threads (like two windows) and check the multiplay round.</summary>
        private static void RunMultiplay(NdsEmulator master, NdsEmulator slave)
        {
            // Dedicated threads released together: a late-starting slave (thread pool starvation
            // under parallel tests) would leave the master sending CMDs to nobody.
            using var start = new Barrier(2);
            var threads = new[] { master, slave }.Select(nds => new Thread(() =>
            {
                start.SignalAndWait();
                for (int frame = 0; frame < 600; frame++)
                {
                    nds.RunFrame();
                    if (R7(master, 0) == TestRomRunner.DoneMagic && R7(slave, 0) == TestRomRunner.DoneMagic && frame > 30)
                        break;
                }
            }) { IsBackground = true }).ToArray();
            foreach (var t in threads) t.Start();
            Assert.IsTrue(threads.All(t => t.Join(TimeSpan.FromSeconds(60))), "consoles finished in time");

            Assert.AreEqual(TestRomRunner.DoneMagic, R7(master, 0), "master done");
            Assert.AreEqual(TestRomRunner.DoneMagic, R7(slave, 0), "slave done");
            Assert.AreEqual(0x21444D43u, R7(slave, 14), "slave received the CMD payload \"CMD!\"");
            Assert.IsTrue(R7(slave, 15) >= 1, "slave received the CMD ACK");
            Assert.IsTrue(R7(master, 15) >= 1, "master received a REPLY");
            Assert.AreEqual(0x594C5052u, R7(master, 16), "reply body \"RPLY\"");
            Assert.AreEqual(0u, R7(master, 14), "no missing replies in the CMD TX header");
            Assert.IsTrue(R7(master, 13) > 0 && R7(slave, 13) > 0, $"beacons seen: master {R7(master, 13)}, slave {R7(slave, 13)}");
        }

        // ── Register-level checks (a test ROM that leaves Wifi alone supplies time) ──

        private const uint Regs = 0x04808000;

        private static NdsEmulator Idle()
        {
            var nds = TestRomRunner.Boot("touch");
            nds.RunFrame();
            return nds;
        }

        private static void W(NdsEmulator nds, uint register, ushort value) => nds.Wifi.Write16(Regs + register, value);
        private static ushort Rd(NdsEmulator nds, uint register) => nds.Wifi.Read16(Regs + register);

        /// <summary>Powered, receiving into a ring at 0C00h–1F60h, accepting group frames of any BSSID.</summary>
        private static void StartReceiver(NdsEmulator nds)
        {
            W(nds, WifiController.W_POWER_US, 0);
            W(nds, WifiController.W_POWERFORCE, 0x8000);
            W(nds, WifiController.W_RXBUF_BEGIN, 0x4C00);
            W(nds, WifiController.W_RXBUF_END, 0x5F60);
            W(nds, WifiController.W_RXBUF_WR_ADDR, 0x0600);
            W(nds, WifiController.W_RXBUF_READCSR, 0x0600);
            W(nds, WifiController.W_RXCNT, 0x8001);
            W(nds, WifiController.W_RXFILTER, 0x0001);
        }

        private static byte[] Frame(ushort fc, byte[] a1, byte[] a2, byte[] a3, params byte[] body)
        {
            var f = new byte[24 + body.Length];
            f[0] = (byte)fc; f[1] = (byte)(fc >> 8);
            a1.CopyTo(f, 4); a2.CopyTo(f, 10); a3.CopyTo(f, 16);
            body.CopyTo(f, 24);
            return f;
        }

        private static readonly byte[] Broadcast = { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };
        private static readonly byte[] Other = { 0x00, 0x09, 0xBF, 0xAA, 0xBB, 0xCC };

        [TestMethod]
        public void ReceivedFrameCountsAtGbatekStatisticsPorts()
        {
            var nds = Idle();
            StartReceiver(nds);
            nds.Wifi.Receive(new WifiPacket { Frame = Frame(0x0008, Broadcast, Other, Other, 1, 2, 3, 4), Sender = 99 });
            Assert.AreEqual(1, nds.Wifi.FramesReceived);
            Assert.AreEqual(0x0100, Rd(nds, 0x1B6), "\"packet received okay\" is the byte at 1B7h; 1B6h (FCS errors) stays 0");
            Assert.AreEqual(0, Rd(nds, 0x1B6), "statistics clear when read");
            Assert.AreEqual(0, Rd(nds, 0x1B4) & 0xFF, "no RXBUF-full error");
        }

        [TestMethod]
        public void WifiIrqLineIsEdgeTriggered()
        {
            var nds = Idle();
            uint bit24 = 1u << 24;
            W(nds, WifiController.W_IE, 0x0003);
            nds.Irq7.AcknowledgeIf(0xFFFFFFFF);
            W(nds, WifiController.W_IF_SET, 0x0001);
            Assert.AreNotEqual(0u, nds.Irq7.If & bit24, "W_IF & W_IE went non-zero: IF.24");
            nds.Irq7.AcknowledgeIf(bit24);
            W(nds, WifiController.W_IF_SET, 0x0002);
            Assert.AreEqual(0u, nds.Irq7.If & bit24, "still non-zero: no new IF.24");
            W(nds, WifiController.W_IF, 0xFFFF);
            W(nds, WifiController.W_IF_SET, 0x0002);
            Assert.AreNotEqual(0u, nds.Irq7.If & bit24, "after clearing W_IF, a new request raises IF.24 again");
        }

        [TestMethod]
        public void CommandThatCannotStartFailsWithIrq12()
        {
            var nds = Idle();
            StartReceiver(nds);
            W(nds, WifiController.W_US_COUNTCNT, 1);
            W(nds, WifiController.W_MODE_RST, 0);         // TX master disabled: the CMD can't go out
            W(nds, WifiController.W_IE, 1 << WifiController.IRQ_CMD_DONE);
            W(nds, WifiController.W_CMD_COUNT, 5);        // 50 µs window
            W(nds, WifiController.W_TXBUF_CMD, 0x8000 | 0x0200);
            W(nds, WifiController.W_TXREQ_SET, 0x0002);
            nds.RunCycles(Scheduler.Arm9ClockHz / 1000 * 3);
            Assert.AreNotEqual(0, Rd(nds, WifiController.W_IF) & (1 << 12), "IRQ12 when W_CMD_COUNT ran out");
            Assert.AreEqual(0x0005, nds.Wifi.Read16(0x04804000 + 0x0400), "TXHDR[0] = 0005h (failed)");
            Assert.AreEqual(0, Rd(nds, WifiController.W_TXBUF_CMD) & 0x8000, "CMD request cleared");
        }

        [TestMethod]
        public void SlaveWithoutReplyAnswersWithEmptyToDsReply()
        {
            var nds = Idle();
            StartReceiver(nds);
            byte[] bssid = Other;
            for (int i = 0; i < 3; i++)
                W(nds, WifiController.W_BSSID + (uint)i * 2, (ushort)(bssid[i * 2] | bssid[i * 2 + 1] << 8));
            W(nds, WifiController.W_AID_LOW, 1);
            WifiPacket? sent = null;
            nds.Wifi.Transmitted += p => sent = p;
            byte[] cmdAddress = { 0x03, 0x09, 0xBF, 0x00, 0x00, 0x00 };
            nds.Wifi.Receive(new WifiPacket { Frame = Frame(0x0228, cmdAddress, bssid, bssid, 0x06, 0x01, 0x02, 0x00), Sender = 99 });
            Assert.IsNotNull(sent, "the hardware replied by itself");
            Assert.AreEqual(0x0158, sent!.FrameControl, "empty reply");
            Assert.AreEqual(1, sent.ReplyAid);
            CollectionAssert.AreEqual(bssid, sent.Frame[4..10], "ToDS: address 1 = BSSID");
            CollectionAssert.AreEqual(new byte[] { 0x03, 0x09, 0xBF, 0x00, 0x00, 0x10 }, sent.Frame[16..22], "address 3 = reply DA");
        }

        [TestMethod]
        public void AccessPointBeaconsReachTheConsole()
        {
            using var online = new OnlineWifiMedium(NullWifiMedium.Instance, null);
            var nds = Boot(0, online);
            RunUntilDone(nds, 120);
            for (int i = 0; i < 20; i++) nds.RunFrame(); // 20 frames ≈ 330 ms: a few 100 ms beacons
            Assert.IsTrue(R7(nds, 13) > 0, "the console's receiver saw the access point's beacons");
        }

        [TestMethod]
        public void FirmwareWritesPersistWithTheOriginalMac()
        {
            string dir = Path.Combine(Path.GetTempPath(), "px86-fw-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string rom = Path.Combine(AppContext.BaseDirectory, "TestRoms", "touch.nds");
                string writes = Path.Combine(dir, "firmware-writes.bin");
                var options = new GameLoadOptions
                {
                    RomPath = rom,
                    SavePath = Path.Combine(dir, "touch.sav"),
                    Profile = new EmulatorCore.Hardware.Spi.FirmwareFlash.UserProfile(),
                    FirmwareWritesPath = writes,
                    MacInstance = 1,
                };
                using (var session = GameSession.Start(options))
                {
                    var flash = session.Emulator.Spi.Firmware;
                    CollectionAssert.AreNotEqual(EmulatorCore.Hardware.Spi.FirmwareFlash.DefaultMac, flash.MacAddress, "second window: different MAC");
                    // What a game's WFC setup does: WREN, then page-write into the access point slots.
                    flash.Transfer(0x06); flash.Deselect();
                    foreach (byte b in new byte[] { 0x0A, 0x03, 0xFA, 0x40, (byte)'P', (byte)'X' }) flash.Transfer(b);
                    flash.Deselect();
                    for (int i = 0; i < 65; i++) session.Emulator.RunFrame();
                    Assert.IsTrue(File.Exists(writes), "firmware changes written");
                }
                var saved = File.ReadAllBytes(writes);
                Assert.AreEqual((byte)'P', saved[0x3FA40]);
                Assert.AreEqual((byte)'X', saved[0x3FA41]);
                CollectionAssert.AreEqual(EmulatorCore.Hardware.Spi.FirmwareFlash.DefaultMac, saved[0x36..0x3C], "the file keeps the real MAC");

                using var again = GameSession.Start(new GameLoadOptions { RomPath = rom, SavePath = options.SavePath, FirmwareWritesPath = writes });
                Assert.AreEqual((byte)'P', again.Emulator.Spi.Firmware.Data[0x3FA40], "loaded back next time");
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [TestMethod]
        public void StateRoundTripKeepsWifiRegisters()
        {
            var a = Boot(0, new EchoMedium());
            RunUntilDone(a, 120);
            using var state = new MemoryStream();
            a.SaveState(state);
            var b = TestRomRunner.Boot("wifi");
            state.Position = 0;
            b.LoadState(state);
            Assert.AreEqual(a.Wifi.Read16(0x04808000 + WifiController.W_MACADDR + 4), b.Wifi.Read16(0x04808000 + WifiController.W_MACADDR + 4));
            Assert.AreEqual(a.Wifi.UsCount(), b.Wifi.UsCount());
            CollectionAssert.AreEqual(a.Wifi.Ram, b.Wifi.Ram);
        }
    }
}
