using System.Collections.Concurrent;
using Px86.NintendoDS.EmulatorCore;
using Px86.NintendoDS.EmulatorCore.Hardware.Wifi;
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
        public void TwoConsolesBeaconAndMultiplay()
        {
            var hub = new WifiHub();
            var master = Boot(1, hub);
            var slave = Boot(2, hub);
            hub.Join(master.Wifi.Id);
            hub.Join(slave.Wifi.Id);

            // Like two emulator windows: each console runs on its own thread.
            var run = new[] { master, slave }.Select(nds => Task.Run(() =>
            {
                for (int frame = 0; frame < 600; frame++)
                {
                    nds.RunFrame();
                    if (R7(master, 0) == TestRomRunner.DoneMagic && R7(slave, 0) == TestRomRunner.DoneMagic && frame > 30)
                        break;
                }
            })).ToArray();
            Assert.IsTrue(Task.WaitAll(run, TimeSpan.FromSeconds(60)), "consoles finished in time");

            Assert.AreEqual(TestRomRunner.DoneMagic, R7(master, 0), "master done");
            Assert.AreEqual(TestRomRunner.DoneMagic, R7(slave, 0), "slave done");
            Assert.AreEqual(0x21444D43u, R7(slave, 14), "slave received the CMD payload \"CMD!\"");
            Assert.IsTrue(R7(slave, 15) >= 1, "slave received the CMD ACK");
            Assert.IsTrue(R7(master, 15) >= 1, "master received a REPLY");
            Assert.AreEqual(0x594C5052u, R7(master, 16), "reply body \"RPLY\"");
            Assert.AreEqual(0u, R7(master, 14), "no missing replies in the CMD TX header");
            Assert.IsTrue(R7(master, 13) > 0 && R7(slave, 13) > 0, $"beacons seen: master {R7(master, 13)}, slave {R7(slave, 13)}");
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
