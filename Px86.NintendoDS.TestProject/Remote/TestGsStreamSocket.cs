using System.Buffers.Binary;
using System.Net.Sockets;
using Px86.NintendoDS.EmulatorCore.Remote;
using Px86.NintendoDS.EmulatorCore.Video;
using Px86.NintendoDS.FileFormats.Images;
using Px86.NintendoDS.TestProject.Emulator;
using static Px86.NintendoDS.TestProject.Remote.TestPineClient;

namespace Px86.NintendoDS.TestProject.Remote
{
    [TestClass]
    public class TestGsStreamSocket
    {
        private static byte[] ReadMessage(NetworkStream stream)
        {
            var size = new byte[4];
            stream.ReadExactly(size);
            int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(size);
            Assert.IsTrue(length >= GsStreamSocket.HeaderSize - 4 && length < 4 * 1024 * 1024, $"plausible frame size ({length})");
            var message = new byte[4 + length];
            size.CopyTo(message, 0);
            stream.ReadExactly(message, 4, length);
            return message;
        }

        [TestMethod]
        public void SubscriberReceivesJpegFrames()
        {
            using var f = new HostFixture("gfxtest");
            using var gs = new GsStreamSocket(f.Host, port: 0);
            gs.Start();
            using var pine = new PineSocket(f.Host, port: 0) { GsStream = gs };
            pine.Start();

            using (var c = new TestPineClient(pine.Port))
            {
                var port = c.Send(Cmd(PineOpcodes.GsStreamGetPort));
                Assert.AreEqual(7, port.Length, "GSStreamGetPort reply size");
                Assert.AreEqual((ushort)gs.Port, U16(port, 5), "PINE reports the GS port");
            }

            using var client = new TcpClient("127.0.0.1", gs.Port);
            client.ReceiveTimeout = 10_000;
            var stream = client.GetStream();
            uint firstIndex = 0, lastIndex = 0;
            for (int i = 0; i < 4; i++)
            {
                var m = ReadMessage(stream);
                Assert.AreEqual(GsStreamSocket.MessageTypeFrame, m[4], "type = frame");
                uint index = BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(5));
                if (i == 0) firstIndex = index;
                else Assert.IsTrue(index >= lastIndex, "frame numbers never go down");
                lastIndex = index;
                Assert.AreEqual((ushort)256, BinaryPrimitives.ReadUInt16LittleEndian(m.AsSpan(9)), "width");
                Assert.AreEqual((ushort)384, BinaryPrimitives.ReadUInt16LittleEndian(m.AsSpan(11)), "height");
                Assert.AreEqual((byte)GsStreamCodec.Mjpeg, m[13], "codec");
                Assert.AreEqual(GsStreamSocket.FlagDualScreen, m[14], "dual-screen flag");
                Assert.AreEqual(0xFF, m[15]); Assert.AreEqual(0xD8, m[16], "payload starts with SOI");
                Assert.AreEqual(0xFF, m[^2]); Assert.AreEqual(0xD9, m[^1], "payload ends with EOI");
            }
            // The server counts a frame after its write returns, which can be just after we read it.
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (gs.FramesDelivered < 2 && timer.ElapsedMilliseconds < 5000)
                Thread.Sleep(5);
            Assert.IsTrue(lastIndex > firstIndex, "the stream moves on");
            Assert.IsTrue(gs.FramesDelivered >= 2, "broadcast deliveries counted");
        }

        [TestMethod]
        public void RawCodecCarriesThePixels()
        {
            var pixels = new uint[ScreenCapture.Width * ScreenCapture.Height];
            pixels[0] = 0xFF112233;
            pixels[^1] = 0xFFAABBCC;
            var m = GsStreamSocket.BuildMessage(pixels, 7, GsStreamCodec.RawRgba);
            Assert.AreEqual(GsStreamSocket.HeaderSize + pixels.Length * 4, m.Length, "header + RGBA");
            Assert.AreEqual((uint)(m.Length - 4), BinaryPrimitives.ReadUInt32LittleEndian(m), "size excludes itself");
            CollectionAssert.AreEqual(new byte[] { 0x11, 0x22, 0x33, 0xFF }, m[15..19], "first pixel RGBA");
            CollectionAssert.AreEqual(new byte[] { 0xAA, 0xBB, 0xCC, 0xFF }, m[^4..], "last pixel RGBA");
        }

        [TestMethod]
        public void JpegHasTheExpectedStructure()
        {
            var pixels = Enumerable.Range(0, 64 * 48).Select(i => 0xFF000000u | (uint)(i * 2654435761u >> 8)).ToArray();
            var jpeg = new JpegWriter(90).Encode(64, 48, pixels);
            Assert.AreEqual(0xD8FF, BinaryPrimitives.ReadUInt16LittleEndian(jpeg), "SOI");
            int sof = jpeg.AsSpan().IndexOf(new byte[] { 0xFF, 0xC0 });
            Assert.IsTrue(sof > 0, "baseline SOF0 present");
            Assert.AreEqual(48, BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(sof + 5)), "height");
            Assert.AreEqual(64, BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(sof + 7)), "width");
            Assert.AreEqual(0xD9FF, BinaryPrimitives.ReadUInt16LittleEndian(jpeg.AsSpan(jpeg.Length - 2)), "EOI");
            int sos = jpeg.AsSpan().IndexOf(new byte[] { 0xFF, 0xDA });
            for (int i = sos + 14; i < jpeg.Length - 2; i++)
                if (jpeg[i] == 0xFF)
                    Assert.AreEqual(0x00, jpeg[i + 1], $"0xFF in entropy data is stuffed (offset {i})");

            // Optional real-decoder check: PX86_JPEG_DUMP=dir writes the image for an external decoder.
            if (Environment.GetEnvironmentVariable("PX86_JPEG_DUMP") is { Length: > 0 } dir)
            {
                File.WriteAllBytes(Path.Combine(dir, "noise.jpg"), jpeg);
                var nds = TestRomRunner.RunToCompletion("gfxtest");
                File.WriteAllBytes(Path.Combine(dir, "gfxtest.jpg"), new JpegWriter(90).Encode(256, 384, ScreenCapture.Compose(nds)));
                ScreenCapture.SavePng(nds, Path.Combine(dir, "gfxtest.png"));
            }
        }
    }
}
