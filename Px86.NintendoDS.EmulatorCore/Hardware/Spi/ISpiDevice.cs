namespace Px86.NintendoDS.EmulatorCore.Hardware.Spi
{
    /// <summary>A device on the ARM7 SPI bus (or the cartridge's AUX SPI bus).</summary>
    public interface ISpiDevice
    {
        /// <summary>Exchange one byte while the device is selected.</summary>
        byte Transfer(byte value);

        /// <summary>Chip-select released: the current command ends.</summary>
        void Deselect();
    }
}
