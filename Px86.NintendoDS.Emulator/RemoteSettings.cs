using Px86.NintendoDS.EmulatorCore.Hosting;

namespace Px86.NintendoDS.Emulator
{
    /// <summary>
    /// Backs the PINE GetSettings/SetSettings opcodes: reads the running
    /// settings, applies changes live where the emulator allows it and saves
    /// them to settings.json.
    /// </summary>
    /// <remarks>
    /// The running settings include this run's command-line overrides, so the
    /// change is merged into a fresh copy from disk before saving: the file
    /// only ever gains what the client changed, never the overrides (those are
    /// saved by <c>--save-settings</c> only).
    /// </remarks>
    public sealed class RemoteSettings
    {
        private readonly EmulatorSettings _live;
        private readonly EmulatorHost _host;
        private readonly WirelessServices? _wireless;
        private readonly string? _path;
        private readonly object _gate = new();

        public RemoteSettings(EmulatorSettings live, EmulatorHost host, WirelessServices? wireless, string? path = null)
        {
            _live = live;
            _host = host;
            _wireless = wireless;
            _path = path;
        }

        public string Get()
        {
            lock (_gate)
                return SettingsJson.Export(_live);
        }

        /// <summary>Apply a partial settings JSON and return the report of what changed and when it takes effect.</summary>
        /// <exception cref="InvalidDataException">Invalid JSON or value.</exception>
        public string Set(string json)
        {
            lock (_gate)
            {
                var changed = SettingsJson.Merge(_live, json);
                if (changed.Count == 0)
                    return SettingsJson.Report(changed);

                var disk = EmulatorSettings.Load(_path);
                SettingsJson.Merge(disk, json);
                disk.Save(_path);

                if (changed.Contains(nameof(EmulatorSettings.CpuMode)))
                    _host.Invoke(() =>
                    {
                        if (_host.Emulator is { } nds)
                            nds.CpuMode = _live.CpuMode;
                    });
                if (changed.Contains(nameof(EmulatorSettings.TurboPercent)))
                    _host.TurboSpeed = _live.TurboPercent / 100.0;
                if (_wireless != null && changed.Any(n => n.StartsWith("Wireless", StringComparison.Ordinal) || n == nameof(EmulatorSettings.OnlineDns)))
                    _wireless.Apply(_live.WirelessMode, _live.WirelessPort, _live.WirelessLan, _live.OnlineDns);

                return SettingsJson.Report(changed);
            }
        }
    }
}
