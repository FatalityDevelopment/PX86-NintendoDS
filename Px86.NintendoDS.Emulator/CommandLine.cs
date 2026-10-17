namespace Px86.NintendoDS.Emulator
{
    /// <summary>
    /// Command-line options.
    ///
    ///   Px86.NintendoDS.Emulator &lt;game.nds&gt; [options]
    ///     --rom &lt;path&gt;          ROM to load (or pass it as the first argument)
    ///     --bios9 &lt;path&gt;        ARM9 BIOS dump (4 KB)
    ///     --bios7 &lt;path&gt;        ARM7 BIOS dump (16 KB)
    ///     --firmware &lt;path&gt;     Firmware dump (256 KB)
    ///     --real-bios           Boot through BIOS + firmware instead of direct boot
    ///     --save &lt;path&gt;         Save file (default: &lt;rom&gt;.sav)
    ///     --scale &lt;n&gt;           Window scale
    ///     --headless            No window. With --frames/--screenshot: run, then exit.
    ///                           Without: run as a server (PINE + GS stream) until stopped
    ///     --frames &lt;n&gt;          Frames to run headless (default 600)
    ///     --screenshot &lt;png&gt;    Headless: write both screens as a PNG at the end
    ///     --pine [port]         Open the PINE socket (default port 28013)
    ///     --gs [port]           Open the GS frame-stream socket (default port 28014)
    ///     --no-pine, --no-gs    Keep a socket closed in headless server mode
    ///     --gs-codec &lt;c&gt;        jpeg (default), png or raw
    ///     --gs-quality &lt;n&gt;      JPEG quality 1–100 (default 85)
    ///     --paused              Start paused (set breakpoints first, then resume over PINE)
    ///     --trace-io            Log unhandled I/O register accesses
    ///     --save-settings       Store the given BIOS/firmware/scale options as defaults
    /// </summary>
    public sealed class CommandLine
    {
        public string? RomPath { get; private set; }
        public string? Bios9Path { get; private set; }
        public string? Bios7Path { get; private set; }
        public string? FirmwarePath { get; private set; }
        public string? SavePath { get; private set; }
        public bool RealBios { get; private set; }
        public int? Scale { get; private set; }
        public bool Headless { get; private set; }
        public int? Frames { get; private set; }
        public string? ScreenshotPath { get; private set; }
        public bool TraceIo { get; private set; }
        public bool? Pine { get; private set; }
        public int? PinePort { get; private set; }
        public bool? Gs { get; private set; }
        public int? GsPort { get; private set; }
        public string? GsCodec { get; private set; }
        public int? GsQuality { get; private set; }
        public bool Paused { get; private set; }
        public string? ReplayGxPath { get; private set; }

        /// <summary>Headless without a fixed frame count: keep running and serve PINE/GS.</summary>
        public bool ServerMode => Headless && Frames == null && ScreenshotPath == null;
        public bool SaveSettings { get; private set; }
        public bool ShowHelp { get; private set; }

        public static CommandLine Parse(string[] args)
        {
            var cl = new CommandLine();
            
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{arg} needs a value.");
                int? OptionalPort()
                {
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out int port))
                    {
                        i++;
                        return port is >= 0 and <= 65535 ? port : throw new ArgumentException($"{arg}: bad port {port}.");
                    }
                    return null;
                }

                switch (arg)
                {
                    case "--rom": cl.RomPath = Next(); break;
                    case "--bios9": cl.Bios9Path = Next(); break;
                    case "--bios7": cl.Bios7Path = Next(); break;
                    case "--firmware": cl.FirmwarePath = Next(); break;
                    case "--save": cl.SavePath = Next(); break;
                    case "--real-bios": cl.RealBios = true; break;
                    case "--scale": cl.Scale = int.Parse(Next()); break;
                    case "--headless": cl.Headless = true; break;
                    case "--frames": cl.Frames = int.Parse(Next()); break;
                    case "--screenshot": cl.ScreenshotPath = Next(); break;
                    case "--trace-io": cl.TraceIo = true; break;
                    case "--pine": cl.Pine = true; cl.PinePort = OptionalPort() ?? cl.PinePort; break;
                    case "--gs": cl.Gs = true; cl.GsPort = OptionalPort() ?? cl.GsPort; break;
                    case "--no-pine": cl.Pine = false; break;
                    case "--no-gs": cl.Gs = false; break;
                    case "--gs-codec": cl.GsCodec = Next(); break;
                    case "--gs-quality": cl.GsQuality = int.Parse(Next()); break;
                    case "--paused": cl.Paused = true; break;
                    case "--replay-gx": cl.ReplayGxPath = Next(); break;
                    case "--save-settings": cl.SaveSettings = true; break;
                    case "-h":
                    case "--help": cl.ShowHelp = true; break;
                    default:
                        if (arg.StartsWith('-'))
                            throw new ArgumentException($"Unknown option {arg}.");
                        cl.RomPath ??= arg;
                        break;
                }
            }

            var resources = @"C:\Users\tholu\Desktop\Px86-NintendoDS\resources";
            cl.Bios9Path = cl.Bios9Path ?? @$"{resources}\bios\bios9.bin";
            cl.Bios7Path = cl.Bios7Path ?? @$"{resources}\bios\bios7.bin";
            cl.FirmwarePath = cl.FirmwarePath ?? @$"{resources}\bios\firmware.bin";
            cl.RomPath = cl.RomPath ?? $@"{resources}\roms\Pokemon - Platinum Version (USA) (Rev 1).nds";

            return cl;
        }

        public const string HelpText = """
            Px86.NintendoDS — Nintendo DS emulator

            Usage: Px86.NintendoDS.Emulator [game.nds] [options]

              --rom <path>          ROM to load (or pass it as the first argument)
              --bios9 <path>        ARM9 BIOS dump (4 KB)      optional
              --bios7 <path>        ARM7 BIOS dump (16 KB)     optional
              --firmware <path>     Firmware dump (256 KB)     optional
              --real-bios           Boot through BIOS + firmware instead of direct boot
              --save <path>         Save file (default: <rom>.sav)
              --scale <n>           Window scale (default 2)
              --headless            No window. With --frames/--screenshot: run, then exit.
                                    Without them: run as a server for CodeDesigner and
                                    other tools (PINE + GS stream) until Ctrl+C
              --frames <n>          Frames to run in headless mode (default 600)
              --screenshot <png>    Headless: write both screens to a PNG at the end
              --pine [port]         Open the PINE socket (default 28013, 127.0.0.1 only)
              --gs [port]           Open the GS frame-stream socket (default 28014)
              --no-pine, --no-gs    Keep a socket closed in headless server mode
              --gs-codec <c>        GS stream format: jpeg (default), png or raw
              --gs-quality <n>      GS stream JPEG quality 1-100 (default 85)
              --paused              Start paused (resume over PINE or with P)
              --replay-gx <file>    Re-render a GX capture (F9) to --screenshot (or <file>.png)
                                    and write a polygon/command dump to <file>.txt
              --trace-io            Log unhandled I/O register accesses
              --save-settings       Remember the BIOS/firmware/scale options

            Controls (default): arrows = D-pad, X = A, Z = B, S = X, A = Y, Q = L, W = R,
            Enter = Start, Backspace = Select, mouse = touch screen, Tab = fast-forward,
            P = pause, F9 = GX capture (3D bug report), F5 = save state, F8 = load state, F6/F7 = state slot,
            F11 = fullscreen, F12 = screenshot, Esc = quit. Gamepads are supported.
            """;
    }
}
