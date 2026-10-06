using System.Buffers.Binary;
using System.Text;
using FnMusic.Core;

namespace FnMusic.Infrastructure;

/// <summary>Read-only, bounded container/tag traversal. Audio payloads and embedded URLs are never opened.</summary>
public static partial class EmbeddedLyricsReader
{
    public const int TextLimit = 512000;
    public static async Task<Lyrics> ReadAsync(Stream input, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!input.CanRead || !input.CanSeek) throw new ArgumentException("A readable seekable stream is required.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        try { return await Task.Run(() => new Reader(input, deadline.Token).ReadAsync(), deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return Lyrics.Empty; }
    }

    private sealed partial class Reader(Stream input, CancellationToken ct)
    {
        private int bytesLeft = 32 * 1024 * 1024, objectsLeft = 20000;
        private Lyrics best = Lyrics.Empty;
        private static readonly UTF8Encoding Utf8 = new(false, true);
        private async Task<byte[]> At(long position, int length)
        {
            ct.ThrowIfCancellationRequested();
            if (position < 0 || length < 0 || length > 16 * 1024 * 1024 || position > input.Length - length || length > bytesLeft)
                throw new InvalidDataException();
            bytesLeft -= length; input.Position = position;
            var data = new byte[length]; await input.ReadExactlyAsync(data, ct).ConfigureAwait(false); return data;
        }
        private void Next()
        { ct.ThrowIfCancellationRequested(); if (--objectsLeft < 0) throw new InvalidDataException(); }
        private static int Be32(byte[] b, int p) => checked((int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(p, 4)));
        private static int Le32(byte[] b, int p) => checked((int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p, 4)));
        private static int Sync(byte[] b, int p)
        {
            if (b.AsSpan(p, 4).ContainsAnyInRange((byte)128, byte.MaxValue)) throw new InvalidDataException();
            return (b[p] << 21) | (b[p + 1] << 14) | (b[p + 2] << 7) | b[p + 3];
        }
        private static bool Is(byte[] b, int p, ReadOnlySpan<byte> signature) => b.Length >= p + signature.Length && b.AsSpan(p, signature.Length).SequenceEqual(signature);
        private void Select(Lyrics value)
        {
            if (value.Lines.Count > 10000) return;
            if (value.Lines.Any(line => !string.IsNullOrWhiteSpace(line.Text)) && (best.Lines.Count == 0 || value.IsTimed && !best.IsTimed)) best = value;
        }
        private void Text(byte[] data, int start = 0, int? count = null, Encoding? encoding = null)
        {
            int size = count ?? data.Length - start;
            if (size <= 0 || size > TextLimit) return;
            try
            {
                string text = (encoding ?? Utf8).GetString(data, start, size).TrimStart('\uFEFF').TrimEnd('\0');
                if (!text.Contains('\0')) Select(Lyrics.Parse(text));
            }
            catch (ArgumentException) { }
        }
        public async Task<Lyrics> ReadAsync()
        {
            try
            {
                if (input.Length < 12) return best;
                var head = await At(0, 12);
                if (Is(head, 0, "ID3"u8)) await Id3(0, input.Length);
                else if (Is(head, 0, "fLaC"u8)) await Flac();
                else if (Is(head, 0, "OggS"u8)) await Ogg();
                else if (Is(head, 4, "ftyp"u8)) await Atoms(0, input.Length, 0, false);
                else if (Is(head, 0, "RIFF"u8) && Is(head, 8, "WAVE"u8)) await Chunks(false);
                else if (Is(head, 0, "FORM"u8) && (Is(head, 8, "AIFF"u8) || Is(head, 8, "AIFC"u8))) await Chunks(true);
                else if (head.AsSpan().SequenceEqual(new Guid("75B22630-668E-11CF-A6D9-00AA0062CE6C").ToByteArray().AsSpan(0, 12))) await Asf();
                if (!best.IsTimed) await Ape();
            }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or OverflowException or IOException or UnauthorizedAccessException or DecoderFallbackException) { }
            ct.ThrowIfCancellationRequested(); return best;
        }
    }
}
