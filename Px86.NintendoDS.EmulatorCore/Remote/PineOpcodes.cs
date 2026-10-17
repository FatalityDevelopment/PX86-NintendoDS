namespace Px86.NintendoDS.EmulatorCore.Remote
{
    /// <summary>
    /// PINE opcode numbers. 0x00–0x0F are upstream PINE, 0x10–0x42 follow
    /// CodeDesigner's PCSX2 PINE extension (<c>CodeDesigner.Ps2.Bridge.PineClient</c>)
    /// so the same client code can drive this emulator, and 0x60+ are
    /// Nintendo DS additions. See docs/pine-protocol.md for every layout.
    /// </summary>
    public static class PineOpcodes
    {
        // ── Upstream PINE ─────────────────────────────────────────────────────
        public const byte Read8 = 0x00;
        public const byte Read16 = 0x01;
        public const byte Read32 = 0x02;
        public const byte Read64 = 0x03;
        public const byte Write8 = 0x04;
        public const byte Write16 = 0x05;
        public const byte Write32 = 0x06;
        public const byte Write64 = 0x07;
        public const byte Version = 0x08;
        public const byte SaveState = 0x09;
        public const byte LoadState = 0x0A;
        public const byte Title = 0x0B;
        public const byte Id = 0x0C;
        public const byte Uuid = 0x0D;
        public const byte GameVersion = 0x0E;
        public const byte Status = 0x0F;

        // ── Debugger extension (CodeDesigner) ─────────────────────────────────
        public const byte GetProgramCounter = 0x10;
        public const byte Pause = 0x11;
        public const byte Resume = 0x12;
        public const byte Step = 0x13;
        public const byte SetBreakpoint = 0x14;
        public const byte ClearBreakpoint = 0x15;
        public const byte ClearAllBreakpoints = 0x16;
        public const byte GetRegisters = 0x17;
        public const byte GetRegister = 0x18;
        public const byte SetRegister = 0x19;
        public const byte GetEEThreads = 0x1A;
        public const byte GetIOPThreads = 0x1B;
        public const byte GetModules = 0x1C;
        public const byte GetStack = 0x1D;
        public const byte StepInto = 0x1E;
        public const byte StepOver = 0x1F;
        public const byte StepOut = 0x20;
        public const byte GetSymbol = 0x21;
        public const byte SaveStateFile = 0x22;
        public const byte LoadStateFile = 0x23;
        public const byte Reset = 0x24;
        public const byte FrameAdvance = 0x25;
        public const byte GetFps = 0x26;
        public const byte SetLimiterMode = 0x27;
        public const byte ListBreakpoints = 0x28;
        public const byte Disassemble = 0x29;
        public const byte ListFunctions = 0x2A;
        public const byte GetSymbolByName = 0x2B;
        public const byte ListGlobals = 0x2C;
        public const byte GetLocals = 0x2D;

        // ── Watchpoints / snapshots ───────────────────────────────────────────
        public const byte AddWatch = 0x2E;
        public const byte RemoveWatch = 0x2F;
        public const byte ListWatches = 0x30;
        public const byte ClearAllWatches = 0x31;
        public const byte SaveScreenshot = 0x32;

        // ── Pad ───────────────────────────────────────────────────────────────
        public const byte PadGetState = 0x33;
        public const byte PadSetButton = 0x34;
        public const byte PadSetAnalog = 0x35;
        public const byte PadSetState = 0x36;
        public const byte PadGetType = 0x37;

        // 0x38–0x3F: PS2 network adapter (not applicable, always fail).

        // ── GS stream discovery ───────────────────────────────────────────────
        public const byte GsStreamGetPort = 0x40;
        public const byte GsStreamGetStatus = 0x41;
        public const byte GsStreamPing = 0x42;

        // ── Nintendo DS extension ─────────────────────────────────────────────
        public const byte SelectCpu = 0x60;
        public const byte ReadBuffer = 0x61;
        public const byte WriteBuffer = 0x62;
        public const byte SetTouch = 0x63;
        public const byte GetFrameCount = 0x64;
        public const byte LoadRom = 0x65;
        public const byte GetBreakInfo = 0x66;
        public const byte Shutdown = 0x67;
        public const byte CaptureGx = 0x6F;

        // ── Result codes ──────────────────────────────────────────────────────
        public const byte IpcOk = 0x00;
        public const byte IpcFail = 0xFF;

        // ── GetRegister / SetRegister categories ──────────────────────────────
        /// <summary>num 0–15 = r0–r15 (r15 = next instruction), 16 = CPSR, 17 = SPSR.</summary>
        public const byte CategoryGpr = 0;
        /// <summary>ARM9 only. num 0 = control, 1 = DTCM region, 2 = ITCM region, 3 = main ID.</summary>
        public const byte CategoryCp15 = 1;
    }
}
