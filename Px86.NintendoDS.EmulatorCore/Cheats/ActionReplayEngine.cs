using Px86.NintendoDS.EmulatorCore.Debugging;

namespace Px86.NintendoDS.EmulatorCore.Cheats
{
    /// <summary>Memory the cheat engine reads and writes.</summary>
    public interface ICheatMemory
    {
        uint Read32(uint address);
        ushort Read16(uint address);
        byte Read8(uint address);
        void Write32(uint address, uint value);
        void Write16(uint address, ushort value);
        void Write8(uint address, byte value);
    }

    /// <summary>The ARM9's view of memory (main RAM, TCMs, VRAM…), without watchpoints.</summary>
    public sealed class EmulatorCheatMemory : ICheatMemory
    {
        private readonly NdsEmulator _nds;

        public EmulatorCheatMemory(NdsEmulator nds) => _nds = nds;

        public uint Read32(uint address) => DebugMemory.Read32(_nds, DebugCpu.Arm9, address & ~3u);
        public ushort Read16(uint address) => DebugMemory.Read16(_nds, DebugCpu.Arm9, address & ~1u);
        public byte Read8(uint address) => DebugMemory.Read8(_nds, DebugCpu.Arm9, address);
        public void Write32(uint address, uint value) => DebugMemory.Write32(_nds, DebugCpu.Arm9, address & ~3u, value);
        public void Write16(uint address, ushort value) => DebugMemory.Write16(_nds, DebugCpu.Arm9, address & ~1u, value);
        public void Write8(uint address, byte value) => DebugMemory.Write8(_nds, DebugCpu.Arm9, address, value);
    }

    /// <summary>
    /// Runs Action Replay DS codes, once per frame like the real device.
    ///
    ///   0XXXXXXX YYYYYYYY  word[X+offset] = Y       1… half   2… byte
    ///   3–6 XXXXXXX Y      IF Y &gt; / &lt; / = / ≠ word[X]   (X = 0: word[offset])
    ///   7–A XXXXXXX ZZZZYYYY  IF Y &gt; / &lt; / = / ≠ (~Z &amp; half[X])
    ///   BXXXXXXX           offset = word[X+offset]
    ///   C0 Y               FOR Y+1 times       C4 offset = address of this code (unsupported)
    ///   C5 XXXXYYYY        counter++, IF (counter &amp; Y) = X     C6 X  word[X] = offset
    ///   D0 ENDIF  D1 NEXT  D2 NEXT + reset everything
    ///   D3 offset = X   D4 data += X   D5 data = X   D6/D7/D8 store data (+4/+2/+1)
    ///   D9/DA/DB load data   DC offset += X
    ///   EXXXXXXX YYYYYYYY  copy the next Y parameter bytes to [X+offset]
    ///   FXXXXXXX YYYYYYYY  copy Y bytes from [offset] to [X]
    ///
    /// IFs nest (up to 32). Every code but D0/D1/D2 is skipped while a
    /// condition is false; nested IFs inside a false block still count, so
    /// their ENDIFs match. FOR saves the condition state and NEXT restores
    /// it. "00000000 XXXXXXXX" lines are hook settings and are ignored.
    ///
    /// Reference: GBATEK "DS Cart Cheat Action Replay DS".
    /// </summary>
    public static class ActionReplayEngine
    {
        private const int MaxNesting = 32;
        private const int MaxSteps = 1_000_000; // runaway-loop guard per frame

        public static void Execute(IReadOnlyList<uint> words, ICheatMemory memory, ref uint counter)
        {
            uint offset = 0, data = 0;
            // Condition stack: bit i set = level i is false. Executing while no level is false.
            uint falseLevels = 0;
            int depth = 0;

            int loopStart = -1;
            uint loopCount = 0, loopTarget = 0;
            uint savedFalse = 0;
            int savedDepth = 0;

            int steps = 0;
            for (int pc = 0; pc + 1 < words.Count && steps++ < MaxSteps; pc += 2)
            {
                uint left = words[pc], right = words[pc + 1];
                uint type = left >> 28;
                uint address = left & 0x0FFFFFFF;
                bool executing = falseLevels == 0;

                if (left == 0)
                    continue; // hook / master code setting

                switch (type)
                {
                    case 0x0:
                        if (executing) memory.Write32(address + offset, right);
                        continue;
                    case 0x1:
                        if (executing) memory.Write16(address + offset, (ushort)right);
                        continue;
                    case 0x2:
                        if (executing) memory.Write8(address + offset, (byte)right);
                        continue;

                    case >= 0x3 and <= 0xA:
                    {
                        bool result = false;
                        if (executing)
                        {
                            if (type <= 0x6)
                            {
                                uint value = memory.Read32(address == 0 ? offset : address);
                                result = type switch { 0x3 => right > value, 0x4 => right < value, 0x5 => right == value, _ => right != value };
                            }
                            else
                            {
                                uint half = (uint)(~(right >> 16) & 0xFFFF) & memory.Read16(address == 0 ? offset : address);
                                uint y = right & 0xFFFF;
                                result = type switch { 0x7 => y > half, 0x8 => y < half, 0x9 => y == half, _ => y != half };
                            }
                        }
                        Push(ref falseLevels, ref depth, !result);
                        continue;
                    }

                    case 0xB:
                        if (executing) offset = memory.Read32(address + offset);
                        continue;

                    case 0xE:
                    {
                        // Parameter bytes follow the code, padded to whole 8-byte lines.
                        int lines = (int)((right + 7) / 8);
                        if (executing)
                        {
                            uint target = address + offset;
                            for (uint i = 0; i < right; i++)
                            {
                                int wordIndex = pc + 2 + (int)(i / 4);
                                if (wordIndex >= words.Count) break;
                                memory.Write8(target + i, (byte)(words[wordIndex] >> (int)(8 * (i % 4))));
                            }
                        }
                        pc += lines * 2;
                        continue;
                    }

                    case 0xF:
                        if (executing)
                            for (uint i = 0; i < right; i++)
                                memory.Write8(address + i, memory.Read8(offset + i));
                        continue;
                }

                // Cxxxxxxx / Dxxxxxxx: the second hex digit is the sub-type.
                uint sub = left >> 24;
                switch (sub)
                {
                    case 0xC0:
                        if (!executing) continue;
                        loopStart = pc + 2;
                        loopCount = 0;
                        loopTarget = right;
                        savedFalse = falseLevels;
                        savedDepth = depth;
                        continue;
                    case 0xC4:
                        continue; // offset = address of this code in the AR's own memory: nothing equivalent here
                    case 0xC5:
                    {
                        counter++;
                        bool result = executing && (counter & (right & 0xFFFF)) == right >> 16;
                        Push(ref falseLevels, ref depth, !result);
                        continue;
                    }
                    case 0xC6:
                        if (executing) memory.Write32(right, offset);
                        continue;

                    case 0xD0:
                        Pop(ref falseLevels, ref depth);
                        continue;
                    case 0xD1:
                    case 0xD2:
                        if (loopStart >= 0 && loopCount < loopTarget)
                        {
                            loopCount++;
                            falseLevels = savedFalse;
                            depth = savedDepth;
                            pc = loopStart - 2;
                            continue;
                        }
                        if (sub == 0xD2)
                        {
                            // Flush: start over with clean registers and no open IFs.
                            offset = 0;
                            data = 0;
                            falseLevels = 0;
                            depth = 0;
                        }
                        else if (loopStart >= 0)
                        {
                            falseLevels = savedFalse;
                            depth = savedDepth;
                        }
                        loopStart = -1; // a NEXT without a running FOR (e.g. skipped by an IF) does nothing else
                        continue;
                }

                if (!executing)
                    continue;
                switch (sub)
                {
                    case 0xD3: offset = right; break;
                    case 0xD4: data += right; break;
                    case 0xD5: data = right; break;
                    case 0xD6: memory.Write32(right + offset, data); offset += 4; break;
                    case 0xD7: memory.Write16(right + offset, (ushort)data); offset += 2; break;
                    case 0xD8: memory.Write8(right + offset, (byte)data); offset += 1; break;
                    case 0xD9: data = memory.Read32(right + offset); break;
                    case 0xDA: data = memory.Read16(right + offset); break;
                    case 0xDB: data = memory.Read8(right + offset); break;
                    case 0xDC: offset += right; break;
                }
            }
        }

        private static void Push(ref uint falseLevels, ref int depth, bool isFalse)
        {
            if (depth >= MaxNesting)
                return;
            if (isFalse)
                falseLevels |= 1u << depth;
            depth++;
        }

        private static void Pop(ref uint falseLevels, ref int depth)
        {
            if (depth == 0)
                return;
            depth--;
            falseLevels &= ~(1u << depth);
        }
    }
}
