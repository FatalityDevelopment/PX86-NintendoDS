namespace Px86.NintendoDS.EmulatorCore.Hardware
{
    /// <summary>
    /// ARM9 hardware divider and square-root unit.
    ///
    ///   04000280h  DIVCNT         bits 0–1 mode (0: 32/32, 1: 64/32, 2: 64/64, 3 = 1), 14 DIV0, 15 busy
    ///   04000290h  DIV_NUMER      64-bit signed
    ///   04000298h  DIV_DENOM      64-bit signed
    ///   040002A0h  DIV_RESULT     64-bit quotient (R)
    ///   040002A8h  DIVREM_RESULT  64-bit remainder (R)
    ///   040002B0h  SQRTCNT        bit 0 mode (0: 32-bit input, 1: 64-bit), 15 busy
    ///   040002B4h  SQRT_RESULT    32-bit (R)
    ///   040002B8h  SQRT_PARAM     64-bit unsigned
    ///
    /// Results are computed immediately on any write; the busy bit always reads 0.
    /// Overflow rules (GBATEK "DS Maths"):
    ///   x / 0      → remainder = x, quotient = +1 or −1 (opposite sign of x)
    ///   −MAX / −1  → quotient = −MAX
    ///   In mode 0 the upper 32 bits of the sign-extended quotient are inverted
    ///   on either overflow.
    /// </summary>
    public sealed class MathUnit
    {
        public const uint REG_DIVCNT        = 0x280;
        public const uint REG_DIV_NUMER     = 0x290;
        public const uint REG_DIV_DENOM     = 0x298;
        public const uint REG_DIV_RESULT    = 0x2A0;
        public const uint REG_DIVREM_RESULT = 0x2A8;
        public const uint REG_SQRTCNT       = 0x2B0;
        public const uint REG_SQRT_RESULT   = 0x2B4;
        public const uint REG_SQRT_PARAM    = 0x2B8;

        public ushort DivControl { get; private set; }
        public ulong DivNumerator { get; private set; }
        public ulong DivDenominator { get; private set; }
        public ulong DivResult { get; private set; }
        public ulong DivRemainder { get; private set; }
        public ushort SqrtControl { get; private set; }
        public uint SqrtResult { get; private set; }
        public ulong SqrtParameter { get; private set; }

        public void Reset()
        {
            DivControl = SqrtControl = 0;
            DivNumerator = DivDenominator = DivResult = DivRemainder = 0;
            SqrtResult = 0;
            SqrtParameter = 0;
        }

        public void WriteDivControl(ushort value)
        {
            DivControl = (ushort)((DivControl & 0x4000) | (value & 3));
            Divide();
        }

        /// <summary>Write the low (wordIndex 0) or high (1) 32 bits of DIV_NUMER.</summary>
        public void WriteNumerator(int wordIndex, uint value)
        {
            DivNumerator = SetWord(DivNumerator, wordIndex, value);
            Divide();
        }

        public void WriteDenominator(int wordIndex, uint value)
        {
            DivDenominator = SetWord(DivDenominator, wordIndex, value);
            Divide();
        }

        public void WriteSqrtControl(ushort value)
        {
            SqrtControl = (ushort)(value & 1);
            SquareRoot();
        }

        public void WriteSqrtParameter(int wordIndex, uint value)
        {
            SqrtParameter = SetWord(SqrtParameter, wordIndex, value);
            SquareRoot();
        }

        private static ulong SetWord(ulong current, int wordIndex, uint value) =>
            wordIndex == 0
                ? (current & 0xFFFFFFFF00000000ul) | value
                : (current & 0x00000000FFFFFFFFul) | ((ulong)value << 32);

        private void Divide()
        {
            int mode = DivControl & 3;
            if (DivDenominator == 0)
                DivControl |= 0x4000;
            else
                DivControl &= unchecked((ushort)~0x4000);

            switch (mode)
            {
                case 0:
                {
                    int numer = (int)DivNumerator;
                    int denom = (int)DivDenominator;
                    if (denom == 0)
                    {
                        DivRemainder = (ulong)(long)numer;
                        long quotient = numer < 0 ? 1 : -1;
                        DivResult = (ulong)quotient ^ 0xFFFFFFFF00000000ul;
                    }
                    else if (numer == int.MinValue && denom == -1)
                    {
                        DivResult = unchecked((ulong)(long)int.MinValue) ^ 0xFFFFFFFF00000000ul;
                        DivRemainder = 0;
                    }
                    else
                    {
                        DivResult = (ulong)(long)(numer / denom);
                        DivRemainder = (ulong)(long)(numer % denom);
                    }
                    break;
                }
                case 1:
                case 3:
                {
                    long numer = (long)DivNumerator;
                    int denom = (int)DivDenominator;
                    if (denom == 0)
                    {
                        DivRemainder = (ulong)numer;
                        DivResult = numer < 0 ? 1ul : ulong.MaxValue;
                    }
                    else if (numer == long.MinValue && denom == -1)
                    {
                        DivResult = unchecked((ulong)long.MinValue);
                        DivRemainder = 0;
                    }
                    else
                    {
                        DivResult = (ulong)(numer / denom);
                        DivRemainder = (ulong)(long)(numer % denom);
                    }
                    break;
                }
                default:
                {
                    long numer = (long)DivNumerator;
                    long denom = (long)DivDenominator;
                    if (denom == 0)
                    {
                        DivRemainder = (ulong)numer;
                        DivResult = numer < 0 ? 1ul : ulong.MaxValue;
                    }
                    else if (numer == long.MinValue && denom == -1)
                    {
                        DivResult = unchecked((ulong)long.MinValue);
                        DivRemainder = 0;
                    }
                    else
                    {
                        DivResult = (ulong)(numer / denom);
                        DivRemainder = (ulong)(numer % denom);
                    }
                    break;
                }
            }
        }

        private void SquareRoot()
        {
            ulong value = (SqrtControl & 1) != 0 ? SqrtParameter : SqrtParameter & 0xFFFFFFFFul;
            SqrtResult = IntegerSquareRoot(value);
        }

        public static uint IntegerSquareRoot(ulong value)
        {
            ulong result = 0;
            ulong bit = 1ul << 62;
            while (bit > value)
                bit >>= 2;
            while (bit != 0)
            {
                if (value >= result + bit)
                {
                    value -= result + bit;
                    result = (result >> 1) + bit;
                }
                else
                {
                    result >>= 1;
                }
                bit >>= 2;
            }
            return (uint)result;
        }
    }
}
