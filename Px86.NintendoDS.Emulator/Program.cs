using Px86.NintendoDS.EmulatorCore;
using Px86.NintendoDS.EmulatorCore.Video;

namespace Px86.NintendoDS.Emulator
{
    /// <summary>
    /// Px86.NintendoDS.Emulator — desktop frontend (Silk.NET: SDL window,
    /// OpenGL presentation, keyboard/gamepad/mouse input, OpenAL audio).
    ///
    /// See <see cref="CommandLine.HelpText"/> for options. Headless mode runs
    /// a fixed number of frames without a window and can write a screenshot —
    /// used for automated checks and debugging on machines without a display.
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

            if (cl.ShowHelp || cl.RomPath == null)
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

            GameSession session;
            try
            {
                session = GameSession.Start(new GameLoadOptions
                {
                    RomPath = cl.RomPath,
                    SavePath = cl.SavePath ?? SavePathFor(cl.RomPath, settings),
                    Bios9Path = settings.Bios9Path,
                    Bios7Path = settings.Bios7Path,
                    FirmwarePath = settings.FirmwarePath,
                    UseRealBios = settings.UseRealBios,
                });
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"Could not start {cl.RomPath}: {ex.Message}");
                return 1;
            }

            using (session)
            {
                var header = session.Rom.Header;
                Console.WriteLine($"Px86.NintendoDS — {header.Title} [{header.GameCode}]");
                foreach (var message in session.Messages)
                    Console.WriteLine("  " + message);

                if (cl.TraceIo)
                {
                    session.Emulator.Arm9Bus.UnhandledIo += (a, w, write) => Console.WriteLine($"[IO9] {(write ? "W" : "R")}{w} {a:X8}");
                    session.Emulator.Arm7Bus.UnhandledIo += (a, w, write) => Console.WriteLine($"[IO7] {(write ? "W" : "R")}{w} {a:X8}");
                }

                if (cl.Headless)
                    return RunHeadless(session, cl);

                using var window = new EmulatorWindow(session, settings);
                window.Run();
            }
            return 0;
        }

        private static string? SavePathFor(string romPath, EmulatorSettings settings)
        {
            if (string.IsNullOrEmpty(settings.SaveDirectory))
                return null;
            Directory.CreateDirectory(settings.SaveDirectory);
            return Path.Combine(settings.SaveDirectory, Path.GetFileNameWithoutExtension(romPath) + ".sav");
        }

        private static int RunHeadless(GameSession session, CommandLine cl)
        {
            var nds = session.Emulator;
            var timer = System.Diagnostics.Stopwatch.StartNew();
            for (int frame = 0; frame < cl.Frames; frame++)
                nds.RunFrame();
            timer.Stop();

            double fps = cl.Frames / Math.Max(timer.Elapsed.TotalSeconds, 1e-6);
            Console.WriteLine($"Ran {cl.Frames} frames in {timer.Elapsed.TotalSeconds:F2} s ({fps:F1} fps, {fps / 59.8261 * 100:F0}% speed).");
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
