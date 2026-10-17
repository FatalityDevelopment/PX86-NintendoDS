using System.Numerics;
using ImGuiNET;

namespace Px86.NintendoDS.Emulator.Ui
{
    /// <summary>Short messages shown in the bottom-left corner for a few seconds ("Saved state 1"…). Thread-safe.</summary>
    public sealed class Notifications
    {
        private const double Lifetime = 4.0;
        private readonly List<(string Text, DateTime Until)> _items = new();

        public void Add(string text)
        {
            Console.WriteLine(text);
            lock (_items)
            {
                _items.Add((Ascii(text), DateTime.UtcNow.AddSeconds(Lifetime)));
                if (_items.Count > 6)
                    _items.RemoveAt(0);
            }
        }

        /// <summary>ImGui's built-in font is ASCII only.</summary>
        public static string Ascii(string text) =>
            text.Replace("\u2026", "...").Replace("\u2192", ">").Replace("\u2014", "-").Replace("\u2013", "-").Replace("\u00b0", " deg");

        public void Draw()
        {
            (string Text, DateTime Until)[] items;
            lock (_items)
            {
                _items.RemoveAll(i => i.Until < DateTime.UtcNow);
                items = _items.ToArray();
            }
            if (items.Length == 0)
                return;

            var draw = ImGui.GetForegroundDrawList();
            var viewport = ImGui.GetMainViewport();
            float y = viewport.Pos.Y + viewport.Size.Y - 8;
            for (int i = items.Length - 1; i >= 0; i--)
            {
                var size = ImGui.CalcTextSize(items[i].Text);
                y -= size.Y + 8;
                var pos = new Vector2(viewport.Pos.X + 8, y);
                draw.AddRectFilled(pos - new Vector2(4, 2), pos + size + new Vector2(4, 2), 0xC0202020, 3f);
                draw.AddText(pos, 0xFFFFFFFF, items[i].Text);
            }
        }
    }
}
