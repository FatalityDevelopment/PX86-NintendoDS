using Px86.NintendoDS.Common.Cpu;
using Px86.NintendoDS.Common.Interfaces;

namespace Px86.NintendoDS.EmulatorCore.Debugging
{
    /// <summary>
    /// Sits between a core and its bus while that core has watchpoints and
    /// reports every data access to the <see cref="DebugMonitor"/>. Opcode
    /// fetches and DMA (which talks to the bus directly) are not watched.
    /// </summary>
    public sealed class WatchingMemoryBus : IArmMemoryBus
    {
        private readonly IArmMemoryBus _inner;
        private readonly DebugMonitor _monitor;
        private readonly DebugCpu _cpu;
        private readonly ArmCpu _core;

        public WatchingMemoryBus(IArmMemoryBus inner, DebugMonitor monitor, DebugCpu cpu, ArmCpu core)
        {
            _inner = inner;
            _monitor = monitor;
            _cpu = cpu;
            _core = core;
        }

        public IArmMemoryBus Inner => _inner;

        public byte Read8(uint address)
        {
            _monitor.OnAccess(_cpu, _core.Pc, address, 1, write: false);
            return _inner.Read8(address);
        }

        public ushort Read16(uint address)
        {
            _monitor.OnAccess(_cpu, _core.Pc, address & ~1u, 2, write: false);
            return _inner.Read16(address);
        }

        public uint Read32(uint address)
        {
            _monitor.OnAccess(_cpu, _core.Pc, address & ~3u, 4, write: false);
            return _inner.Read32(address);
        }

        public void Write8(uint address, byte value)
        {
            _monitor.OnAccess(_cpu, _core.Pc, address, 1, write: true);
            _inner.Write8(address, value);
        }

        public void Write16(uint address, ushort value)
        {
            _monitor.OnAccess(_cpu, _core.Pc, address & ~1u, 2, write: true);
            _inner.Write16(address, value);
        }

        public void Write32(uint address, uint value)
        {
            _monitor.OnAccess(_cpu, _core.Pc, address & ~3u, 4, write: true);
            _inner.Write32(address, value);
        }

        public uint Fetch32(uint address) => _inner.Fetch32(address);

        public ushort Fetch16(uint address) => _inner.Fetch16(address);
    }
}
