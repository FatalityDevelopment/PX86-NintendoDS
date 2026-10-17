using System.Collections.Concurrent;

namespace Px86.NintendoDS.EmulatorCore.Hardware.Wifi
{
    /// <summary>
    /// One 802.11 frame on the air: the IEEE header and body as the receiver
    /// stores them (no FCS), plus what the receiving hardware needs to know.
    /// </summary>
    public sealed class WifiPacket
    {
        /// <summary>IEEE 802.11 header + body, without the 4-byte FCS.</summary>
        public required byte[] Frame { get; init; }

        /// <summary>Transfer rate in 100 kbit/s units (0Ah = 1 Mbit/s, 14h = 2 Mbit/s), as in the RX header.</summary>
        public int Rate { get; init; } = 0x14;

        /// <summary>Multiplay slave number of the sender (W_AID_LOW) for REPLY frames, else 0.</summary>
        public int ReplyAid { get; init; }

        /// <summary>A unique id of the sending console, so a medium never hands a console its own frames.</summary>
        public long Sender { get; init; }

        public ushort FrameControl => Frame.Length >= 2 ? (ushort)(Frame[0] | Frame[1] << 8) : (ushort)0;
    }

    /// <summary>
    /// What the Wifi hardware transmits into and receives from: nothing, other
    /// emulated consoles (local wireless) or a software access point (online).
    /// Implementations must be thread-safe: consoles on other threads (or the
    /// network) deliver into them while the owning console polls.
    /// </summary>
    public interface IWifiMedium
    {
        /// <summary>Put a frame on the air. It must not come back to <see cref="WifiPacket.Sender"/>.</summary>
        void Send(WifiPacket packet);

        /// <summary>A frame for <paramref name="receiver"/>, waiting up to <paramref name="timeout"/> for one.</summary>
        bool TryReceive(long receiver, TimeSpan timeout, out WifiPacket packet);

        /// <summary>Consoles (other than <paramref name="self"/>) heard from recently; a multiplay master only waits for those.</summary>
        int PeerCount(long self);
    }

    /// <summary>Nobody out there: transmits vanish, nothing is received.</summary>
    public sealed class NullWifiMedium : IWifiMedium
    {
        public static readonly NullWifiMedium Instance = new();

        public void Send(WifiPacket packet) { }

        public bool TryReceive(long receiver, TimeSpan timeout, out WifiPacket packet)
        {
            packet = null!;
            return false;
        }

        public int PeerCount(long self) => 0;
    }

    /// <summary>
    /// An in-process "room" joining several consoles (or a console and a
    /// software access point): every frame goes to every other member.
    /// Used for tests, and as the hub that network media feed into.
    /// </summary>
    public sealed class WifiHub : IWifiMedium
    {
        private readonly ConcurrentDictionary<long, BlockingCollection<WifiPacket>> _queues = new();

        /// <summary>Add a member; frames sent from now on reach it.</summary>
        public void Join(long member) => _queues.TryAdd(member, new BlockingCollection<WifiPacket>(new ConcurrentQueue<WifiPacket>(), 4096));

        public void Leave(long member) => _queues.TryRemove(member, out _);

        public void Send(WifiPacket packet)
        {
            foreach (var (member, queue) in _queues)
            {
                if (member != packet.Sender)
                    queue.TryAdd(packet); // a full queue drops the frame, like a busy channel
            }
        }

        public bool TryReceive(long receiver, TimeSpan timeout, out WifiPacket packet)
        {
            packet = null!;
            if (!_queues.TryGetValue(receiver, out var queue))
                return false;
            return timeout <= TimeSpan.Zero ? queue.TryTake(out packet!) : queue.TryTake(out packet!, timeout);
        }

        public int PeerCount(long self) => _queues.Count - (_queues.ContainsKey(self) ? 1 : 0);
    }
}
