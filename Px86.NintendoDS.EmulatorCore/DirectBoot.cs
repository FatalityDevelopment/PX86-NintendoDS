using Px86.NintendoDS.Common.Core;
using Px86.NintendoDS.Common.Cpu;
using Px86.NintendoDS.EmulatorCore.Cartridge;
using Px86.NintendoDS.FileFormats.Rom;

namespace Px86.NintendoDS.EmulatorCore
{
    /// <summary>
    /// Starts a game the way the firmware leaves the console after its boot
    /// menu, without running the BIOS or firmware:
    ///
    ///   • ARM9/ARM7 binaries copied from the ROM to their RAM addresses
    ///   • Cartridge header (170h bytes) copied to 027FFE00h
    ///   • Boot info: chip ID at 027FF800h/027FFC00h, header and secure-area
    ///     CRCs, boot indicator 027FFC40h = 1 (cartridge boot)
    ///   • Firmware user settings (70h bytes) copied to 027FFC80h
    ///   • CP15: DTCM at 03000000h (16 KB), ITCM 32 MB window, caches and TCMs on
    ///   • WRAMCNT = 3 (all shared WRAM to the ARM7), POSTFLG = 1, POWCNT1 = 820Fh
    ///   • Stack pointers as the BIOS sets them; both CPUs in System mode at
    ///     their entry points; cartridge in KEY2 main-data mode
    ///
    /// Reference: GBATEK "DS Cartridge Header", "DS Firmware User Settings",
    /// "BIOS RAM Usage"; behaviour cross-checked against the boot state other
    /// DS emulators document for direct boot.
    /// </summary>
    public static class DirectBoot
    {
        public const uint HeaderAddress = 0x027FFE00;
        public const uint UserSettingsAddress = 0x027FFC80;

        public static void Apply(NdsEmulator nds, NdsRom rom)
        {
            var header = rom.Header;

            // CP15 as left by the firmware.
            nds.Cp15.SetState(control: 0x0005707D, dtcmRegion: 0x0300000A, itcmRegion: 0x00000020);

            // WRAMCNT first: ARM7 binaries often load at 037F8000h, which is
            // shared WRAM only once it is mapped to the ARM7.
            nds.Control.WriteWramMode(3);

            var arm9 = rom.Arm9Binary.ToArray();
            SecureAreaStatus = PrepareSecureArea(nds, rom, arm9);
            CopyBinary(nds.Arm9Bus.BusWrite8, arm9, header.Arm9RamAddress);
            CopyBinary(nds.Arm7Bus.Write8, rom.Arm7Binary, header.Arm7RamAddress);

            for (int i = 0; i < 0x170; i++)
                nds.Arm9Bus.BusWrite8(HeaderAddress + (uint)i, rom.Data[i]);

            uint chipId = nds.Cartridge.ChipId;
            WriteBootInfo(nds, 0x027FF800, chipId, header.HeaderCrc, header.SecureAreaCrc);
            WriteBootInfo(nds, 0x027FFC00, chipId, header.HeaderCrc, header.SecureAreaCrc);
            nds.Arm9Bus.BusWrite16(0x027FF850, 0x5835);
            nds.Arm9Bus.BusWrite16(0x027FFC10, 0x5835);
            nds.Arm9Bus.BusWrite16(0x027FFC30, 0xFFFF);
            nds.Arm9Bus.BusWrite16(0x027FFC40, 0x0001);

            var settings = nds.Spi.Firmware.UserSettings;
            for (int i = 0; i < 0x70; i++)
                nds.Arm9Bus.BusWrite8(UserSettingsAddress + (uint)i, settings[i]);

            nds.Control.PostFlag9 = 1;
            nds.Control.PostFlag7 = 1;
            nds.Control.WritePowerControl9(0x820F);
            nds.Control.WritePowerControl7(0x0001);
            nds.Arm7Bus.Write16(0x04000504, 0x0200); // SOUNDBIAS

            nds.Cartridge.Mode = CartridgeSlot.ProtocolMode.Main;
            nds.Cartridge.WriteSpiControl(0x8000);
            nds.Cartridge.WriteRomControl(0x20000000u | (header.NormalCardControl & 0x1FFFFFFFu & ~0x80000000u));

            SetupCpu(nds.Arm9, header.Arm9EntryAddress, irqStack: 0x03003F80, svcStack: 0x03003FC0, sysStack: 0x03002F7C);
            SetupCpu(nds.Arm7, header.Arm7EntryAddress, irqStack: 0x0380FF80, svcStack: 0x0380FFC0, sysStack: 0x0380FD80);
        }

        public enum SecureArea { NotPresent, Decrypted, DecryptedWithBios, EncryptedNoBios, DecryptionFailed }

        /// <summary>What direct boot found in the cartridge's secure area on the last boot.</summary>
        public static SecureArea SecureAreaStatus { get; private set; }

        /// <summary>
        /// The ARM9 binary's first 2 KB (ROM 4000h) must be in the decrypted
        /// form the BIOS leaves behind. Most dumps already are; encrypted dumps
        /// are decrypted with the KEY1 table from the user's ARM7 BIOS.
        /// </summary>
        private static SecureArea PrepareSecureArea(NdsEmulator nds, NdsRom rom, byte[] arm9)
        {
            if (rom.Header.Arm9RomOffset != 0x4000 || arm9.Length < 0x800 || Key1Decryptor.IsSecureAreaEmpty(rom.Data))
                return SecureArea.NotPresent;
            if (Key1Decryptor.IsSecureAreaDecrypted(rom.Data))
                return SecureArea.Decrypted;
            if (!nds.HasRealBios)
                return SecureArea.EncryptedNoBios;

            var key1 = new Key1Decryptor(nds.Bios7, BitConverter.ToUInt32(rom.Data, 0x0C), ReadOnlySpan<byte>.Empty);
            return key1.DecryptSecureArea(arm9) ? SecureArea.DecryptedWithBios : SecureArea.DecryptionFailed;
        }

        private static void CopyBinary(Action<uint, byte> write, ReadOnlySpan<byte> binary, uint address)
        {
            for (int i = 0; i < binary.Length; i++)
                write(address + (uint)i, binary[i]);
        }

        private static void WriteBootInfo(NdsEmulator nds, uint address, uint chipId, ushort headerCrc, ushort secureCrc)
        {
            nds.Arm9Bus.BusWrite32(address + 0, chipId);
            nds.Arm9Bus.BusWrite32(address + 4, chipId);
            nds.Arm9Bus.BusWrite16(address + 8, headerCrc);
            nds.Arm9Bus.BusWrite16(address + 10, secureCrc);
        }

        private static void SetupCpu(ArmCpu cpu, uint entry, uint irqStack, uint svcStack, uint sysStack)
        {
            cpu.SetCpsr((uint)ProcessorMode.System);
            cpu.SetBankedStackPointer(ProcessorMode.Irq, irqStack);
            cpu.SetBankedStackPointer(ProcessorMode.Supervisor, svcStack);
            cpu.R[13] = sysStack;
            cpu.R[12] = entry;
            cpu.R[14] = entry;
            cpu.SetPc(entry);
        }
    }
}
