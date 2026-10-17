using System.Numerics;
using ImGuiNET;

namespace Px86.NintendoDS.Emulator.Ui
{
    /// <summary>
    /// A small modal file picker (ImGui has no native dialog): folders and
    /// drives on the left of the list, files filtered by extension, a path box,
    /// double-click or OK to choose.
    /// </summary>
    public sealed class FileBrowser
    {
        private const string PopupId = "Choose a file##px86browser";

        private string _folder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        private string _fileName = "";
        private string _title = "";
        private string[] _extensions = Array.Empty<string>();
        private Action<string>? _onChosen;
        private bool _open;
        private bool _saveMode;
        private string? _error;

        public bool IsOpen => _open;

        /// <summary>Show the picker. <paramref name="extensions"/> like ".nds" (empty = any file).</summary>
        public void Open(string title, string? startFolder, string[] extensions, Action<string> onChosen, bool save = false, string? defaultName = null)
        {
            _title = title;
            _extensions = extensions;
            _onChosen = onChosen;
            _saveMode = save;
            _fileName = defaultName ?? "";
            _error = null;
            if (!string.IsNullOrEmpty(startFolder) && Directory.Exists(startFolder))
                _folder = startFolder;
            _open = true;
            ImGui.OpenPopup(PopupId);
        }

        public void Draw()
        {
            if (!_open)
                return;
            if (!ImGui.IsPopupOpen(PopupId))
                ImGui.OpenPopup(PopupId);

            var viewport = ImGui.GetMainViewport();
            ImGui.SetNextWindowSize(new Vector2(Math.Min(560, viewport.Size.X - 20), Math.Min(420, viewport.Size.Y - 40)), ImGuiCond.Appearing);
            ImGui.SetNextWindowPos(viewport.GetCenter(), ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
            bool open = true;
            if (!ImGui.BeginPopupModal(PopupId, ref open))
            {
                _open = open;
                return;
            }

            ImGui.TextUnformatted(_title);
            if (ImGui.Button("Up") && Directory.GetParent(_folder) is { } parent)
                _folder = parent.FullName;
            ImGui.SameLine();
            string folder = _folder;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputText("##folder", ref folder, 1024, ImGuiInputTextFlags.EnterReturnsTrue) && Directory.Exists(folder))
                _folder = folder;

            float footer = ImGui.GetFrameHeightWithSpacing() * 2 + 8;
            if (ImGui.BeginChild("##entries", new Vector2(0, -footer), true))
            {
                if (OperatingSystem.IsWindows())
                {
                    foreach (var drive in DriveInfo.GetDrives())
                    {
                        if (ImGui.Selectable($"[{drive.Name}]"))
                            _folder = drive.RootDirectory.FullName;
                    }
                    ImGui.Separator();
                }
                try
                {
                    foreach (var dir in Directory.EnumerateDirectories(_folder).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                    {
                        string name = Path.GetFileName(dir);
                        if (name.StartsWith('.')) continue;
                        if (ImGui.Selectable($"[{name}]", false, ImGuiSelectableFlags.AllowDoubleClick) && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                            _folder = dir;
                    }
                    foreach (var file in Directory.EnumerateFiles(_folder).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                    {
                        if (_extensions.Length > 0 && !_extensions.Any(e => file.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
                            continue;
                        string name = Path.GetFileName(file);
                        if (ImGui.Selectable(name, name == _fileName, ImGuiSelectableFlags.AllowDoubleClick))
                        {
                            _fileName = name;
                            if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                                Choose();
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    ImGui.TextDisabled(ex.Message);
                }
            }
            ImGui.EndChild();

            ImGui.SetNextItemWidth(-160);
            ImGui.InputText("##name", ref _fileName, 512);
            ImGui.SameLine();
            if (ImGui.Button(_saveMode ? "Save" : "Open", new Vector2(70, 0)))
                Choose();
            ImGui.SameLine();
            if (ImGui.Button("Cancel", new Vector2(70, 0)))
                Close();
            if (_error != null)
                ImGui.TextColored(new Vector4(1, 0.4f, 0.4f, 1), _error);
            ImGui.EndPopup();
        }

        private void Choose()
        {
            if (string.IsNullOrWhiteSpace(_fileName))
                return;
            string path = Path.IsPathRooted(_fileName) ? _fileName : Path.Combine(_folder, _fileName);
            if (!_saveMode && !File.Exists(path))
            {
                _error = "That file doesn't exist.";
                return;
            }
            var callback = _onChosen;
            Close();
            callback?.Invoke(path);
        }

        private void Close()
        {
            _open = false;
            ImGui.CloseCurrentPopup();
        }
    }
}
