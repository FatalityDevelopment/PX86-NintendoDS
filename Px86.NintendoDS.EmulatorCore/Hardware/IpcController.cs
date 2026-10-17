namespace Px86.NintendoDS.EmulatorCore.Hardware
{
    /// <summary>
    /// Inter-processor communication between the ARM9 (side 0) and the ARM7 (side 1).
    ///
    ///   04000180h  IPCSYNC     bits 0–3 in (remote's 8–11), 8–11 out, 13 send IRQ, 14 IRQ enable
    ///   04000184h  IPCFIFOCNT  0 send empty, 1 send full, 2 send-empty IRQ, 3 send clear (W),
    ///                          8 recv empty, 9 recv full, 10 recv-not-empty IRQ,
    ///                          14 error (W1 = acknowledge), 15 FIFO enable
    ///   04000188h  IPCFIFOSEND (W)
    ///   04100000h  IPCFIFORECV (R)
    ///
    /// Each side owns a 16-word send FIFO, which is the other side's receive
    /// FIFO. The FIFO IRQs are edge-triggered on the conditions
    /// "send-empty IRQ enabled AND send empty" and "recv IRQ enabled AND recv not empty".
    ///
    /// Reference: GBATEK "DS Inter Process Communication (IPC)".
    /// </summary>
    public sealed class IpcController
    {
        public const uint REG_IPCSYNC     = 0x180;
        public const uint REG_IPCFIFOCNT  = 0x184;
        public const uint REG_IPCFIFOSEND = 0x188;
        public const uint REG_IPCFIFORECV = 0x100000;

        public const int FifoDepth = 16;

        private readonly InterruptController[] _irq;
        private readonly Queue<uint>[] _send = { new(FifoDepth), new(FifoDepth) };
        private readonly ushort[] _sync = new ushort[2];
        private readonly ushort[] _fifoControl = new ushort[2];
        private readonly uint[] _lastReceived = new uint[2];

        public IpcController(InterruptController arm9Irq, InterruptController arm7Irq)
        {
            _irq = new[] { arm9Irq, arm7Irq };
        }

        public void Reset()
        {
            foreach (var q in _send) q.Clear();
            Array.Clear(_sync);
            Array.Clear(_lastReceived);
            _fifoControl[0] = _fifoControl[1] = 0x0101;
        }

        private static int Other(int side) => side ^ 1;

        // ── IPCSYNC ───────────────────────────────────────────────────────────

        public ushort ReadSync(int side) =>
            (ushort)((_sync[side] & 0x4F00) | ((_sync[Other(side)] >> 8) & 0xF));

        public void WriteSync(int side, ushort value, ushort mask = 0xFFFF)
        {
            _sync[side] = (ushort)((_sync[side] & ~(mask & 0x4F00)) | (value & mask & 0x4F00));
            if ((value & mask & 0x2000) != 0 && (_sync[Other(side)] & 0x4000) != 0)
                _irq[Other(side)].Request(InterruptController.IRQ_IPC_SYNC);
        }

        // ── IPCFIFOCNT ────────────────────────────────────────────────────────

        public ushort ReadFifoControl(int side)
        {
            var send = _send[side];
            var recv = _send[Other(side)];
            int value = _fifoControl[side] & 0xC404;
            if (send.Count == 0) value |= 0x0001;
            if (send.Count == FifoDepth) value |= 0x0002;
            if (recv.Count == 0) value |= 0x0100;
            if (recv.Count == FifoDepth) value |= 0x0200;
            return (ushort)value;
        }

        public void WriteFifoControl(int side, ushort value, ushort mask = 0xFFFF)
        {
            value &= mask;
            bool sendIrqBefore = SendEmptyCondition(side);
            bool recvIrqBefore = RecvNotEmptyCondition(side);

            if ((value & 0x0008) != 0)
                _send[side].Clear();

            ushort control = _fifoControl[side];
            control = (ushort)((control & ~(mask & 0x8404)) | (value & 0x8404));
            if ((value & 0x4000) != 0)
                control &= unchecked((ushort)~0x4000);
            _fifoControl[side] = control;

            if (!sendIrqBefore && SendEmptyCondition(side))
                _irq[side].Request(InterruptController.IRQ_IPC_SEND_EMPTY);
            if (!recvIrqBefore && RecvNotEmptyCondition(side))
                _irq[side].Request(InterruptController.IRQ_IPC_RECV);
        }

        private bool SendEmptyCondition(int side) =>
            (_fifoControl[side] & 0x0004) != 0 && _send[side].Count == 0;

        private bool RecvNotEmptyCondition(int side) =>
            (_fifoControl[side] & 0x0400) != 0 && _send[Other(side)].Count != 0;

        // ── FIFO data ─────────────────────────────────────────────────────────

        public void Send(int side, uint value)
        {
            if ((_fifoControl[side] & 0x8000) == 0)
                return;

            var queue = _send[side];
            if (queue.Count == FifoDepth)
            {
                _fifoControl[side] |= 0x4000;
                return;
            }

            int other = Other(side);
            bool remoteBefore = RecvNotEmptyCondition(other);
            queue.Enqueue(value);
            if (!remoteBefore && RecvNotEmptyCondition(other))
                _irq[other].Request(InterruptController.IRQ_IPC_RECV);
        }

        public uint Receive(int side)
        {
            int other = Other(side);
            var queue = _send[other];

            if (queue.Count == 0)
            {
                _fifoControl[side] |= 0x4000;
                return _lastReceived[side];
            }

            if ((_fifoControl[side] & 0x8000) == 0)
                return queue.Peek();

            bool remoteBefore = SendEmptyCondition(other);
            uint value = queue.Dequeue();
            _lastReceived[side] = value;
            if (!remoteBefore && SendEmptyCondition(other))
                _irq[other].Request(InterruptController.IRQ_IPC_SEND_EMPTY);
            return value;
        }
    }
}
