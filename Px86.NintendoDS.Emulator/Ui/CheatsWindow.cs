using System.Numerics;
using ImGuiNET;
using Px86.NintendoDS.EmulatorCore.Cheats;

namespace Px86.NintendoDS.Emulator.Ui
{
    /// <summary>
    /// Tools → Cheats: the running game's cheat list (tick to enable), an
    /// editor for Action Replay and raw codes, and import from an R4/DeSmuME
    /// usrcheat.dat database. Every change is saved straight away.
    /// </summary>
    public sealed class CheatsWindow
    {
        private static readonly string[] Types = { "Action Replay", "Raw (ADDRESS VALUE)" };

        private readonly UiContext _ui;
        private bool _open;
        private int _selected = -1;
        private string _name = "", _note = "", _code = "";
        private int _type;
        private string? _error;
        private List<Cheat>? _importChoices;
        private bool[] _importPicked = Array.Empty<bool>();
        private string _importTitle = "";

        public CheatsWindow(UiContext ui) => _ui = ui;

        public void Open() => _open = true;

        /// <summary>Put a new cheat in the editor (used by the cheat search).</summary>
        public void Add(Cheat cheat)
        {
            if (_ui.Host.EditCheats(list => list.Cheats.Add(cheat)))
                _ui.Notifications.Add($"Added cheat '{cheat.Name}'");
            _open = true;
        }

        public void Draw()
        {
            if (!_open)
                return;
            var viewport = ImGui.GetMainViewport();
            ImGui.SetNextWindowSize(new Vector2(Math.Min(560, viewport.Size.X - 20), Math.Min(520, viewport.Size.Y - 40)), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowPos(ImGui.GetMainViewport().GetCenter(), ImGuiCond.FirstUseEver, new Vector2(0.5f, 0.5f));
            if (ImGui.Begin("Cheats", ref _open, ImGuiWindowFlags.NoCollapse))
            {
                if (_ui.Host.Status == EmulatorCore.Hosting.HostStatus.Shutdown)
                {
                    ImGui.TextDisabled("Start a game to edit its cheats.");
                }
                else
                {
                    CheatTable();
                    ImGui.Separator();
                    Editor();
                    ImGui.Separator();
                    ImportButtons();
                }
            }
            ImGui.End();
            ImportPopup();
        }

        private void CheatTable()
        {
            var cheats = _ui.Host.Invoke(() => _ui.Host.Cheats.Cheats.Select(c => (c.Name, c.Enabled, c.Type)).ToList());
            if (ImGui.BeginChild("##list", new Vector2(0, 180), true))
            {
                if (cheats.Count == 0)
                    ImGui.TextDisabled("No cheats yet. Add one below or import a usrcheat.dat.");
                for (int i = 0; i < cheats.Count; i++)
                {
                    bool enabled = cheats[i].Enabled;
                    int index = i;
                    if (ImGui.Checkbox($"##on{i}", ref enabled))
                        _ui.Host.EditCheats(list => list.Cheats[index].Enabled = enabled);
                    ImGui.SameLine();
                    string label = $"{cheats[i].Name}{(cheats[i].Type == CheatType.Raw ? "  [raw]" : "")}##cheat{i}";
                    if (ImGui.Selectable(label, _selected == i))
                        Select(i);
                }
            }
            ImGui.EndChild();
        }

        private void Select(int index)
        {
            _selected = index;
            _error = null;
            var cheat = _ui.Host.Invoke(() => index < _ui.Host.Cheats.Cheats.Count ? _ui.Host.Cheats.Cheats[index] : null);
            if (cheat == null)
                return;
            _name = cheat.Name;
            _note = cheat.Note;
            _code = cheat.Code;
            _type = (int)cheat.Type;
        }

        private void Editor()
        {
            ImGui.TextUnformatted(_selected >= 0 ? "Edit cheat" : "New cheat");
            ImGui.InputText("Name", ref _name, 128);
            ImGui.Combo("Type", ref _type, Types, Types.Length);
            ImGui.InputText("Note", ref _note, 256);
            ImGui.InputTextMultiline("##code", ref _code, 64 * 1024, new Vector2(-1, 110));
            ImGui.TextDisabled(_type == 0 ? "One \"XXXXXXXX YYYYYYYY\" pair per line. ; or // start a comment."
                                          : "One \"ADDRESS VALUE\" per line; 2/4/8 value digits = byte/halfword/word.");
            if (_error != null)
                ImGui.TextColored(new Vector4(1, 0.4f, 0.4f, 1), _error);

            var cheat = new Cheat { Name = _name.Trim(), Note = _note, Code = _code, Type = (CheatType)_type, Enabled = true };
            if (ImGui.Button(_selected >= 0 ? "Save changes" : "Add cheat"))
            {
                _error = string.IsNullOrWhiteSpace(cheat.Name) ? "Give the cheat a name." : cheat.Validate();
                if (_error == null)
                {
                    int index = _selected;
                    _ui.Host.EditCheats(list =>
                    {
                        if (index >= 0 && index < list.Cheats.Count)
                        {
                            cheat.Enabled = list.Cheats[index].Enabled;
                            list.Cheats[index] = cheat;
                        }
                        else
                        {
                            list.Cheats.Add(cheat);
                        }
                    });
                    if (index < 0) Clear();
                }
            }
            ImGui.SameLine();
            if (ImGui.Button("New"))
                Clear();
            if (_selected >= 0)
            {
                ImGui.SameLine();
                if (ImGui.Button("Delete"))
                {
                    int index = _selected;
                    _ui.Host.EditCheats(list => { if (index < list.Cheats.Count) list.Cheats.RemoveAt(index); });
                    Clear();
                }
            }
        }

        private void Clear()
        {
            _selected = -1;
            _name = _note = _code = "";
            _type = 0;
            _error = null;
        }

        // ── usrcheat.dat import ───────────────────────────────────────────────

        private void ImportButtons()
        {
            if (ImGui.Button("Import from usrcheat.dat..."))
            {
                string? folder = Path.GetDirectoryName(_ui.Settings.CheatDatabasePath) ?? _ui.Settings.RomFolder;
                _ui.Browser.Open("Choose a usrcheat.dat cheat database", folder, new[] { ".dat" }, LoadDatabase);
            }
            if (_ui.Settings.CheatDatabasePath is { } last && File.Exists(last))
            {
                ImGui.SameLine();
                if (ImGui.Button("Import from " + Path.GetFileName(last)))
                    LoadDatabase(last);
            }
            ImGui.TextDisabled("Imported cheats start switched off.");
        }

        private void LoadDatabase(string path)
        {
            var rom = _ui.Host.Invoke(() => _ui.Host.Emulator?.Rom);
            if (rom == null)
                return;
            try
            {
                var game = UsrCheatDatabase.Find(File.ReadAllBytes(path), rom.Header.GameCode, UsrCheatDatabase.KeyFor(rom.Data));
                _ui.Settings.CheatDatabasePath = path;
                if (game == null || game.Cheats.Count == 0)
                {
                    _ui.Notifications.Add($"No cheats for {rom.Header.GameCode} in {Path.GetFileName(path)}");
                    return;
                }
                _importChoices = game.Cheats.ToList();
                _importPicked = new bool[_importChoices.Count];
                _importTitle = game.Title;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
            {
                _ui.Notifications.Add($"Could not read {Path.GetFileName(path)}: {ex.Message}");
            }
        }

        private void ImportPopup()
        {
            if (_importChoices == null)
                return;
            const string id = "Import cheats##px86import";
            if (!ImGui.IsPopupOpen(id))
                ImGui.OpenPopup(id);
            ImGui.SetNextWindowSize(new Vector2(480, 420), ImGuiCond.Appearing);
            bool open = true;
            if (ImGui.BeginPopupModal(id, ref open))
            {
                ImGui.TextUnformatted(_importTitle);
                if (ImGui.Button("All")) Array.Fill(_importPicked, true);
                ImGui.SameLine();
                if (ImGui.Button("None")) Array.Fill(_importPicked, false);
                if (ImGui.BeginChild("##choices", new Vector2(0, -ImGui.GetFrameHeightWithSpacing() - 4), true))
                {
                    for (int i = 0; i < _importChoices.Count; i++)
                    {
                        ImGui.Checkbox($"{_importChoices[i].Name}##imp{i}", ref _importPicked[i]);
                        if (_importChoices[i].Note.Length > 0 && ImGui.IsItemHovered())
                            ImGui.SetTooltip(_importChoices[i].Note);
                    }
                }
                ImGui.EndChild();
                if (ImGui.Button("Import selected"))
                {
                    var picked = _importChoices.Where((_, i) => _importPicked[i]).ToList();
                    _ui.Host.EditCheats(list => list.Cheats.AddRange(picked));
                    _ui.Notifications.Add($"Imported {picked.Count} cheat(s)");
                    _importChoices = null;
                    ImGui.CloseCurrentPopup();
                }
                ImGui.SameLine();
                if (ImGui.Button("Cancel"))
                {
                    _importChoices = null;
                    ImGui.CloseCurrentPopup();
                }
                ImGui.EndPopup();
            }
            if (!open)
                _importChoices = null;
        }
    }
}
