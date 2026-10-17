using Px86.NintendoDS.EmulatorCore.Hosting;

namespace Px86.NintendoDS.Emulator.Ui
{
    /// <summary>What the menus and tool windows need from the frontend.</summary>
    public sealed class UiContext
    {
        public required EmulatorHost Host { get; init; }
        public required EmulatorSettings Settings { get; init; }
        public required GameLauncher Launcher { get; init; }
        public required RemoteServices Remote { get; init; }
        public required Notifications Notifications { get; init; }
        public required FileBrowser Browser { get; init; }

        /// <summary>Re-read video settings (layout, filter).</summary>
        public required Action ApplyVideo { get; init; }
        public required Action ApplyAudio { get; init; }
        public required Action ApplyInput { get; init; }
        public required Action<int> ResizeWindow { get; init; }
        public required Action ToggleFullscreen { get; init; }
        public required Action SaveScreenshot { get; init; }
        public required Action CaptureGx { get; init; }
        public required Action Quit { get; init; }

        /// <summary>One line about the microphone (device in use, or why there is none).</summary>
        public required Func<string> MicrophoneStatus { get; init; }

        /// <summary>State slot used by F5/F8.</summary>
        public int StateSlot { get; set; }

        public void SaveState(int slot)
        {
            try
            {
                Notifications.Add(Host.SaveState(slot) ? $"Saved state {slot}" : "No game running");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Notifications.Add($"Save state failed: {ex.Message}");
            }
        }

        public void LoadState(int slot)
        {
            try
            {
                Notifications.Add(Host.LoadState(slot) ? $"Loaded state {slot}" : $"State slot {slot} is empty");
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                Notifications.Add($"Load state failed: {ex.Message}");
            }
        }

        public void OpenRomDialog() =>
            Browser.Open("Open a Nintendo DS ROM", Settings.RomFolder, new[] { ".nds", ".srl" }, path => Launcher.Load(path));
    }
}
