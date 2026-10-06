using System.Buffers.Binary;
using System.Text;

namespace FnMusic.Infrastructure;
public static partial class EmbeddedLyricsReader
{
    private sealed partial class Reader
    {
        private static readonly Guid AsfHeader = new("75B22630-668E-11CF-A6D9-00AA0062CE6C"), AsfExtension = new("5FBF03B5-A92E-11CF-8EE3-00C00C205365"),
            AsfContent = new("D2D0A440-E307-11D2-97F0-00A0C95EA850"), AsfMetadata = new("C5F8CBEA-5BAF-4877-8467-AA8C44FA4CCA"),
            AsfLibrary = new("44231C94-9498-49D1-A141-1D134E457054");
        private static readonly UnicodeEncoding Utf16 = new(false, false, true);
        private static int Short(byte[] b, int p) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p, 2));
        private async Task Asf()
        {
            if (input.Length < 30) return; var h = await At(0, 30);
            long bound = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(h.AsSpan(16, 8)));
            if (new Guid(h.AsSpan(0, 16)) != AsfHeader || bound < 30 || bound > input.Length) return;
            await AsfObjects(30, bound, 0);
        }
        private async Task AsfObjects(long start, long end, int depth)
        {
            if (depth > 4) return;
            for (long pos = start; pos <= end - 24;)
            {
                Next(); var h = await At(pos, 24); var id = new Guid(h.AsSpan(0, 16));
                long size = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(h.AsSpan(16, 8)));
                if (size < 24 || size > end - pos) return;
                long payload = pos + 24, bound = pos + size;
                if (id == AsfExtension && size >= 46)
                {
                    var extension = await At(payload, 22); long dataLength = BinaryPrimitives.ReadUInt32LittleEndian(extension.AsSpan(18, 4));
                    if (dataLength <= bound - payload - 22) await AsfObjects(payload + 22, payload + 22 + dataLength, depth + 1);
                }
                else if ((id == AsfContent || id == AsfMetadata || id == AsfLibrary) && size <= 16 * 1024 * 1024)
                    AsfDescriptors(await At(payload, (int)size - 24), id == AsfContent);
                if (best.IsTimed) return; pos = bound;
            }
        }
        private void AsfDescriptors(byte[] b, bool extended)
        {
            if (b.Length < 2) return; int count = Short(b, 0), p = 2;
            if (count > 10000) return;
            for (int i = 0; i < count; i++)
            {
                Next(); int nameLength, type, size; string name;
                if (extended)
                {
                    if (p > b.Length - 2) return; nameLength = Short(b, p); p += 2;
                    if (nameLength > b.Length - p - 4 || (nameLength & 1) != 0) return;
                    name = Utf16.GetString(b, p, nameLength).TrimEnd('\0'); p += nameLength;
                    type = Short(b, p); size = Short(b, p + 2); p += 4;
                }
                else
                {
                    if (p > b.Length - 12) return;
                    nameLength = Short(b, p + 4); type = Short(b, p + 6); size = Le32(b, p + 8); p += 12;
                    if (nameLength > b.Length - p || (nameLength & 1) != 0) return;
                    name = Utf16.GetString(b, p, nameLength).TrimEnd('\0'); p += nameLength;
                }
                if (size > b.Length - p) return;
                if (type == 0 && name.Equals("WM/Lyrics", StringComparison.OrdinalIgnoreCase)) Text(b, p, size, Utf16);
                p += size;
            }
        }
    }
}
