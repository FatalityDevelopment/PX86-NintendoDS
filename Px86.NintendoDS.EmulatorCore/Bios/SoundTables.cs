namespace Px86.NintendoDS.EmulatorCore.Bios
{
    /// <summary>
    /// The ARM7 BIOS sound tables returned by SWI 1Ah–1Ch, computed from their
    /// definitions instead of copied from a BIOS image.
    ///
    ///   Sine   64 entries   sin(i × 90° / 64) × 8000h          (0000h..7FF5h)
    ///   Pitch  768 entries  10000h × (2^(i/768) − 1)            (0000h..FF8Ah)
    ///   Volume 724 entries  amplitude for (i − 723) / 10 dB, 0–7Fh, scaled
    ///                       up ×2/×4/×16 below −6/−12/−24 dB so it pairs with
    ///                       the SPU's volume-divider shift (as Nintendo's
    ///                       sound library expects)
    ///
    /// Reference: GBATEK "BIOS Misc Functions" (GetSineTable / GetPitchTable / GetVolumeTable).
    /// </summary>
    public static class SoundTables
    {
        public static readonly uint[] Sine = BuildSine();
        public static readonly uint[] Pitch = BuildPitch();
        public static readonly uint[] Volume = BuildVolume();

        private static uint[] BuildSine()
        {
            var table = new uint[64];
            for (int i = 0; i < table.Length; i++)
                table[i] = (uint)Math.Round(Math.Sin(i * Math.PI / 128) * 0x8000);
            return table;
        }

        private static uint[] BuildPitch()
        {
            var table = new uint[768];
            for (int i = 0; i < table.Length; i++)
                table[i] = (uint)Math.Round(0x10000 * (Math.Pow(2, i / 768.0) - 1));
            return table;
        }

        private static uint[] BuildVolume()
        {
            var table = new uint[724];
            for (int i = 0; i < table.Length; i++)
            {
                double decibels = (i - 723) / 10.0;
                double amplitude = 127 * Math.Pow(10, decibels / 20);
                int scale = i < 723 - 240 ? 16 : i < 723 - 120 ? 4 : i < 723 - 60 ? 2 : 1;
                table[i] = (uint)Math.Min(127, Math.Round(amplitude * scale));
            }
            return table;
        }
    }
}
