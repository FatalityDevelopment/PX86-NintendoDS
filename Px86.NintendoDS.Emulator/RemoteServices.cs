using System.Net.Sockets;
using Px86.NintendoDS.EmulatorCore.Hosting;
using Px86.NintendoDS.EmulatorCore.Remote;

namespace Px86.NintendoDS.Emulator
{
    /// <summary>
    /// Owns the PINE and GS stream sockets so they can be opened and closed at
    /// run time (command line, Tools menu, settings).
    /// </summary>
    public sealed class RemoteServices : IDisposable
    {
        private readonly EmulatorHost _host;
        private readonly GameLauncher _launcher;

        public RemoteServices(EmulatorHost host, GameLauncher launcher)
        {
            _host = host;
            _launcher = launcher;
            Wireless = new WirelessServices(host);
            Wireless.Log += m => Log?.Invoke(m);
        }

        /// <summary>Local wireless / online access for the emulated Wifi hardware.</summary>
        public WirelessServices Wireless { get; }

        /// <summary>Answers the PINE settings opcodes. Null: they fail.</summary>
        public RemoteSettings? Settings { get; set; }

        public PineSocket? Pine { get; private set; }
        public GsStreamSocket? Gs { get; private set; }

        /// <summary>A PINE client asked the emulator to quit.</summary>
        public event Action? ShutdownRequested;

        public event Action<string>? Log;

        public bool StartGs(int port, GsStreamCodec codec, int quality)
        {
            StopGs();
            try
            {
                var gs = new GsStreamSocket(_host, port) { Codec = codec, Quality = quality };
                gs.Log += m => Log?.Invoke(m);
                gs.Start();
                Gs = gs;
                if (Pine != null) Pine.GsStream = gs;
                Log?.Invoke($"GS stream listening on 127.0.0.1:{gs.Port} ({gs.Codec}).");
                return true;
            }
            catch (SocketException ex)
            {
                Log?.Invoke($"Could not open the GS stream on port {port}: {ex.Message}");
                return false;
            }
        }

        public bool StartPine(int port)
        {
            StopPine();
            try
            {
                var pine = new PineSocket(_host, port) { GsStream = Gs, LoadOptions = path => _launcher.OptionsFor(path) };
                if (Settings is { } settings)
                {
                    pine.GetSettings = settings.Get;
                    pine.SetSettings = settings.Set;
                }
                pine.Log += m => Log?.Invoke(m);
                pine.ShutdownRequested += () => ShutdownRequested?.Invoke();
                pine.Start();
                Pine = pine;
                Log?.Invoke($"PINE listening on 127.0.0.1:{pine.Port}.");
                return true;
            }
            catch (SocketException ex)
            {
                Log?.Invoke($"Could not open PINE on port {port}: {ex.Message}");
                return false;
            }
        }

        public void StopPine()
        {
            Pine?.Dispose();
            Pine = null;
        }

        public void StopGs()
        {
            Gs?.Dispose();
            Gs = null;
            if (Pine != null) Pine.GsStream = null;
        }

        public static GsStreamCodec ParseCodec(string name) => name.ToLowerInvariant() switch
        {
            "jpeg" or "jpg" or "mjpeg" => GsStreamCodec.Mjpeg,
            "png" => GsStreamCodec.Png,
            "raw" or "rgba" => GsStreamCodec.RawRgba,
            _ => throw new ArgumentException($"Unknown GS codec '{name}' (use jpeg, png or raw)."),
        };

        public void Dispose()
        {
            StopPine();
            StopGs();
            Wireless.Dispose();
        }
    }
}
