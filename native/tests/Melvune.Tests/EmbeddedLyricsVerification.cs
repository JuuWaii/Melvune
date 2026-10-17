using System.Buffers.Binary;
using System.Text;
using Melvune.Core;
using Melvune.Infrastructure;

static class EmbeddedLyricsVerification
{
    private const string Timed = "[offset:-250]\r[00:01.00]内嵌歌词\r[00:02.00]第二句";
    private static void Check(bool value) { if (!value) throw new Exception("Embedded lyrics assertion failed."); }
    private static byte[] Join(params byte[][] values) => values.SelectMany(v => v).ToArray();
    private static byte[] Utf(string value) => Encoding.UTF8.GetBytes(value);
    private static byte[] Num(int value, bool big = false, int size = 4)
    { var b = new byte[4]; if (big) BinaryPrimitives.WriteInt32BigEndian(b, value); else BinaryPrimitives.WriteInt32LittleEndian(b, value); return big ? b[(4 - size)..] : b[..size]; }
    private static byte[] Long(long value, bool big = false)
    { var b = new byte[8]; if (big) BinaryPrimitives.WriteInt64BigEndian(b, value); else BinaryPrimitives.WriteInt64LittleEndian(b, value); return b; }
    private static byte[] Synch(int n) => [(byte)(n >> 21), (byte)((n >> 14) & 127), (byte)((n >> 7) & 127), (byte)(n & 127)];
    private static byte[] Frame(string id, byte[] value, int version = 3, byte flags = 0) => version == 2
        ? Join(Utf(id), Num(value.Length, true, 3), value)
        : Join(Utf(id), version == 4 ? Synch(value.Length) : Num(value.Length, true), [0, flags], value);
    private static byte[] Id3(byte[] frames, int version = 3, byte flags = 0) => Join("ID3"u8.ToArray(), [(byte)version, 0, flags], Synch(frames.Length), frames);
    private static byte[] Unsynced(string value, byte code = 3)
    {
        Encoding e = code switch { 0 => Encoding.Latin1, 1 => new UnicodeEncoding(false, true), 2 => new UnicodeEncoding(true, false), _ => Encoding.UTF8 };
        return Join([code], "eng"u8.ToArray(), code == 1 ? e.GetPreamble() : [], new byte[code is 1 or 2 ? 2 : 1], code == 1 ? e.GetPreamble() : [], e.GetBytes(value));
    }
    private static byte[] Comments(params string[] values) => Join(Num(0), Num(values.Length), Join(values.Select(v => Join(Num(Utf(v).Length), Utf(v))).ToArray()));
    private static byte[] Flac(byte[] comments) => Join("fLaC"u8.ToArray(), [0, 0, 0, 34], new byte[34], [132], Num(comments.Length, true, 3), comments);
    private static byte[] Atom(string type, byte[] body, bool extended = false) => extended
        ? Join(Num(1, true), Encoding.Latin1.GetBytes(type), Long(body.Length + 16, true), body)
        : Join(Num(body.Length + 8, true), Encoding.Latin1.GetBytes(type), body);
    private static byte[] Mp4(byte[] text, int format = 1) => Join(Atom("ftyp", "M4A \0\0\0\0"u8.ToArray()), Atom("mdat", new byte[1024]),
        Atom("moov", Atom("udta", Atom("meta", Join(new byte[4], Atom("ilst", Atom("©lyr", Atom("data", Join(Num(format, true), new byte[4], text), true))))))));
    private static byte[] Page(byte[] payload, byte[] laces, int sequence, byte flags = 0) => Join("OggS"u8.ToArray(), [0, flags], new byte[8], Num(42), Num(sequence), new byte[4], [(byte)laces.Length], laces, payload);
    private static byte[] Ogg(string codec, bool continued = false)
    {
        byte[] ident = codec == "opus" ? Join("OpusHead"u8.ToArray(), new byte[11]) : Join("\x01vorbis"u8.ToArray(), new byte[23]);
        byte[] comment = Join(codec == "opus" ? "OpusTags"u8.ToArray() : "\x03vorbis"u8.ToArray(), Comments("TITLE=fixture", "LYRICS=" + Timed + (continued ? new string(' ', 300) : "")));
        var first = Page(ident, [(byte)ident.Length], 0, 2);
        if (!continued) return Join(first, Page(comment, [(byte)comment.Length], 1));
        return Join(first, Page(comment[..255], [255], 1), Page(comment[255..], [(byte)(comment.Length - 255)], 2, 1));
    }
    private static byte[] ChunkFile(byte[] tag, bool big) => Join(big ? "FORM"u8.ToArray() : "RIFF"u8.ToArray(), Num(tag.Length + 12 + (tag.Length & 1), big),
        big ? "AIFF"u8.ToArray() : "WAVE"u8.ToArray(), "ID3 "u8.ToArray(), Num(tag.Length, big), tag, (tag.Length & 1) != 0 ? new byte[1] : []);
    private static byte[] Ape() { var value = Utf(Timed); var item = Join(Num(value.Length), Num(0), "LYRICS\0"u8.ToArray(), value);
        return Join(new byte[12], item, "APETAGEX"u8.ToArray(), Num(2000), Num(item.Length + 32), Num(1), new byte[12]); }
    private static byte[] AsfObject(string id, byte[] value) => Join(new Guid(id).ToByteArray(), Long(value.Length + 24), value);
    private static byte[] Asf(bool metadata)
    {
        var name = Encoding.Unicode.GetBytes("WM/Lyrics\0"); var value = Encoding.Unicode.GetBytes(Timed + "\0");
        var record = metadata ? Join(new byte[4], Num(name.Length, size: 2), Num(0, size: 2), Num(value.Length), name, value)
            : Join(Num(name.Length, size: 2), name, Num(0, size: 2), Num(value.Length, size: 2), value);
        var descriptor = AsfObject(metadata ? "44231C94-9498-49D1-A141-1D134E457054" : "D2D0A440-E307-11D2-97F0-00A0C95EA850", Join(Num(1, size: 2), record));
        if (metadata) descriptor = AsfObject("5FBF03B5-A92E-11CF-8EE3-00C00C205365", Join(new byte[18], Num(descriptor.Length), descriptor));
        return Join(new Guid("75B22630-668E-11CF-A6D9-00AA0062CE6C").ToByteArray(), Long(descriptor.Length + 30), Num(1), [1, 2], descriptor);
    }
    private static async Task<Lyrics> Read(byte[] b) { using var input = new MemoryStream(b, false); return await EmbeddedLyricsReader.ReadAsync(input, default); }
    private static void Expected(Lyrics value) => Check(value.IsTimed && value.Lines.Count == 2 && value.Lines[0] == new LyricLine(1.25, "内嵌歌词") && value.Lines[1] == new LyricLine(2.25, "第二句"));
    public static async Task ContainersAsync()
    {
        var tag = Id3(Frame("USLT", Unsynced(Timed)));
        foreach (var sample in new[] { tag, Id3(Frame("USLT", Unsynced(Timed), 4), 4), Id3(Frame("ULT", Unsynced(Timed), 2), 2),
            Flac(Comments("TITLE=fixture", "LYRICS=" + Timed)), Mp4(Utf(Timed)), Mp4(Encoding.BigEndianUnicode.GetBytes(Timed), 2),
            Ogg("vorbis"), Ogg("opus"), Ogg("vorbis", true), Ogg("opus", true), ChunkFile(tag, false), ChunkFile(tag, true), Ape(), Asf(false), Asf(true) }) Expected(await Read(sample));
        var plain = await Read(Id3(Frame("USLT", Unsynced("第一句\n第二句")))); Check(!plain.IsTimed && plain.Lines.Count == 2);
    }
    public static async Task Id3Async()
    {
        foreach (byte code in new byte[] { 1, 2, 3 }) Expected(await Read(Id3(Frame("USLT", Unsynced(Timed, code)))));
        Check((await Read(Id3(Frame("USLT", Unsynced("Latin words", 0))))).Lines.Single().Text == "Latin words");
        var sylt = Join([3], "eng"u8.ToArray(), [2, 1, 0], Utf("第一句"), [0], Num(1500, true), Utf("翻译"), [0], Num(1500, true), Utf("第二句"), [0], Num(2500, true));
        var actual = await Read(Id3(Join(Frame("USLT", Unsynced("纯文本")), Frame("SYLT", sylt))));
        Check(actual.IsTimed && actual.Lines.Count == 2 && actual.Lines[0] == new LyricLine(1.5, "第一句\n翻译") && actual.FindLine(2.5) == 1);
        sylt[4] = 1; Check((await Read(Id3(Frame("SYLT", sylt)))).Lines.Count == 0);
        Expected(await Read(Id3(Frame("TXXX", Join([3], "UNSYNCEDLYRICS\0"u8.ToArray(), Utf(Timed))))));
        foreach (int version in new[] { 3, 4 })
        {
            byte[] extended = version == 3 ? Join(Num(6, true), new byte[6]) : Join(Synch(6), [1, 0]);
            Expected(await Read(Id3(Join(extended, Frame("USLT", Unsynced(Timed), version)), version, 64)));
        }
        // Latin-1 FF E0 needs an inserted zero; frame sizes remain pre-unsynchronisation in v2.3.
        var content = Unsynced("aÿàb", 0); var frame = Frame("USLT", content); var unsync = new List<byte>();
        for (int i = 0; i < frame.Length; i++) { unsync.Add(frame[i]); if (frame[i] == 255) unsync.Add(0); }
        Check((await Read(Id3(unsync.ToArray(), 3, 128))).Lines.Single().Text == "aÿàb");
        Expected(await Read(Id3(Frame("USLT", Join([0], Unsynced(Timed)), 3, 32))));
        Expected(await Read(Id3(Frame("USLT", Join(Synch(Utf(Timed).Length), Unsynced(Timed)), 4, 1), 4)));
    }
    public static async Task InvalidAsync()
    {
        byte[] tag = Id3(Frame("USLT", Unsynced(Timed))), mp4 = Mp4(Utf(Timed)), flac = Flac(Comments("LYRICS=" + Timed));
        foreach (var sample in new[] { tag, mp4, flac, Asf(true), Ogg("opus"), Ape() })
        {
            for (int n = 0; n < Math.Min(sample.Length, 80); n++) Check((await Read(sample[..n])).Lines.Count == 0);
        }
        var badSize = tag.ToArray(); Array.Fill(badSize, (byte)255, 14, 4); Check((await Read(badSize)).Lines.Count == 0);
        Check((await Read(Id3(Frame("USLT", Unsynced(Timed), 3, 128)))).Lines.Count == 0);
        Check((await Read(Id3(Frame("USLT", Unsynced(new string('x', EmbeddedLyricsReader.TextLimit + 1)))))).Lines.Count == 0);
        Check((await Read(Flac(Comments("LYRICS=" + new string('x', EmbeddedLyricsReader.TextLimit + 1))))).Lines.Count == 0);
        Check((await Read(Id3(Frame("USLT", [3, 101, 110, 103, 0, 255])))).Lines.Count == 0);
        var sequence = Ogg("opus", true); // Discontinuous page sequence must not assemble a false lyric packet.
        int last = sequence.AsSpan().LastIndexOf("OggS"u8); sequence[last + 18] = 5; Check((await Read(sequence)).Lines.Count == 0);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        try { using var s = new MemoryStream(tag); await EmbeddedLyricsReader.ReadAsync(s, canceled.Token); throw new Exception("Cancellation ignored"); } catch (OperationCanceledException) { }
    }
    public static async Task SparseAsync()
    {
        // A moov after a 5 GiB mdat must require only metadata reads, without allocating/reading the audio payload.
        long mediaSize = 5L * 1024 * 1024 * 1024, end = 16 + mediaSize;
        var ftyp = Atom("ftyp", "M4A \0\0\0\0"u8.ToArray());
        var mdat = Join(Num(1, true), "mdat"u8.ToArray(), Long(mediaSize, true));
        var moov = Atom("moov", Atom("udta", Atom("meta", Join(new byte[4], Atom("ilst", Atom("©lyr", Atom("data", Join(Num(1, true), new byte[4], Utf(Timed)))))))));
        using var sparse = new SparseStream(end + moov.Length, [(0, ftyp), (16, mdat), (end, moov)]);
        Expected(await EmbeddedLyricsReader.ReadAsync(sparse, default)); Check(sparse.ReadBytes < 4096);
        using var stalled = new StalledStream(); using var cancel = new CancellationTokenSource(50);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try { await EmbeddedLyricsReader.ReadAsync(stalled, cancel.Token); throw new Exception("Cancellation ignored"); } catch (OperationCanceledException) { }
        Check(clock.ElapsedMilliseconds < 1000);
        using var timeout = new StalledStream(); clock.Restart(); Check((await EmbeddedLyricsReader.ReadAsync(timeout, default)).Lines.Count == 0 && clock.ElapsedMilliseconds < 5000);
    }
    public static async Task SidecarsAsync()
    {
        string dir = Path.Combine(Path.GetTempPath(), "melvune-embedded-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        try
        {
            string audio = Path.Combine(dir, "fixture.mp3"), sidecar = Path.ChangeExtension(audio, ".lrc");
            var bytes = Id3(Frame("USLT", Unsynced(Timed))); await File.WriteAllBytesAsync(audio, bytes);
            var source = await LocalMusicSource.ScanAsync(new(Guid.NewGuid(), dir), default); var reference = source.Tracks.Single().Reference;
            Expected(await source.GetLyricsAsync(reference, default));
            await File.WriteAllTextAsync(sidecar, "[00:09]侧车优先"); Check((await source.GetLyricsAsync(reference, default)).Lines.Single() == new LyricLine(9, "侧车优先"));
            foreach (byte[] invalid in new[] { Array.Empty<byte>(), new byte[] { 0, 0, 0 }, new byte[512001] })
            { await File.WriteAllBytesAsync(sidecar, invalid); Expected(await source.GetLyricsAsync(reference, default)); }
            Check(File.ReadAllBytes(audio).SequenceEqual(bytes));
            try { await source.GetLyricsAsync(new(Guid.NewGuid(), reference.TrackId), default); throw new Exception("Foreign source accepted"); } catch (MusicApiException e) { Check(e.Failure == MusicFailure.Rejected); }
        }
        finally { Directory.Delete(dir, true); }
    }
    public static async Task ProducerAsync(string directory)
    {
        var definitions = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(await File.ReadAllTextAsync(Path.Combine(directory, "fixtures.json")));
        var source = await LocalMusicSource.ScanAsync(new(Guid.NewGuid(), Path.GetFullPath(directory)), default);
        Check(source.Tracks.Count == definitions.GetArrayLength());
        foreach (var definition in definitions.EnumerateArray())
        {
            string filename = definition.GetProperty("file").GetString()!;
            var track = source.Tracks.Single(t => Path.GetFileName(source.GetFilePath(t.Reference)) == filename);
            var actual = await source.GetLyricsAsync(track.Reference, default);
            bool timed = definition.GetProperty("kind").GetString() == "timed";
            Check(actual.IsTimed == timed && actual.Lines.Count == (timed ? 3 : 2) && actual.Lines[0].Text == (timed ? "内嵌第一句" : "内嵌纯文本"));
            if (timed) Check(actual.Lines[2].Seconds == 6 && actual.FindLine(6) == 2);
            Check(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(source.GetFilePath(track.Reference)))) == definition.GetProperty("sha256").GetString());
            Console.WriteLine("PASS independent producer " + filename);
        }
    }
    private sealed class SparseStream(long size, (long Offset, byte[] Bytes)[] segments) : Stream
    {
        public long ReadBytes; public override bool CanRead => true; public override bool CanSeek => true; public override bool CanWrite => false;
        public override long Length => size; public override long Position { get; set; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            count = (int)Math.Min(count, size - Position); Array.Clear(buffer, offset, count);
            foreach (var (start, bytes) in segments)
            { long a = Math.Max(Position, start), b = Math.Min(Position + count, start + bytes.Length); if (b > a) Array.Copy(bytes, a - start, buffer, offset + a - Position, b - a); }
            Position += count; ReadBytes += count; return count;
        }
        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => Position + offset, _ => size + offset };
        public override void Flush() { } public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }
    private sealed class StalledStream : Stream
    {
        public override bool CanRead => true; public override bool CanSeek => true; public override bool CanWrite => false;
        public override long Length => 4096; public override long Position { get; set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) { await Task.Delay(Timeout.Infinite, ct); return 0; }
        public override int Read(byte[] b, int o, int c) => throw new NotSupportedException(); public override long Seek(long o, SeekOrigin s) => Position = o;
        public override void Flush() { } public override void SetLength(long v) => throw new NotSupportedException(); public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }
}
