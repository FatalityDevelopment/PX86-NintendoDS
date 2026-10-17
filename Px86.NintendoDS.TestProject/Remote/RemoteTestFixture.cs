using System.Buffers.Binary;
using System.Net.Sockets;
using Px86.NintendoDS.EmulatorCore;
using Px86.NintendoDS.EmulatorCore.Hosting;

namespace Px86.NintendoDS.TestProject.Remote
{
    /// <summary>A host running a test ROM (copied to a temp folder so saves/states don't land in the build output).</summary>
    internal sealed class HostFixture : IDisposable
    {
        private readonly string _folder;

        public HostFixture(string rom, bool paused = false)
        {
            _folder = Path.Combine(Path.GetTempPath(), "px86-host-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_folder);
            RomPath = Path.Combine(_folder, rom + ".nds");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "TestRoms", rom + ".nds"), RomPath);
            Host = new EmulatorHost { Limiter = LimiterMode.Unlimited, CheatFolder = Path.Combine(_folder, "cheats") };
            Host.LoadGame(new GameLoadOptions { RomPath = RomPath });
            Host.Start(paused);
        }

        public EmulatorHost Host { get; }
        public string RomPath { get; }
        public string Folder => _folder;

        /// <summary>Wait until <paramref name="condition"/> holds (checked under the host lock).</summary>
        public void WaitFor(Func<bool> condition, string what, int timeoutMs = 10_000)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (!Host.Invoke(condition))
            {
                if (timer.ElapsedMilliseconds > timeoutMs)
                    Assert.Fail($"Timed out waiting for {what}.");
                Thread.Sleep(5);
            }
        }

        public void Dispose()
        {
            Host.Dispose();
            try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>Minimal PINE client for the tests: builds packets and returns the raw reply.</summary>
    internal sealed class TestPineClient : IDisposable
    {
        private readonly TcpClient _tcp;
        private readonly NetworkStream _stream;

        public TestPineClient(int port)
        {
            _tcp = new TcpClient("127.0.0.1", port) { NoDelay = true };
            _stream = _tcp.GetStream();
        }

        /// <summary>Send one packet built from the given command bytes; returns the whole reply.</summary>
        public byte[] Send(params byte[][] commands)
        {
            int size = 4 + commands.Sum(c => c.Length);
            var packet = new byte[size];
            BinaryPrimitives.WriteUInt32LittleEndian(packet, (uint)size);
            int pos = 4;
            foreach (var c in commands)
            {
                c.CopyTo(packet, pos);
                pos += c.Length;
            }
            _stream.Write(packet);

            var header = new byte[4];
            _stream.ReadExactly(header);
            int replySize = (int)BinaryPrimitives.ReadUInt32LittleEndian(header);
            var reply = new byte[replySize];
            header.CopyTo(reply, 0);
            _stream.ReadExactly(reply, 4, replySize - 4);
            return reply;
        }

        public static byte[] Cmd(byte opcode, params object[] args)
        {
            var bytes = new List<byte> { opcode };
            foreach (var a in args)
            {
                switch (a)
                {
                    case byte b: bytes.Add(b); break;
                    case ushort h: bytes.AddRange(BitConverter.GetBytes(h)); break;
                    case uint w: bytes.AddRange(BitConverter.GetBytes(w)); break;
                    case float f: bytes.AddRange(BitConverter.GetBytes(f)); break;
                    case byte[] raw: bytes.AddRange(raw); break;
                    case string s:
                        var utf8 = System.Text.Encoding.UTF8.GetBytes(s);
                        bytes.AddRange(BitConverter.GetBytes((ushort)utf8.Length));
                        bytes.AddRange(utf8);
                        break;
                    default: throw new ArgumentException($"Unsupported argument {a.GetType()}");
                }
            }
            return bytes.ToArray();
        }

        public static uint U32(byte[] reply, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(reply.AsSpan(offset));
        public static ushort U16(byte[] reply, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(reply.AsSpan(offset));

        /// <summary>Read a PINE string (u32 length incl. NUL, bytes, NUL) at <paramref name="offset"/>.</summary>
        public static string Str(byte[] reply, int offset)
        {
            int length = (int)U32(reply, offset);
            return System.Text.Encoding.UTF8.GetString(reply, offset + 4, length - 1);
        }

        public void Dispose() => _tcp.Dispose();
    }
}
