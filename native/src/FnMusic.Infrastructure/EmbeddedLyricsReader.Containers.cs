using System.Buffers.Binary;
using System.Text;

namespace FnMusic.Infrastructure;
public static partial class EmbeddedLyricsReader
{
    private sealed partial class Reader
    {
        private async Task Flac()
        {
            long position = 4;
            while (position <= input.Length - 4)
            {
                Next(); var h = await At(position, 4); int size = (h[1] << 16) | (h[2] << 8) | h[3];
                position += 4; if (size > input.Length - position) return;
                if ((h[0] & 127) == 4) Comments(await At(position, size));
                if ((h[0] & 128) != 0 || best.IsTimed) return; position += size;
            }
        }
        private void Comments(byte[] b)
        {
            if (b.Length < 8) return;
            int vendor = Le32(b, 0); if (vendor > b.Length - 8) return;
            int cursor = 4 + vendor, count = Le32(b, cursor); cursor += 4;
            if (count > 10000) return;
            for (int i = 0; i < count; i++)
            {
                Next(); if (cursor > b.Length - 4) return;
                int size = Le32(b, cursor); cursor += 4; if (size > b.Length - cursor) return;
                // Unknown fields (including large cover data) are skipped without decoding.
                int search = Math.Min(size, 64), equals = Array.IndexOf(b, (byte)'=', cursor, search);
                if (equals >= cursor && IsLyricKey(Encoding.ASCII.GetString(b, cursor, equals - cursor))) Text(b, equals + 1, size - (equals - cursor) - 1);
                cursor += size;
            }
        }
        private async Task Ogg()
        {
            long position = 0; uint? serial = null, sequence = null; int packetNumber = 0; string codec = "";
            using var packet = new MemoryStream();
            for (int pages = 0; pages < 1024 && position <= input.Length - 27; pages++)
            {
                Next(); var h = await At(position, 27); if (!Is(h, 0, "OggS"u8) || h[4] != 0) return;
                uint pageSerial = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(14, 4)), pageSequence = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(18, 4));
                serial ??= pageSerial;
                var laces = await At(position + 27, h[26]); long payload = position + 27 + laces.Length;
                int size = laces.Sum(x => (int)x); if (size > input.Length - payload) return;
                position = payload + size;
                if (pageSerial != serial) continue;
                if (sequence is { } previous && pageSequence != previous + 1 || ((h[5] & 1) != 0) != (packet.Length > 0)) return;
                sequence = pageSequence;
                foreach (byte lace in laces)
                {
                    Next(); if (packet.Length + lace > 16 * 1024 * 1024) return;
                    packet.Write(await At(payload, lace)); payload += lace;
                    if (lace == 255) continue;
                    var value = packet.ToArray(); packet.SetLength(0);
                    if (packetNumber++ == 0)
                    { codec = Is(value, 0, "\x01vorbis"u8) ? "vorbis" : Is(value, 0, "OpusHead"u8) ? "opus" : ""; if (codec.Length == 0) return; }
                    else
                    {
                        if (codec == "vorbis" && Is(value, 0, "\x03vorbis"u8)) Comments(value[7..]);
                        else if (codec == "opus" && Is(value, 0, "OpusTags"u8)) Comments(value[8..]);
                        return;
                    }
                }
            }
        }
        private async Task Atoms(long start, long end, int depth, bool lyric)
        {
            if (depth > 8) return;
            for (long pos = start; pos <= end - 8;)
            {
                Next(); var h = await At(pos, 8); long size = BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(0, 4)); int header = 8;
                string type = Encoding.Latin1.GetString(h, 4, 4);
                if (size == 1)
                { if (pos > end - 16) return; var extended = await At(pos + 8, 8); size = checked((long)BinaryPrimitives.ReadUInt64BigEndian(extended)); header = 16; }
                if (size == 0) size = end - pos;
                if (size < header || size > end - pos) return;
                long content = pos + header, bound = pos + size;
                if (type is "moov" or "udta" or "ilst" or "©lyr") await Atoms(content, bound, depth + 1, lyric || type == "©lyr");
                else if (type == "meta" && size >= header + 4) await Atoms(content + 4, bound, depth + 1, lyric);
                else if (lyric && type == "data" && size >= header + 8 && size <= TextLimit + header + 8)
                {
                    var data = await At(content, checked((int)(size - header)));
                    uint format = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(0, 4)) & 0xFFFFFF;
                    if (format == 1) Text(data, 8);
                    else if (format == 2) Text(data, 8, null, new UnicodeEncoding(true, false, true));
                }
                if (best.IsTimed) return; pos = bound;
            }
        }
        private async Task Chunks(bool bigEndian)
        {
            var h = await At(0, 12); long bound = 8L + (bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(4, 4)) : BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(4, 4)));
            if (bound > input.Length) return;
            for (long pos = 12; pos <= bound - 8;)
            {
                Next(); var chunk = await At(pos, 8); long size = bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(chunk.AsSpan(4, 4)) : BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan(4, 4)); pos += 8;
                if (size > bound - pos) return;
                if (Is(chunk, 0, "id3 "u8) || Is(chunk, 0, "ID3 "u8)) { if (size >= 10) await Id3(pos, pos + size); }
                if (best.IsTimed) return; pos += size + (size & 1);
            }
        }
        private async Task Ape()
        {
            long end = input.Length;
            if (end >= 128 && Is(await At(end - 128, 3), 0, "TAG"u8)) end -= 128;
            if (end < 32) return;
            var footer = await At(end - 32, 32); if (!Is(footer, 0, "APETAGEX"u8)) return;
            int version = Le32(footer, 8), size = Le32(footer, 12), count = Le32(footer, 16);
            if (version is not (1000 or 2000) || size < 32 || size > 16 * 1024 * 1024 || size > end || count > 10000) return;
            long pos = end - size, bound = end - 32;
            for (int i = 0; i < count; i++)
            {
                Next(); if (pos > bound - 9) return;
                var h = await At(pos, 8); int length = Le32(h, 0); uint flags = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(4, 4)); pos += 8;
                var keyBytes = await At(pos, (int)Math.Min(256, bound - pos)); int stop = Array.IndexOf(keyBytes, (byte)0);
                if (stop < 0) return;
                string key = Encoding.ASCII.GetString(keyBytes, 0, stop); pos += stop + 1;
                if (length > bound - pos) return;
                if (((flags >> 1) & 3) == 0 && IsLyricKey(key) && length <= TextLimit) Text(await At(pos, length));
                if (best.IsTimed) return; pos += length;
            }
        }
    }
}
