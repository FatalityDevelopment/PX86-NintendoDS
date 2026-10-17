using Px86.NintendoDS.EmulatorCore;
using Px86.NintendoDS.EmulatorCore.Hosting;
using Px86.NintendoDS.EmulatorCore.Remote;
using Px86.NintendoDS.EmulatorCore.Video;

namespace Px86.NintendoDS.Emulator
{
    /// <summary>
    /// Px86.NintendoDS.Emulator — desktop frontend (Silk.NET: SDL window,
    /// OpenGL presentation, keyboard/gamepad/mouse input, OpenAL audio).
    ///
    /// Three ways to run (see <see cref="CommandLine.HelpText"/>):
    ///   window          the normal player; PINE/GS sockets are opt-in
    ///   headless batch  --headless --frames N [--screenshot out.png]: run, report, exit
    ///   headless server --headless alone: no window, the console runs on an
    ///                   <see cref="EmulatorHost"/> and is driven over the PINE
    ///                   socket (127.0.0.1:28013) with frames on the GS stream
    ///                   (127.0.0.1:28014). This is how CodeDesigner embeds it.
    ///                   The ROM is optional: a client can send LoadRom.
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            CommandLine cl;
            try
            {
                cl = CommandLine.Parse(args);
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException)
            {
                Console.Error.WriteLine(ex.Message);
                Console.Error.WriteLine(CommandLine.HelpText);
                return 2;
            }

            if (cl.ReplayGxPath != null)
                return ReplayGx(cl.ReplayGxPath, cl.ScreenshotPath);

            if (cl.ShowHelp || (cl.RomPath == null && cl.Headless && !cl.ServerMode))
            {
                Console.WriteLine(CommandLine.HelpText);
                return cl.ShowHelp ? 0 : 2;
            }

            var settings = EmulatorSettings.Load();
            if (cl.Bios9Path != null) settings.Bios9Path = cl.Bios9Path;
            if (cl.Bios7Path != null) settings.Bios7Path = cl.Bios7Path;
            if (cl.FirmwarePath != null) settings.FirmwarePath = cl.FirmwarePath;
            if (cl.RealBios) settings.UseRealBios = true;
            if (cl.Scale is int scale) settings.Scale = Math.Clamp(scale, 1, 8);
            if (cl.SaveSettings) settings.Save();

            using var host = new EmulatorHost();
            var launcher = new GameLauncher(host, settings);
            launcher.Message += m => Console.WriteLine("  " + m);

            GameSession? session = null;
            if (cl.RomPath != null)
            {
                session = launcher.Start(launcher.OptionsFor(cl.RomPath, cl.SavePath));
                if (session == null)
                    return 1;
                settings.AddRecentRom(cl.RomPath);
            }

            if (cl.Headless && !cl.ServerMode)
            {
                using (session)
                    return RunHeadless(session!, cl);
            }

            if (cl.TraceIo)
                host.GameStarted += AttachIoTrace;
            if (session != null)
                host.Attach(session);
            host.BreakHit += b => Console.WriteLine($"[BREAK] {b.Cpu} {b.Reason} at {b.Pc:X8}" +
                                                     (b.Reason == EmulatorCore.Debugging.BreakReason.Watchpoint ? $" ({(b.IsWrite ? "write" : "read")} {b.Address:X8})" : ""));

            using var remote = new RemoteServices(host, launcher);
            using var shutdown = new ManualResetEventSlim();
            remote.ShutdownRequested += shutdown.Set;
            if (cl.ServerMode)
                remote.Log += Console.WriteLine;

            bool gsOn = cl.Gs ?? (cl.ServerMode || settings.GsEnabled);
            bool pineOn = cl.Pine ?? (cl.ServerMode || settings.PineEnabled);
            try
            {
                if (gsOn && !remote.StartGs(cl.GsPort ?? settings.GsPort, RemoteServices.ParseCodec(cl.GsCodec ?? settings.GsCodec), cl.GsQuality ?? settings.GsQuality) && cl.ServerMode)
                    return 1;
                if (pineOn && !remote.StartPine(cl.PinePort ?? settings.PinePort) && cl.ServerMode)
                    return 1;
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 2;
            }

            host.Start(paused: cl.Paused);
            if (cl.ServerMode)
            {
                Console.WriteLine(session == null
                    ? "Headless server: no game loaded, waiting for a PINE LoadRom."
                    : $"Headless server running{(cl.Paused ? " (paused)" : "")}. Ctrl+C to stop.");
                Console.CancelKeyPress += (_, e) =>
                {
                    e.Cancel = true;
                    shutdown.Set();
                };
                shutdown.Wait();
                Console.WriteLine("Stopping.");
            }
            else
            {
                using var window = new EmulatorWindow(host, settings, launcher, remote);
                remote.ShutdownRequested += window.RequestClose;
                window.Run();
            }
            host.Stop();
            return 0;
        }

        private static readonly HashSet<NdsEmulator> Traced = new();

        private static void AttachIoTrace(GameSession session)
        {
            var nds = session.Emulator;
            if (!Traced.Add(nds))
                return; // GameStarted is raised again on reset
            nds.Arm9Bus.UnhandledIo += (a, w, write) => Console.WriteLine($"[IO9] {(write ? "W" : "R")}{w} {a:X8}");
            nds.Arm7Bus.UnhandledIo += (a, w, write) => Console.WriteLine($"[IO7] {(write ? "W" : "R")}{w} {a:X8}");
        }

        private static int ReplayGx(string path, string? screenshot)
        {
            try
            {
                var result = EmulatorCore.Video.Gpu3D.GxCapture.Replay(File.ReadAllBytes(path));
                screenshot ??= Path.ChangeExtension(path, ".png");
                FileFormats.Images.PngWriter.Write(screenshot, 256, 192, result.Pixels);
                string dump = Path.ChangeExtension(path, ".txt");
                File.WriteAllText(dump, result.Describe());
                Console.WriteLine($"{result.GameCode}: {result.Polygons.Count} polygons from {result.Commands.Count} command words.");
                Console.WriteLine($"Image: {screenshot}\nDump:  {dump}");
                return 0;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"Could not replay {path}: {ex.Message}");
                return 1;
            }
        }

        private static int RunHeadless(GameSession session, CommandLine cl)
        {
            var nds = session.Emulator;
            int frames = cl.Frames ?? 600;
            var timer = System.Diagnostics.Stopwatch.StartNew();
            for (int frame = 0; frame < frames; frame++)
                nds.RunFrame();
            timer.Stop();

            double fps = frames / Math.Max(timer.Elapsed.TotalSeconds, 1e-6);
            Console.WriteLine($"Ran {frames} frames in {timer.Elapsed.TotalSeconds:F2} s ({fps:F1} fps, {fps / EmulatorHost.FrameRate * 100:F0}% speed).");
            Console.WriteLine($"ARM9: {nds.Arm9.Pc:X8} ({(nds.Arm9.IsThumb ? "Thumb" : "ARM")})  ARM7: {nds.Arm7.Pc:X8}");

            if (cl.ScreenshotPath != null)
            {
                ScreenCapture.SavePng(nds, cl.ScreenshotPath);
                Console.WriteLine($"Screenshot: {cl.ScreenshotPath}");
            }
            return 0;
        }
    }
}
