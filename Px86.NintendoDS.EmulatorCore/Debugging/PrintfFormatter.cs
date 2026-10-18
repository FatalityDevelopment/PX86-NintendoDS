using System.Globalization;
using System.Text;

namespace Px86.NintendoDS.EmulatorCore.Debugging
{
    /// <summary>
    /// Where a hooked printf-family call keeps its variable arguments, read the
    /// way the ARM Procedure Call Standard passes them:
    ///
    ///   Registers  the arguments after the fixed ones are in the remaining
    ///              r0–r3, then on the stack at [sp], [sp+4], ... A 64-bit value
    ///              takes an even register pair (r2:r3) or an 8-byte aligned
    ///              stack slot; once one argument goes to the stack, all later
    ///              ones do.
    ///   va_list    a pointer to a list of 4-byte slots (64-bit values aligned to 8).
    ///
    /// Reads go through <see cref="DebugMemory"/>, so they have no side effects.
    ///
    /// Reference: ARM IHI 0042 "Procedure Call Standard for the ARM Architecture" §5.5, §7.1.4.
    /// </summary>
    public sealed class PrintfArgs
    {
        private readonly NdsEmulator _nds;
        private readonly DebugCpu _cpu;
        private readonly uint[] _regs;
        private int _reg;
        private uint _memory;

        private PrintfArgs(NdsEmulator nds, DebugCpu cpu, uint[] regs, int firstReg, uint memory)
        {
            _nds = nds;
            _cpu = cpu;
            _regs = regs;
            _reg = firstReg;
            _memory = memory;
        }

        /// <summary>Arguments starting at register <paramref name="firstReg"/> (0–4; 4 = all on the stack).</summary>
        public static PrintfArgs FromRegisters(NdsEmulator nds, DebugCpu cpu, uint[] r, int firstReg) =>
            new(nds, cpu, new[] { r[0], r[1], r[2], r[3] }, firstReg, r[13]);

        /// <summary>Arguments in a va_list at <paramref name="list"/>.</summary>
        public static PrintfArgs FromVaList(NdsEmulator nds, DebugCpu cpu, uint list) =>
            new(nds, cpu, Array.Empty<uint>(), 4, list);

        public uint Next32()
        {
            if (_reg < 4)
                return _regs[_reg++];
            uint v = DebugMemory.Read32(_nds, _cpu, _memory);
            _memory += 4;
            return v;
        }

        public ulong Next64()
        {
            if (_reg < 4)
            {
                _reg = (_reg + 1) & ~1;
                if (_reg < 4)
                {
                    ulong lo = _regs[_reg], hi = _regs[_reg + 1];
                    _reg += 2;
                    return hi << 32 | lo;
                }
            }
            _memory = (_memory + 7) & ~7u;
            ulong low = Next32(), high = Next32();
            return high << 32 | low;
        }
    }

    /// <summary>
    /// A C printf formatter for text that games print: conversions d i u o x X c s p f F e E g G n %,
    /// flags "- + space # 0", width and precision (also "*"), lengths hh h l ll j z t L.
    /// Unknown conversions are printed as written. Strings come from emulated memory and are
    /// capped, so a bad pointer gives garbage text, never an exception.
    ///
    /// Reference: ISO C99 §7.19.6.1 "The fprintf function".
    /// </summary>
    public static class PrintfFormatter
    {
        public const int MaxString = 1024;
        public const int MaxOutput = 4096;

        /// <summary>Read the zero-terminated string at <paramref name="address"/> (non-printable bytes become '?').</summary>
        public static string ReadString(NdsEmulator nds, DebugCpu cpu, uint address, int max = MaxString)
        {
            var text = new StringBuilder();
            for (int i = 0; i < max; i++)
            {
                byte b = DebugMemory.Read8(nds, cpu, address + (uint)i);
                if (b == 0)
                    break;
                text.Append(b is >= 0x20 and < 0x7F or (byte)'\n' or (byte)'\t' ? (char)b : '?');
            }
            return text.ToString();
        }

        /// <summary>Whether <paramref name="address"/> looks like the start of a format string: mapped, printable, terminated.</summary>
        public static bool LooksLikeString(NdsEmulator nds, DebugCpu cpu, uint address)
        {
            if (address < 0x01000000 || address >= 0x04000000 && address < 0xFFFF0000)
                return address < 0x8000 && cpu == DebugCpu.Arm9; // ITCM
            for (int i = 0; i < MaxString; i++)
            {
                byte b = DebugMemory.Read8(nds, cpu, address + (uint)i);
                if (b == 0)
                    return i > 0;
                if (b is < 0x20 and not (byte)'\n' and not (byte)'\t' and not (byte)'\r' || b == 0x7F)
                    return false;
            }
            return false;
        }

        public static string Format(NdsEmulator nds, DebugCpu cpu, string format, PrintfArgs args)
        {
            var o = new StringBuilder();
            int i = 0;
            while (i < format.Length && o.Length < MaxOutput)
            {
                char c = format[i];
                if (c != '%')
                {
                    o.Append(c);
                    i++;
                    continue;
                }
                int start = i++;
                bool left = false, plus = false, space = false, alt = false, zero = false;
                for (; i < format.Length; i++)
                {
                    switch (format[i])
                    {
                        case '-': left = true; continue;
                        case '+': plus = true; continue;
                        case ' ': space = true; continue;
                        case '#': alt = true; continue;
                        case '0': zero = true; continue;
                    }
                    break;
                }
                int width = 0;
                if (i < format.Length && format[i] == '*')
                {
                    width = (int)args.Next32();
                    if (width < 0) { left = true; width = -width; }
                    i++;
                }
                else
                    while (i < format.Length && char.IsAsciiDigit(format[i]))
                        width = width * 10 + format[i++] - '0';
                width = System.Math.Min(width, 256);
                int precision = -1;
                if (i < format.Length && format[i] == '.')
                {
                    i++;
                    precision = 0;
                    if (i < format.Length && format[i] == '*')
                    {
                        precision = (int)args.Next32();
                        i++;
                    }
                    else
                        while (i < format.Length && char.IsAsciiDigit(format[i]))
                            precision = precision * 10 + format[i++] - '0';
                    precision = System.Math.Min(precision, 256);
                }
                int length = 0; // -2 hh, -1 h, 0 int, 1 l, 2 ll
                while (i < format.Length)
                {
                    char m = format[i];
                    if (m == 'h') length = length == -1 ? -2 : -1;
                    else if (m == 'l') length = length == 1 ? 2 : 1;
                    else if (m is 'j' or 'q' or 'L') length = 2;
                    else if (m is 'z' or 't') length = 0;
                    else break;
                    i++;
                }
                if (i >= format.Length)
                {
                    o.Append(format, start, format.Length - start);
                    break;
                }
                char conv = format[i++];
                string body;
                string prefix = "";
                switch (conv)
                {
                    case 'd':
                    case 'i':
                    {
                        long v = length == 2 ? (long)args.Next64() : (int)args.Next32();
                        if (length == -1) v = (short)v;
                        else if (length == -2) v = (sbyte)v;
                        if (v < 0) prefix = "-";
                        else if (plus) prefix = "+";
                        else if (space) prefix = " ";
                        body = Digits(v < 0 ? (ulong)-v : (ulong)v, 10, false, precision);
                        break;
                    }
                    case 'u':
                    case 'o':
                    case 'x':
                    case 'X':
                    {
                        ulong v = length == 2 ? args.Next64() : args.Next32();
                        if (length == -1) v = (ushort)v;
                        else if (length == -2) v = (byte)v;
                        int radix = conv == 'u' ? 10 : conv == 'o' ? 8 : 16;
                        body = Digits(v, radix, conv == 'X', precision);
                        if (alt && v != 0 && radix == 16) prefix = conv == 'X' ? "0X" : "0x";
                        if (alt && radix == 8 && !body.StartsWith('0')) body = "0" + body;
                        break;
                    }
                    case 'p':
                        body = "0x" + args.Next32().ToString("x8");
                        break;
                    case 'c':
                    {
                        byte b = (byte)args.Next32();
                        body = (b is >= 0x20 and < 0x7F or (byte)'\n' or (byte)'\t' ? (char)b : '?').ToString();
                        zero = false;
                        break;
                    }
                    case 's':
                    {
                        uint p = args.Next32();
                        body = p == 0 ? "(null)" : ReadString(nds, cpu, p, precision >= 0 ? System.Math.Min(precision, MaxString) : MaxString);
                        zero = false;
                        break;
                    }
                    case 'f': case 'F': case 'e': case 'E': case 'g': case 'G':
                    {
                        // Varargs promote float to double (64 bits).
                        double v = BitConverter.Int64BitsToDouble((long)args.Next64());
                        if (v < 0 || double.IsNegative(v)) { prefix = "-"; v = -v; }
                        else if (plus) prefix = "+";
                        else if (space) prefix = " ";
                        int digits = precision < 0 ? 6 : precision;
                        string fraction = digits > 0 ? "." + new string('0', digits) : "";
                        string spec = conv switch
                        {
                            'f' or 'F' => "F" + digits,
                            'e' => "0" + fraction + "e+00",
                            'E' => "0" + fraction + "E+00",
                            _ => "G" + System.Math.Max(digits, 1),
                        };
                        body = double.IsFinite(v) ? v.ToString(spec, CultureInfo.InvariantCulture)
                            : double.IsNaN(v) ? "nan" : "inf";
                        if (conv == 'g') body = body.ToLowerInvariant();
                        if (char.IsUpper(conv)) body = body.ToUpperInvariant();
                        break;
                    }
                    case 'n':
                        args.Next32(); // writes the count back: nothing to print, and we never write game memory
                        continue;
                    case '%':
                        o.Append('%');
                        continue;
                    default:
                        o.Append(format, start, i - start);
                        continue;
                }
                bool numeric = conv is not ('s' or 'c' or 'p');
                int pad = width - prefix.Length - body.Length;
                if (pad <= 0)
                    o.Append(prefix).Append(body);
                else if (left)
                    o.Append(prefix).Append(body).Append(' ', pad);
                else if (zero && numeric && (precision < 0 || conv is 'f' or 'F' or 'e' or 'E' or 'g' or 'G'))
                    o.Append(prefix).Append('0', pad).Append(body);
                else
                    o.Append(' ', pad).Append(prefix).Append(body);
            }
            return o.Length > MaxOutput ? o.ToString(0, MaxOutput) : o.ToString();
        }

        private static string Digits(ulong value, int radix, bool upper, int precision)
        {
            if (value == 0 && precision == 0)
                return "";
            string s = radix switch
            {
                10 => value.ToString(CultureInfo.InvariantCulture),
                16 => value.ToString(upper ? "X" : "x"),
                _ => Convert.ToString((long)value, 8),
            };
            return precision > s.Length ? new string('0', precision - s.Length) + s : s;
        }
    }
}
