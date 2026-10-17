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
    ///     --headless            No window: run --frames frames, then exit
    ///     --frames &lt;n&gt;          Frames to run headless (default 600)
    ///     --screenshot &lt;png&gt;    Headless: write both screens as a PNG at the end
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
        public int Frames { get; private set; } = 600;
        public string? ScreenshotPath { get; private set; }
        public bool TraceIo { get; private set; }
        public bool SaveSettings { get; private set; }
        public bool ShowHelp { get; private set; }

        public static CommandLine Parse(string[] args)
        {
            var cl = new CommandLine();
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{arg} needs a value.");

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
            return cl;
        }

        public const string HelpText = """
            Px86.NintendoDS — Nintendo DS emulator

            Usage: Px86.NintendoDS.Emulator <game.nds> [options]

              --rom <path>          ROM to load (or pass it as the first argument)
              --bios9 <path>        ARM9 BIOS dump (4 KB)      optional
              --bios7 <path>        ARM7 BIOS dump (16 KB)     optional
              --firmware <path>     Firmware dump (256 KB)     optional
              --real-bios           Boot through BIOS + firmware instead of direct boot
              --save <path>         Save file (default: <rom>.sav)
              --scale <n>           Window scale (default 2)
              --headless            No window: run --frames frames, then exit
              --frames <n>          Frames to run in headless mode (default 600)
              --screenshot <png>    Headless: write both screens to a PNG at the end
              --trace-io            Log unhandled I/O register accesses
              --save-settings       Remember the BIOS/firmware/scale options

            Controls (default): arrows = D-pad, X = A, Z = B, S = X, A = Y, Q = L, W = R,
            Enter = Start, Backspace = Select, mouse = touch screen, Tab = fast-forward,
            P = pause, F11 = fullscreen, Esc = quit. Gamepads are supported.
            """;
    }
}
