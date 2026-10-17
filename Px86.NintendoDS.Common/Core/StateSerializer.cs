namespace Px86.NintendoDS.Common.Core
{
    /// <summary>
    /// Symmetric binary serializer for save states. Each component implements
    /// one <c>Serialize(StateSerializer s)</c> method that calls <c>s.Sync(…)</c>
    /// on its fields in a fixed order; the same code both writes and reads,
    /// so the two can never drift apart.
    ///
    /// <see cref="Section"/> writes a tag that is verified on load, so a
    /// mismatch reports which component is out of sync instead of silently
    /// loading garbage.
    /// </summary>
    public sealed class StateSerializer
    {
        private readonly BinaryWriter? _writer;
        private readonly BinaryReader? _reader;

        private StateSerializer(BinaryWriter? writer, BinaryReader? reader)
        {
            _writer = writer;
            _reader = reader;
        }

        public static StateSerializer ForWriting(Stream stream) => new(new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true), null);

        public static StateSerializer ForReading(Stream stream) => new(null, new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true));

        public bool IsReading => _reader != null;

        public void Section(string name)
        {
            if (_reader == null)
            {
                _writer!.Write(name);
                return;
            }
            string found = _reader.ReadString();
            if (found != name)
                throw new InvalidDataException($"Save state is out of sync: expected section '{name}', found '{found}'.");
        }

        public void Sync(ref bool value) { if (_reader != null) value = _reader.ReadBoolean(); else _writer!.Write(value); }
        public void Sync(ref byte value) { if (_reader != null) value = _reader.ReadByte(); else _writer!.Write(value); }
        public void Sync(ref sbyte value) { if (_reader != null) value = _reader.ReadSByte(); else _writer!.Write(value); }
        public void Sync(ref short value) { if (_reader != null) value = _reader.ReadInt16(); else _writer!.Write(value); }
        public void Sync(ref ushort value) { if (_reader != null) value = _reader.ReadUInt16(); else _writer!.Write(value); }
        public void Sync(ref int value) { if (_reader != null) value = _reader.ReadInt32(); else _writer!.Write(value); }
        public void Sync(ref uint value) { if (_reader != null) value = _reader.ReadUInt32(); else _writer!.Write(value); }
        public void Sync(ref long value) { if (_reader != null) value = _reader.ReadInt64(); else _writer!.Write(value); }
        public void Sync(ref ulong value) { if (_reader != null) value = _reader.ReadUInt64(); else _writer!.Write(value); }
        public void Sync(ref string value) { if (_reader != null) value = _reader.ReadString(); else _writer!.Write(value); }

        public void SyncEnum<T>(ref T value) where T : struct, Enum
        {
            int raw = Convert.ToInt32(value);
            Sync(ref raw);
            if (_reader != null)
                value = (T)Enum.ToObject(typeof(T), raw);
        }

        /// <summary>Property helper: <c>Prop(() => X, v => X = v)</c> for values with private setters.</summary>
        public void Sync(Func<uint> get, Action<uint> set)
        {
            uint value = get();
            Sync(ref value);
            if (_reader != null) set(value);
        }

        // ── Arrays (fixed size, synced in place) ──────────────────────────────

        private void SyncLength(int length)
        {
            int stored = length;
            Sync(ref stored);
            if (stored != length)
                throw new InvalidDataException($"Save state array length mismatch ({stored} vs {length}).");
        }

        public void Sync(byte[] array)
        {
            SyncLength(array.Length);
            if (_reader != null)
            {
                int read = _reader.Read(array, 0, array.Length);
                if (read != array.Length) throw new EndOfStreamException();
            }
            else
            {
                _writer!.Write(array);
            }
        }

        public void Sync(bool[] array) { SyncLength(array.Length); for (int i = 0; i < array.Length; i++) Sync(ref array[i]); }
        public void Sync(short[] array) { SyncLength(array.Length); for (int i = 0; i < array.Length; i++) Sync(ref array[i]); }
        public void Sync(ushort[] array) { SyncLength(array.Length); for (int i = 0; i < array.Length; i++) Sync(ref array[i]); }
        public void Sync(int[] array) { SyncLength(array.Length); for (int i = 0; i < array.Length; i++) Sync(ref array[i]); }
        public void Sync(uint[] array) { SyncLength(array.Length); for (int i = 0; i < array.Length; i++) Sync(ref array[i]); }
        public void Sync(long[] array) { SyncLength(array.Length); for (int i = 0; i < array.Length; i++) Sync(ref array[i]); }

        /// <summary>A byte array whose length may differ between states (e.g. save memory).</summary>
        public byte[] SyncVariable(byte[] array)
        {
            int length = array.Length;
            Sync(ref length);
            if (_reader != null)
            {
                array = _reader.ReadBytes(length);
                if (array.Length != length) throw new EndOfStreamException();
            }
            else
            {
                _writer!.Write(array);
            }
            return array;
        }

        /// <summary>A queue of 32-bit values (FIFOs).</summary>
        public void Sync(Queue<uint> queue)
        {
            int count = queue.Count;
            Sync(ref count);
            if (_reader != null)
            {
                queue.Clear();
                for (int i = 0; i < count; i++) queue.Enqueue(_reader.ReadUInt32());
            }
            else
            {
                foreach (uint v in queue) _writer!.Write(v);
            }
        }

        public void Sync(Queue<byte> queue)
        {
            int count = queue.Count;
            Sync(ref count);
            if (_reader != null)
            {
                queue.Clear();
                for (int i = 0; i < count; i++) queue.Enqueue(_reader.ReadByte());
            }
            else
            {
                foreach (byte v in queue) _writer!.Write(v);
            }
        }

        /// <summary>A list of objects with their own serialize method, created with <paramref name="create"/> on load.</summary>
        public void Sync<T>(List<T> list, Func<T> create, Action<T, StateSerializer> serialize)
        {
            int count = list.Count;
            Sync(ref count);
            if (_reader != null)
            {
                list.Clear();
                for (int i = 0; i < count; i++)
                {
                    var item = create();
                    serialize(item, this);
                    list.Add(item);
                }
            }
            else
            {
                foreach (var item in list) serialize(item, this);
            }
        }
    }
}
