using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace EditorFontFallback.Editor
{
    public class FontFile
    {
        public string Family;
        public string Style;
        public bool IsColor;  // Has colour glyph tables
        public bool IsSymbol;  // Only has a symbol character map

        private int[] _rangeStarts = Array.Empty<int>();
        private int[] _rangeEnds = Array.Empty<int>();

        public bool HasCharacter(int character)
        {
            var low = 0;
            var high = _rangeStarts.Length - 1;
            while (low <= high)
            {
                var middle = (low + high) / 2;
                if (character < _rangeStarts[middle])
                    high = middle - 1;
                else if (character > _rangeEnds[middle])
                    low = middle + 1;
                else
                    return true;
            }

            return false;
        }

        public static List<FontFile> Read(string path)  // One per face, empty if unreadable
        {
            var faces = new List<FontFile>();
            try
            {
                using var stream = File.OpenRead(path);
                var reader = new Reader(stream);

                // Read each face of a collection
                if (reader.ReadTag(0) == "ttcf")
                {
                    var faceCount = reader.ReadUInt32(8);
                    for (var i = 0; i < faceCount; i++)
                        AddFace(reader, (int)reader.ReadUInt32(12 + i * 4), faces);
                }
                else
                {
                    AddFace(reader, 0, faces);
                }
            }
            catch (Exception)
            {
                // Skip fonts that can't be read
            }

            return faces;
        }

        private static void AddFace(Reader reader, int offset, List<FontFile> faces)
        {
            var tables = new Dictionary<string, (int offset, int length)>();
            var tableCount = reader.ReadUInt16(offset + 4);
            for (var i = 0; i < tableCount; i++)
            {
                var record = offset + 12 + i * 16;
                tables[reader.ReadTag(record)] = ((int)reader.ReadUInt32(record + 8), (int)reader.ReadUInt32(record + 12));
            }

            if (!tables.TryGetValue("name", out var names) || !tables.TryGetValue("cmap", out var characterMap))
                return;

            var face = new FontFile
            {
                Family = ReadName(reader.ReadBytes(names.offset, names.length), 1),
                Style = ReadName(reader.ReadBytes(names.offset, names.length), 2),
                IsColor = tables.ContainsKey("COLR") || tables.ContainsKey("CBDT") || tables.ContainsKey("sbix") || tables.ContainsKey("SVG "),
            };

            if (string.IsNullOrEmpty(face.Family))
                return;

            face.ReadCharacterMap(reader.ReadBytes(characterMap.offset, characterMap.length));
            faces.Add(face);
        }

        private static string ReadName(byte[] table, int nameId)  // Null if missing
        {
            var count = Reader.ReadUInt16(table, 2);
            var stringsStart = Reader.ReadUInt16(table, 4);
            string fallback = null;
            for (var i = 0; i < count; i++)
            {
                var record = 6 + i * 12;
                if (Reader.ReadUInt16(table, record + 6) != nameId)
                    continue;

                var platform = Reader.ReadUInt16(table, record);
                var language = Reader.ReadUInt16(table, record + 4);
                var length = Reader.ReadUInt16(table, record + 8);
                var start = stringsStart + Reader.ReadUInt16(table, record + 10);
                if (start + length > table.Length)
                    continue;

                // Prefer the Windows English name
                if (platform == 3)
                {
                    var name = Encoding.BigEndianUnicode.GetString(table, start, length);
                    if (language == 0x409)
                        return name;

                    fallback ??= name;
                }
                else if (platform == 1)
                {
                    fallback ??= Encoding.ASCII.GetString(table, start, length);
                }
            }

            return fallback;
        }

        private void ReadCharacterMap(byte[] table)
        {
            var count = Reader.ReadUInt16(table, 2);
            var (format4, format12, symbol) = (-1, -1, -1);
            for (var i = 0; i < count; i++)
            {
                var record = 4 + i * 8;
                var platform = Reader.ReadUInt16(table, record);
                var encoding = Reader.ReadUInt16(table, record + 2);
                var start = (int)Reader.ReadUInt32(table, record + 4);
                if (start + 2 > table.Length)
                    continue;

                var format = Reader.ReadUInt16(table, start);
                if (format == 12 && (platform == 0 || (platform == 3 && encoding == 10)))
                    format12 = start;
                else if (format == 4 && (platform == 0 || (platform == 3 && encoding == 1)))
                    format4 = start;
                else if (format == 4 && platform == 3 && encoding == 0)
                    symbol = start;
            }

            var ranges = new List<(int start, int end)>();
            if (format12 >= 0)
                ReadFormat12(table, format12, ranges);
            else if (format4 >= 0)
                ReadFormat4(table, format4, ranges);
            else
                IsSymbol = symbol >= 0;

            ranges.Sort((a, b) => a.start.CompareTo(b.start));
            _rangeStarts = ranges.ConvertAll(range => range.start).ToArray();
            _rangeEnds = ranges.ConvertAll(range => range.end).ToArray();
            return;

            static void ReadFormat12(byte[] table, int start, List<(int start, int end)> ranges)
            {
                var groupCount = Reader.ReadUInt32(table, start + 12);
                for (var i = 0; i < groupCount; i++)
                {
                    var group = start + 16 + i * 12;
                    ranges.Add(((int)Reader.ReadUInt32(table, group), (int)Reader.ReadUInt32(table, group + 4)));
                }
            }

            static void ReadFormat4(byte[] table, int start, List<(int start, int end)> ranges)
            {
                var segmentCount = Reader.ReadUInt16(table, start + 6) / 2;
                var ends = start + 14;
                var starts = ends + segmentCount * 2 + 2;
                var deltas = starts + segmentCount * 2;
                var rangeOffsets = deltas + segmentCount * 2;
                for (var i = 0; i < segmentCount; i++)
                {
                    int first = Reader.ReadUInt16(table, starts + i * 2);
                    int last = Reader.ReadUInt16(table, ends + i * 2);
                    var delta = (short)Reader.ReadUInt16(table, deltas + i * 2);
                    var rangeOffsetAt = rangeOffsets + i * 2;
                    var rangeOffset = Reader.ReadUInt16(table, rangeOffsetAt);
                    if (first == 0xFFFF)
                        continue;

                    // Keep only characters that map to a glyph
                    var runStart = -1;
                    for (var character = first; character <= last; character++)
                    {
                        int glyph;
                        if (rangeOffset == 0)
                        {
                            glyph = (character + delta) & 0xFFFF;
                        }
                        else
                        {
                            var glyphAt = rangeOffsetAt + rangeOffset + (character - first) * 2;
                            glyph = glyphAt + 2 > table.Length ? 0 : Reader.ReadUInt16(table, glyphAt);
                            if (glyph != 0)
                                glyph = (glyph + delta) & 0xFFFF;
                        }

                        if (glyph != 0 && runStart < 0)
                            runStart = character;

                        if (glyph == 0 && runStart >= 0)
                        {
                            ranges.Add((runStart, character - 1));
                            runStart = -1;
                        }
                    }

                    if (runStart >= 0)
                        ranges.Add((runStart, last));
                }
            }
        }

        private class Reader
        {
            private readonly Stream _stream;
            private readonly byte[] _buffer = new byte[16];

            public Reader(Stream stream) => _stream = stream;

            public byte[] ReadBytes(int offset, int length)
            {
                if (offset < 0 || length < 0 || offset + (long)length > _stream.Length)
                    throw new InvalidDataException();

                var bytes = new byte[length];
                _stream.Position = offset;
                var read = 0;
                while (read < length)
                {
                    var count = _stream.Read(bytes, read, length - read);
                    if (count == 0)
                        throw new EndOfStreamException();

                    read += count;
                }

                return bytes;
            }

            public string ReadTag(int offset) => Encoding.ASCII.GetString(ReadSmall(offset, 4), 0, 4);

            public ushort ReadUInt16(int offset) => ReadUInt16(ReadSmall(offset, 2), 0);

            public uint ReadUInt32(int offset) => ReadUInt32(ReadSmall(offset, 4), 0);

            public static ushort ReadUInt16(byte[] bytes, int offset)
                => offset + 2 > bytes.Length ? (ushort)0 : (ushort)((bytes[offset] << 8) | bytes[offset + 1]);

            public static uint ReadUInt32(byte[] bytes, int offset)
                => offset + 4 > bytes.Length ? 0 : (uint)((bytes[offset] << 24) | (bytes[offset + 1] << 16) | (bytes[offset + 2] << 8) | bytes[offset + 3]);

            private byte[] ReadSmall(int offset, int length)
            {
                if (offset < 0 || offset + (long)length > _stream.Length)
                    throw new InvalidDataException();

                _stream.Position = offset;
                if (_stream.Read(_buffer, 0, length) != length)
                    throw new EndOfStreamException();

                return _buffer;
            }
        }
    }
}
