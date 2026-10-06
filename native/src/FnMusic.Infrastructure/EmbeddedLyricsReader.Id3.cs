using System.Buffers.Binary;
using System.Text;
using FnMusic.Core;

namespace FnMusic.Infrastructure;
public static partial class EmbeddedLyricsReader
{
    private sealed partial class Reader
    {
        private static byte[] Desync(byte[] data)
        {
            var result = new byte[data.Length]; int n = 0;
            for (int i = 0; i < data.Length; i++)
            { result[n++] = data[i]; if (data[i] == 255 && i + 1 < data.Length && data[i + 1] == 0) i++; }
            return result[..n];
        }
        private async Task Id3(long offset, long bound)
        {
            var header = await At(offset, 10);
            int version = header[3], flags = header[5], length = Sync(header, 6);
            if (!Is(header, 0, "ID3"u8) || version is < 2 or > 4 || header[4] == 255 || length > 16 * 1024 * 1024 || offset + 10L + length > bound
                || version == 2 && (flags & 64) != 0) return;
            // v2.3 unsynchronisation applies to the entire tag, before frame-size traversal.
            byte[]? body = version < 4 && (flags & 128) != 0 ? Desync(await At(offset + 10, length)) : null;
            long pos = 0, end = body?.Length ?? length;
            async Task<byte[]> Part(long p, int n) => body is null ? await At(offset + 10 + p, n) : body.AsSpan(checked((int)p), n).ToArray();
            if (version >= 3 && (flags & 64) != 0)
            {
                if (end < 4) return; var ext = await Part(0, 4);
                pos = version == 4 ? Sync(ext, 0) : 4L + Be32(ext, 0);
                if (pos < (version == 4 ? 6 : 10) || pos > end) return;
            }
            int frameHeader = version == 2 ? 6 : 10;
            while (pos <= end - frameHeader)
            {
                Next(); var h = await Part(pos, frameHeader); if (h[0] == 0) break;
                int idLength = version == 2 ? 3 : 4;
                if (h.Take(idLength).Any(c => !(c is >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9'))) return;
                string id = Encoding.ASCII.GetString(h, 0, idLength);
                int size = version == 2 ? (h[3] << 16) | (h[4] << 8) | h[5] : version == 4 ? Sync(h, 4) : Be32(h, 4);
                pos += frameHeader; if (size < 1 || size > end - pos) return;
                if (size <= TextLimit + 1024 && id is "USLT" or "ULT" or "SYLT" or "SLT" or "TXXX" or "TXX")
                {
                    int format = version == 2 ? 0 : h[9];
                    bool unsupported = version == 3 ? (format & 192) != 0 : (format & 12) != 0;
                    if (!unsupported)
                    {
                        var data = await Part(pos, size);
                        if (version == 4 && ((flags & 128) != 0 || (format & 2) != 0)) data = Desync(data);
                        int prefix = version == 3 ? ((format & 32) != 0 ? 1 : 0) : ((format & 64) != 0 ? 1 : 0) + ((format & 1) != 0 ? 4 : 0);
                        if (prefix < data.Length) Id3Frame(id, data[prefix..]);
                    }
                }
                if (best.IsTimed) return; pos += size;
            }
        }
        private static int Terminator(byte[] b, int start, bool wide)
        {
            for (int i = start; i < b.Length - (wide ? 1 : 0); i += wide ? 2 : 1)
                if (b[i] == 0 && (!wide || b[i + 1] == 0)) return i;
            return -1;
        }
        private static Encoding Id3Encoding(byte code, byte[] b, int start)
        {
            return code switch
            {
                0 => Encoding.Latin1,
                1 => start + 1 < b.Length && b[start] == 254 && b[start + 1] == 255 ? new UnicodeEncoding(true, false, true) : new UnicodeEncoding(false, false, true),
                2 => new UnicodeEncoding(true, false, true),
                3 => Utf8,
                _ => throw new InvalidDataException()
            };
        }
        private static string Id3String(byte[] b, int start, int count, Encoding encoding)
        {
            if (count >= 2 && b[start] == 254 && b[start + 1] == 255) encoding = new UnicodeEncoding(true, false, true);
            else if (count >= 2 && b[start] == 255 && b[start + 1] == 254) encoding = new UnicodeEncoding(false, false, true);
            return encoding.GetString(b, start, count).TrimStart('\uFEFF').TrimEnd('\0');
        }
        private void Id3Frame(string id, byte[] b)
        {
            try
            {
                if (b.Length < 2 || b[0] > 3) return;
                bool sync = id is "SYLT" or "SLT", userText = id is "TXXX" or "TXX", wide = b[0] is 1 or 2;
                int start = userText ? 1 : sync ? 6 : 4;
                if (start >= b.Length || sync && (b[4] != 2 || b[5] != 1)) return; // MPEG-frame timestamps need stream timing; only millisecond lyrics are accepted.
                int separator = Terminator(b, start, wide); if (separator < 0) return;
                var encoding = Id3Encoding(b[0], b, start);
                string descriptor = Id3String(b, start, separator - start, encoding);
                if (userText && !IsLyricKey(descriptor)) return;
                int cursor = separator + (wide ? 2 : 1);
                if (!sync)
                {
                    if (b.Length - cursor > TextLimit) return;
                    string text = Id3String(b, cursor, b.Length - cursor, encoding);
                    if (!text.Contains('\0')) Select(Lyrics.Parse(text)); return;
                }
                var lines = new List<LyricLine>();
                while (cursor < b.Length)
                {
                    Next(); int stop = Terminator(b, cursor, wide); if (stop < 0 || stop > b.Length - (wide ? 6 : 5)) return;
                    string text = Id3String(b, cursor, stop - cursor, encoding);
                    cursor = stop + (wide ? 2 : 1);
                    double seconds = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(cursor, 4)) / 1000d; cursor += 4;
                    if (text.Contains('\0') || lines.Count >= 10000 || seconds > 864000) return;
                    lines.Add(new(seconds, text));
                }
                Select(new(lines.OrderBy(l => l.Seconds).GroupBy(l => l.Seconds).Select(g => new LyricLine(g.Key, string.Join("\n", g.Select(l => l.Text)))).ToArray(), true));
            }
            catch (ArgumentException) { }
        }
        private static bool IsLyricKey(string key) => key.Equals("LYRICS", StringComparison.OrdinalIgnoreCase)
            || key.Length == 10 && key.StartsWith("LYRICS-", StringComparison.OrdinalIgnoreCase) && key.AsSpan(7).ToArray().All(c => char.IsAsciiLetter(c))
            || key.Equals("UNSYNCEDLYRICS", StringComparison.OrdinalIgnoreCase) || key.Equals("SYNCEDLYRICS", StringComparison.OrdinalIgnoreCase);
    }
}
