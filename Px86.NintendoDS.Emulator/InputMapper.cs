using Silk.NET.Input;
using Px86.NintendoDS.EmulatorCore;
using Px86.NintendoDS.EmulatorCore.Hardware;

namespace Px86.NintendoDS.Emulator
{
    /// <summary>
    /// Turns host input into DS input. Written by the window thread, read by
    /// the emulation thread once per frame (<see cref="Apply"/>).
    ///
    /// Keyboard bindings come from <see cref="EmulatorSettings.KeyBindings"/>.
    /// Gamepads use the standard layout: D-pad/left stick = D-pad, south/east/
    /// north/west = B/A/X/Y (Nintendo positions), shoulders = L/R, Start/Back =
    /// Start/Select. The mouse (left button) on the bottom screen is the stylus.
    /// Tab (or the right trigger) holds fast-forward.
    /// </summary>
    public sealed class InputMapper
    {
        private readonly Dictionary<Key, NdsButton> _keyMap = new();
        private readonly object _lock = new();
        private NdsButton _keyboard;
        private NdsButton _gamepad;
        private (int X, int Y)? _touch;
        private bool _fastForwardKey;
        private bool _fastForwardPad;

        public InputMapper(EmulatorSettings settings)
        {
            foreach (var (hostKey, button) in settings.KeyBindings)
            {
                if (Enum.TryParse<Key>(hostKey, ignoreCase: true, out var key) &&
                    Enum.TryParse<NdsButton>(button, ignoreCase: true, out var ndsButton))
                    _keyMap[key] = ndsButton;
                else
                    Console.Error.WriteLine($"[Input] Ignoring binding {hostKey} → {button}.");
            }
        }

        public bool FastForward => _fastForwardKey || _fastForwardPad;

        public void KeyDown(Key key)
        {
            if (key == Key.Tab) { _fastForwardKey = true; return; }
            if (_keyMap.TryGetValue(key, out var button))
                lock (_lock) _keyboard |= button;
        }

        public void KeyUp(Key key)
        {
            if (key == Key.Tab) { _fastForwardKey = false; return; }
            if (_keyMap.TryGetValue(key, out var button))
                lock (_lock) _keyboard &= ~button;
        }

        public void SetTouch((int X, int Y)? point)
        {
            lock (_lock) _touch = point;
        }

        public void PollGamepads(IReadOnlyList<IGamepad> gamepads)
        {
            NdsButton pressed = NdsButton.None;
            bool fast = false;
            foreach (var pad in gamepads)
            {
                if (!pad.IsConnected) continue;
                foreach (var b in pad.Buttons)
                {
                    if (!b.Pressed) continue;
                    pressed |= b.Name switch
                    {
                        ButtonName.A => NdsButton.B,        // south
                        ButtonName.B => NdsButton.A,        // east
                        ButtonName.X => NdsButton.Y,        // west
                        ButtonName.Y => NdsButton.X,        // north
                        ButtonName.LeftBumper => NdsButton.L,
                        ButtonName.RightBumper => NdsButton.R,
                        ButtonName.Start => NdsButton.Start,
                        ButtonName.Back => NdsButton.Select,
                        ButtonName.DPadUp => NdsButton.Up,
                        ButtonName.DPadDown => NdsButton.Down,
                        ButtonName.DPadLeft => NdsButton.Left,
                        ButtonName.DPadRight => NdsButton.Right,
                        _ => NdsButton.None,
                    };
                }
                if (pad.Thumbsticks.Count > 0)
                {
                    var stick = pad.Thumbsticks[0];
                    if (stick.X < -0.5f) pressed |= NdsButton.Left;
                    if (stick.X > 0.5f) pressed |= NdsButton.Right;
                    if (stick.Y < -0.5f) pressed |= NdsButton.Up;
                    if (stick.Y > 0.5f) pressed |= NdsButton.Down;
                }
                if (pad.Triggers.Count > 1 && pad.Triggers[1].Position > 0.5f)
                    fast = true;
            }
            lock (_lock) _gamepad = pressed;
            _fastForwardPad = fast;
        }

        /// <summary>Emulation thread: push the current state into the console.</summary>
        public void Apply(NdsEmulator nds)
        {
            NdsButton buttons;
            (int X, int Y)? touch;
            lock (_lock)
            {
                buttons = _keyboard | _gamepad;
                touch = _touch;
            }

            // Opposite directions at once confuse some games; drop both.
            if ((buttons & (NdsButton.Left | NdsButton.Right)) == (NdsButton.Left | NdsButton.Right))
                buttons &= ~(NdsButton.Left | NdsButton.Right);
            if ((buttons & (NdsButton.Up | NdsButton.Down)) == (NdsButton.Up | NdsButton.Down))
                buttons &= ~(NdsButton.Up | NdsButton.Down);

            nds.Keypad.SetButtons(buttons);
            if (touch is { } p)
            {
                nds.Spi.Touch.SetTouch(p.X, p.Y);
                nds.Keypad.SetPen(true);
            }
            else
            {
                nds.Spi.Touch.ReleaseTouch();
                nds.Keypad.SetPen(false);
            }
        }
    }
}
