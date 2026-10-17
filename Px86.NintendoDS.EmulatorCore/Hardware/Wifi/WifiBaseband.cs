using Px86.NintendoDS.Common.Core;

namespace Px86.NintendoDS.EmulatorCore.Hardware.Wifi
{
    /// <summary>
    /// Mitsumi MM3155 baseband chip, reached through a serial port in the MAC.
    ///
    ///   4808158h  W_BB_CNT    0–7 index (00h–68h), 12–15 direction (5 = write BB_WRITE, 6 = read into BB_READ)
    ///   480815Ah  W_BB_WRITE  0–7 data for the next write
    ///   480815Ch  W_BB_READ   0–7 data from the last read
    ///   480815Eh  W_BB_BUSY   0 busy (transfers complete instantly here)
    ///
    /// 69h 8-bit registers. Most are plain storage that the firmware fills from
    /// its Wifi calibration block (firmware[064h..0CCh]); a few read fixed
    /// values: [00h] = 6Dh (chip ID), [5Dh] = 01h, [64h] = FFh, and the
    /// unimplemented ranges read 00h.
    ///
    /// Reference: GBATEK "DS Wifi Baseband Chip (BB)".
    /// </summary>
    public sealed class WifiBaseband
    {
        public const int RegisterCount = 0x69;

        private readonly byte[] _registers = new byte[0x100];

        public byte LastRead { get; private set; }

        public void Reset()
        {
            Array.Clear(_registers);
            LastRead = 0;
        }

        /// <summary>Firmware boot code: BB[0..68h] = firmware[064h..].</summary>
        public void LoadCalibration(ReadOnlySpan<byte> values)
        {
            for (int i = 0; i < Math.Min(values.Length, RegisterCount); i++)
                Write(i, values[i]);
        }

        public byte this[int index] => Read(index);

        /// <summary>A W_BB_CNT write: run one serial transfer.</summary>
        public void Transfer(ushort control, ushort writeData)
        {
            int index = control & 0xFF;
            switch (control >> 12)
            {
                case 5: Write(index, (byte)writeData); break;
                case 6: LastRead = Read(index); break;
            }
        }

        private static bool IsWritable(int i) =>
            i is (>= 0x01 and <= 0x0C) or (>= 0x13 and <= 0x15) or (>= 0x1B and <= 0x26) or (>= 0x28 and <= 0x4C)
              or (>= 0x4E and <= 0x5C) or 0x62 or 0x63 or 0x65 or 0x67 or 0x68;

        private void Write(int index, byte value)
        {
            if (IsWritable(index))
                _registers[index] = value;
        }

        private byte Read(int index) => index switch
        {
            0x00 => 0x6D,
            0x5D => 0x01,
            0x64 => 0xFF,
            _ => IsWritable(index) ? _registers[index] : (byte)0,
        };

        public void Serialize(StateSerializer s)
        {
            s.Section("WifiBB");
            s.Sync(_registers);
            byte last = LastRead;
            s.Sync(ref last);
            LastRead = last;
        }
    }
}
