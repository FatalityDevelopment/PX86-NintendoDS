using Px86.NintendoDS.Common.Core;

namespace Px86.NintendoDS.EmulatorCore.Hardware.Wifi
{
    /// <summary>
    /// The RF transceiver behind the second serial port (RF9008 on the DS,
    /// MM3218 on the DS-Lite).
    ///
    ///   480817Ch  W_RF_DATA2  Type 2: 0–1 data bits 16–17, 2–6 index, 7 read;  Type 3: 0–3 command (5 write, 6 read)
    ///   480817Eh  W_RF_DATA1  Type 2: data bits 0–15;  Type 3: 0–7 data, 8–15 index (40h–FFh mirror 00h–3Fh)
    ///   4808180h  W_RF_BUSY   0 busy (instant here)
    ///   4808184h  W_RF_CNT    0–5 transfer length (24 bits = type 2, 20 bits = type 3), 8 flag
    ///
    /// Writing DATA2 starts the transfer; a read leaves the value in DATA1/DATA2
    /// the same way it was sent. The registers are plain storage; RF[1Fh]
    /// writes reset the type-2 chip.
    ///
    /// Reference: GBATEK "DS Wifi RF Chip", "DS Wifi RF9008 Registers".
    /// </summary>
    public sealed class WifiRf
    {
        private static readonly uint[] PowerOn =
        {
            0x00007, 0x09003, 0x00022, 0x1FF78, 0x09003, 0x01780, 0x00000, 0x14578,
            0x1E742, 0x00120, 0x00000, 0x00000, 0x00000, 0x00000, 0x00000, 0x00000,
        };

        private readonly uint[] _registers = new uint[0x40];

        public void Reset()
        {
            Array.Clear(_registers);
            PowerOn.CopyTo(_registers, 0);
            _registers[0x1B] = 0x0000F;
            _registers[0x1F] = 0x00001;
        }

        public uint this[int index] => _registers[index & 0x3F];

        /// <summary>A W_RF_DATA2 write. Returns the new (DATA1, DATA2) pair after a read.</summary>
        public (ushort Data1, ushort Data2) Transfer(ushort control, ushort data1, ushort data2)
        {
            bool type3 = (control & 0x3F) != 0x18;
            if (type3)
            {
                int index = (data1 >> 8) & 0x3F;
                switch (data2 & 0xF)
                {
                    case 5: _registers[index] = (byte)data1; break;
                    case 6: data1 = (ushort)((data1 & 0xFF00u) | (_registers[index] & 0xFFu)); break;
                }
                return (data1, data2);
            }

            int reg = (data2 >> 2) & 0x1F;
            if ((data2 & 0x80) == 0)
            {
                uint value = ((uint)(data2 & 3) << 16) | data1;
                if (reg == 0x1F) Reset();
                else _registers[reg] = value;
                return (data1, data2);
            }
            uint read = _registers[reg];
            return ((ushort)read, (ushort)((data2 & 0xFCu) | ((read >> 16) & 3)));
        }

        public void Serialize(StateSerializer s)
        {
            s.Section("WifiRF");
            s.Sync(_registers);
        }
    }
}
