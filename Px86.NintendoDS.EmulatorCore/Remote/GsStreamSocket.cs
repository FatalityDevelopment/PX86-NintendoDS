using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Px86.NintendoDS.EmulatorCore.Hosting;
using Px86.NintendoDS.EmulatorCore.Video;
using Px86.NintendoDS.FileFormats.Images;

namespace Px86.NintendoDS.EmulatorCore.Remote
{
    /// <summary>Picture format of a GS stream frame (the header's codec byte).</summary>
    public enum GsStreamCodec : byte
    {
        /// <summary>One complete baseline JPEG per frame — what CodeDesigner's GS viewer decodes.</summary>
        Mjpeg = 1,
        /// <summary>One PNG per frame (lossless, bigger, slower to encode).</summary>
        Png = 2,
        /// <summary>Uncompressed RGBA, 4 bytes per pixel, rows top to bottom.</summary>
        RawRgba = 3,
    }

    /// <summary>
    /// "GS stream" frame server: pushes every finished frame (both screens
    /// stacked, 256×384) to all connected subscribers. It is the counterpart
    /// of the GSStreamServer in CodeDesigner's PCSX2 build: same framing, port
    /// discoverable through PINE (GSStreamGetPort), so CodeDesigner's
    /// GSStreamForwarder and GS viewer can show the DS screens unchanged.
    ///
    /// Frame (little-endian), sent server → client only:
    ///   u32 size       bytes after this field (11 + payload)
    ///   u8  type       1 = frame
    ///   u32 frame_idx  emulated frame number
    ///   u16 width      256
    ///   u16 height     384 (top screen, then bottom screen)
    ///   u8  codec      <see cref="GsStreamCodec"/>
    ///   u8  flags      bit 0 = Nintendo DS dual-screen layout
    ///   bytes payload
    ///
    /// Frames are encoded on this socket's own thread, only while somebody is
    /// subscribed, and at most <see cref="MaxFps"/> per second. If encoding
    /// falls behind, older frames are skipped (counted in <see cref="FramesDropped"/>).
    /// A subscriber that can't take a frame within 250 ms is disconnected. A new
    /// subscriber first gets the current picture, so frame numbers never go
    /// down but the first one may repeat.
    /// </summary>
    public sealed class GsStreamSocket : IDisposable
    {
        public const int DefaultPort = 28014;
        public const byte MessageTypeFrame = 1;
        public const byte FlagDualScreen = 0x01;
        public const int HeaderSize = 15;

        private readonly EmulatorHost _host;
        private readonly List<TcpClient> _subscribers = new();
        private readonly object _frameLock = new();
        private readonly uint[] _pending = new uint[ScreenCapture.Width * ScreenCapture.Height];
        private readonly uint[] _encoding = new uint[ScreenCapture.Width * ScreenCapture.Height];
        private readonly AutoResetEvent _frameArrived = new(false);
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private long _pendingIndex = -1;
        private long _lastTakenIndex = -1;
        private long _lastSentTicks;
        private TcpListener? _listener;
        private Thread? _acceptThread;
        private Thread? _encodeThread;
        private volatile bool _running;
        private long _delivered;
        private long _dropped;
        private JpegWriter _jpeg;

        public GsStreamSocket(EmulatorHost host, int port = DefaultPort)
        {
            _host = host;
            Port = port;
            _jpeg = new JpegWriter(85);
        }

        public int Port { get; private set; }

        public bool IsListening => _listener != null;

        public GsStreamCodec Codec { get; set; } = GsStreamCodec.Mjpeg;

        /// <summary>JPEG quality, 1–100.</summary>
        public int Quality
        {
            get => _jpeg.Quality;
            set => _jpeg = new JpegWriter(value);
        }

        /// <summary>Frame rate cap (frames are skipped above it). 0 = every frame.</summary>
        public double MaxFps { get; set; } = 60;

        public int SubscriberCount
        {
            get { lock (_subscribers) return _subscribers.Count; }
        }

        public long FramesDelivered => Interlocked.Read(ref _delivered);

        public long FramesDropped => Interlocked.Read(ref _dropped);

        public event Action<string>? Log;

        // ── Lifetime ──────────────────────────────────────────────────────────

        public void Start()
        {
            if (_listener != null)
                throw new InvalidOperationException("The GS stream socket is already started.");
            _listener = new TcpListener(IPAddress.Loopback, Port);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _running = true;
            _host.FrameReady += OnFrame;
            _acceptThread = new Thread(AcceptLoop) { Name = "Px86 GS stream accept", IsBackground = true };
            _acceptThread.Start();
            _encodeThread = new Thread(EncodeLoop) { Name = "Px86 GS stream encoder", IsBackground = true };
            _encodeThread.Start();
        }

        public void Stop()
        {
            _running = false;
            _host.FrameReady -= OnFrame;
            _frameArrived.Set();
            try { _listener?.Stop(); } catch (SocketException) { }
            _listener = null;
            _encodeThread?.Join(1000);
            lock (_subscribers)
            {
                foreach (var s in _subscribers)
                    s.Close();
                _subscribers.Clear();
            }
        }

        public void Dispose()
        {
            Stop();
            _frameArrived.Dispose();
        }

        // ── Frames ────────────────────────────────────────────────────────────

        private void OnFrame(uint[] frame, long index)
        {
            if (SubscriberCount == 0)
                return;
            lock (_frameLock)
            {
                if (index <= _lastTakenIndex)
                    return; // already sent (or older)
                if (_pendingIndex >= 0 && index > _pendingIndex)
                    Interlocked.Increment(ref _dropped);
                frame.CopyTo(_pending, 0);
                _pendingIndex = index;
            }
            _frameArrived.Set();
        }

        private void EncodeLoop()
        {
            while (_running)
            {
                _frameArrived.WaitOne(250);
                if (MaxFps > 0)
                {
                    long due = _lastSentTicks + (long)(Stopwatch.Frequency / MaxFps);
                    while (_running && _clock.ElapsedTicks < due)
                        Thread.Sleep(1);
                }

                long index;
                lock (_frameLock)
                {
                    index = _pendingIndex;
                    if (index < 0)
                        continue;
                    _pending.CopyTo(_encoding, 0);
                    _pendingIndex = -1;
                    _lastTakenIndex = index;
                }

                _lastSentTicks = _clock.ElapsedTicks;
                Broadcast(BuildMessage(_encoding, index, Codec, _jpeg));
            }
        }

        /// <summary>Build one framed message (header + payload). Public for tests and in-process users.</summary>
        public static byte[] BuildMessage(ReadOnlySpan<uint> pixels, long frameIndex, GsStreamCodec codec, JpegWriter? jpeg = null)
        {
            const int w = ScreenCapture.Width, h = ScreenCapture.Height;
            byte[] payload = codec switch
            {
                GsStreamCodec.Mjpeg => (jpeg ?? new JpegWriter()).Encode(w, h, pixels),
                GsStreamCodec.Png => EncodePng(pixels),
                GsStreamCodec.RawRgba => EncodeRaw(pixels),
                _ => throw new ArgumentOutOfRangeException(nameof(codec)),
            };

            var message = new byte[HeaderSize + payload.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(message, (uint)(message.Length - 4));
            message[4] = MessageTypeFrame;
            BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(5), (uint)frameIndex);
            BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(9), w);
            BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(11), h);
            message[13] = (byte)codec;
            message[14] = FlagDualScreen;
            payload.CopyTo(message, HeaderSize);
            return message;
        }

        private static byte[] EncodePng(ReadOnlySpan<uint> pixels)
        {
            using var ms = new MemoryStream();
            PngWriter.Write(ms, ScreenCapture.Width, ScreenCapture.Height, pixels);
            return ms.ToArray();
        }

        private static byte[] EncodeRaw(ReadOnlySpan<uint> pixels)
        {
            var raw = new byte[pixels.Length * 4];
            for (int i = 0; i < pixels.Length; i++)
            {
                uint p = pixels[i];
                raw[i * 4] = (byte)(p >> 16);
                raw[i * 4 + 1] = (byte)(p >> 8);
                raw[i * 4 + 2] = (byte)p;
                raw[i * 4 + 3] = 0xFF;
            }
            return raw;
        }

        // ── Subscribers ───────────────────────────────────────────────────────

        private void AcceptLoop()
        {
            while (_running)
            {
                TcpClient client;
                try
                {
                    client = _listener!.AcceptTcpClient();
                }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException or InvalidOperationException)
                {
                    return;
                }
                client.NoDelay = true;
                client.SendTimeout = 250;

                // Greet the new client with the current picture (useful while paused), then add it.
                byte[]? greeting = _host.Invoke(() =>
                    _host.Emulator is { } nds ? BuildMessage(ScreenCapture.Compose(nds), nds.FrameCount, Codec, _jpeg) : null);
                try
                {
                    if (greeting != null)
                        client.GetStream().Write(greeting);
                }
                catch (Exception ex) when (ex is IOException or SocketException)
                {
                    client.Close();
                    continue;
                }
                lock (_subscribers)
                    _subscribers.Add(client);
                Log?.Invoke($"[GS] subscriber connected ({SubscriberCount} total)");
            }
        }

        private void Broadcast(byte[] message)
        {
            TcpClient[] targets;
            lock (_subscribers)
                targets = _subscribers.ToArray();

            foreach (var client in targets)
            {
                try
                {
                    client.GetStream().Write(message);
                    Interlocked.Increment(ref _delivered);
                }
                catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or InvalidOperationException)
                {
                    lock (_subscribers)
                        _subscribers.Remove(client);
                    client.Close();
                    Log?.Invoke($"[GS] subscriber dropped ({SubscriberCount} left)");
                }
            }
        }
    }
}
