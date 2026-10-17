using System.Text.Json;
using Melvune.Core;
using Melvune.Infrastructure;

internal static class LocalScanConcurrencyVerification
{
    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "melvune-parallel-scan-" + Guid.NewGuid().ToString("N"));
        internal LocalMusicSettings Settings { get; }
        internal Fixture(int count)
        {
            Directory.CreateDirectory(Root); Settings = new(Guid.NewGuid(), Root);
            for (int index = 0; index < count; index++) File.WriteAllText(Path.Combine(Root, $"track-{index:D5}.flac"), "fixture");
        }
        public void Dispose()
        {
            // Delete only regular files directly inside this uniquely owned fixture root.
            string prefix = Path.GetFullPath(Root) + Path.DirectorySeparatorChar;
            foreach (string path in Directory.EnumerateFiles(Root))
            {
                if (!Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException();
                File.Delete(path);
            }
            Directory.Delete(Root);
        }
    }
    private sealed class DirectProgress(Action<int> report) : IProgress<int>
    { public void Report(int value) => report(value); }
    private static void Require(bool value, string name)
    { if (!value) throw new InvalidOperationException("Local parallel scan: " + name); }
    private static bool SameTracks(LocalMusicSource first, LocalMusicSource second) =>
        first.Tracks.Select(track => (track.Reference, track.Title, track.Artist, track.DurationSeconds, track.Album, string.Join(";", track.LibraryIds)))
            .SequenceEqual(second.Tracks.Select(track => (track.Reference, track.Title, track.Artist, track.DurationSeconds, track.Album, string.Join(";", track.LibraryIds))));
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Canceled(Task task)
    {
        try { await task; throw new InvalidOperationException("Canceled scan returned a snapshot"); }
        catch (OperationCanceledException) { }
    }
    internal static async Task RunAsync()
    {
        await BoundedAndOrderedAsync(); Console.WriteLine("PASS local scan bounded metadata workers and deterministic rows/progress");
        await CancellationAsync(); Console.WriteLine("PASS local scan cancellation drains workers and preserves prior snapshot");
        await CacheAsync(); Console.WriteLine("PASS local scan cached metadata reused and changed files re-read");
        await FailureAsync(); Console.WriteLine("PASS local scan reader failures isolate fallback and retry without poisoning valid cache");
        await ChangedDuringReadAsync(); Console.WriteLine("PASS local scan file changed during probe is not incorrectly cached");
        await CapacityAsync(); Console.WriteLine("PASS local scan empty and ten-thousand boundaries reject overflow before probes");
        await IdentityAsync(); Console.WriteLine("PASS local scan identity isolates metadata reuse and source-qualified rows");
    }
    private static async Task BoundedAndOrderedAsync()
    {
        using var fixture = new Fixture(75);
        var fourStarted = Signal(); var release = Signal();
        int active = 0, maximum = 0, reads = 0;
        var gate = new object(); var progress = new List<int>();
        async Task<LocalMusicMetadata> Read(string path, CancellationToken ct)
        {
            lock (gate)
            {
                reads++; maximum = Math.Max(maximum, ++active);
                if (active == LocalMusicSource.MaximumMetadataConcurrency) fourStarted.TrySetResult();
            }
            try
            {
                await release.Task.WaitAsync(ct);
                // Duplicated titles exercise the ID tie-breaker independent of worker order.
                return new("title-" + (int.Parse(Path.GetFileNameWithoutExtension(path)[6..]) % 3), "artist", 30) { Album = "album" };
            }
            finally { lock (gate) active--; }
        }
        var scan = LocalMusicSource.ScanAsync(fixture.Settings, default, Read, new DirectProgress(value => progress.Add(value)));
        bool allWorkersStarted = false;
        try { await fourStarted.Task.WaitAsync(TimeSpan.FromSeconds(15)); allWorkersStarted = true; }
        catch (TimeoutException) { }
        finally { release.TrySetResult(); }
        var source = await scan;
        Require(allWorkersStarted, "four metadata probes actually overlap");
        Require(reads == 75 && active == 0 && maximum == LocalMusicSource.MaximumMetadataConcurrency && maximum == 4, "worker bound");
        var expected = source.Tracks.OrderBy(track => track.Title, StringComparer.OrdinalIgnoreCase).ThenBy(track => track.Id, StringComparer.Ordinal).ToArray();
        Require(source.Tracks.SequenceEqual(expected) && source.Tracks.Count == 75
            && source.Tracks.Select(track => track.Reference).Distinct().Count() == 75, "deterministic sort and unique identity");
        Require(progress.SequenceEqual(new[] { 0, 25, 50, 75, 75 }), "monotonic progress");
        Require(source.Tracks.All(track => track.SourceInstanceId == fixture.Settings.SourceId && track.Album == "album"
            && track.LibraryIds.SequenceEqual(new[] { fixture.Settings.SourceId.ToString("N") })), "metadata and source mapping");
        foreach (var track in source.Tracks)
        { await using var input = await source.OpenAsync(track.Reference, default); Require(input.Length == 7, "playable paths"); }
    }
    private static async Task CancellationAsync()
    {
        using var fixture = new Fixture(12);
        var previous = await LocalMusicSource.ScanAsync(fixture.Settings, default);
        string unchanged = JsonSerializer.Serialize(previous.CreateIndex());
        using var lifetime = new CancellationTokenSource(); var fourStarted = Signal();
        int reads = 0, active = 0;
        async Task<LocalMusicMetadata> Read(string _, CancellationToken ct)
        {
            Interlocked.Increment(ref reads);
            if (Interlocked.Increment(ref active) == 4) fourStarted.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return new("never", "never", 1); }
            finally { Interlocked.Decrement(ref active); }
        }
        var scan = LocalMusicSource.ScanAsync(fixture.Settings, lifetime.Token, Read, previous: previous);
        try { await fourStarted.Task.WaitAsync(TimeSpan.FromSeconds(15)); }
        catch (TimeoutException) { }
        finally { lifetime.Cancel(); }
        await Canceled(scan);
        Require(reads == 4 && active == 0 && JsonSerializer.Serialize(previous.CreateIndex()) == unchanged, "cancel drains workers without later probes/publication");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Canceled(LocalMusicSource.ScanAsync(fixture.Settings, canceled.Token, Read));
        Require(reads == 4, "pre-cancel never reads metadata");
    }
    private static async Task CacheAsync()
    {
        using var fixture = new Fixture(24); int reads = 0;
        Task<LocalMusicMetadata> Read(string path, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Interlocked.Increment(ref reads); return Task.FromResult(new LocalMusicMetadata(Path.GetFileName(path), "artist", 42)); }
        var initial = await LocalMusicSource.ScanAsync(fixture.Settings, default, Read);
        Require(reads == 24, "initial metadata count"); reads = 0;
        var restored = LocalMusicSource.FromIndex(initial.CreateIndex());
        var unchanged = await LocalMusicSource.ScanAsync(fixture.Settings, default, Read, previous: restored);
        Require(reads == 0 && SameTracks(unchanged, initial), "all unchanged cached tags reused");
        await File.AppendAllTextAsync(Path.Combine(fixture.Root, "track-00007.flac"), "changed");
        var updated = await LocalMusicSource.ScanAsync(fixture.Settings, default, Read, previous: unchanged);
        Require(reads == 1 && updated.Tracks.Select(track => track.Reference).SequenceEqual(initial.Tracks.Select(track => track.Reference)), "one changed file keeps IDs");
        reads = 0;
        await LocalMusicSource.ScanAsync(fixture.Settings, default, Read, previous: LocalMusicSource.FromIndex(initial.CreateIndex() with { MetadataRevision = 0 }));
        Require(reads == 24, "legacy metadata revision refreshes tags");
    }
    private static async Task FailureAsync()
    {
        using var fixture = new Fixture(16); int reads = 0;
        Task<LocalMusicMetadata> Read(string path, CancellationToken ct)
        {
            Interlocked.Increment(ref reads);
            int index = int.Parse(Path.GetFileNameWithoutExtension(path)[6..]);
            return index switch
            {
                0 => throw new IOException("unsupported tags"),
                1 => throw new InvalidDataException("invalid tags"),
                2 => throw new OperationCanceledException("reader-only timeout", ct),
                3 => Task.FromResult<LocalMusicMetadata>(null!),
                _ => Task.FromResult(new LocalMusicMetadata("valid " + index, "artist", 60))
            };
        }
        var initial = await LocalMusicSource.ScanAsync(fixture.Settings, default, Read);
        Require(reads == 16 && initial.Tracks.Count == 16 && initial.Tracks.Count(track => track.Title.StartsWith("track-")) == 4, "only failed files use filename fallback");
        Require(initial.CreateIndex().Entries.Count(entry => entry.Size == -1 && entry.Modified == -1) == 4, "failed tags not cached");
        reads = 0; var retry = await LocalMusicSource.ScanAsync(fixture.Settings, default, Read, previous: initial);
        Require(reads == 4 && SameTracks(retry, initial), "only failed tags retried and valid rows retained");
    }
    private static async Task ChangedDuringReadAsync()
    {
        using var fixture = new Fixture(1); int reads = 0;
        async Task<LocalMusicMetadata> Changed(string path, CancellationToken ct)
        { Interlocked.Increment(ref reads); await File.AppendAllTextAsync(path, "changed", ct); return new("new tags", "artist", 1); }
        var initial = await LocalMusicSource.ScanAsync(fixture.Settings, default, Changed);
        Require(reads == 1 && initial.CreateIndex().Entries.Single().Size == -1, "changed fingerprint not cached");
        reads = 0;
        var refreshed = await LocalMusicSource.ScanAsync(fixture.Settings, default, (_, _) =>
        { Interlocked.Increment(ref reads); return Task.FromResult(new LocalMusicMetadata("stable tags", "artist", 1)); }, previous: initial);
        Require(reads == 1 && refreshed.CreateIndex().Entries.Single().Size > 0 && refreshed.Tracks.Single().Title == "stable tags", "unstable file read again");
    }
    private static async Task CapacityAsync()
    {
        using var fixture = new Fixture(10000); int reads = 0;
        Task<LocalMusicMetadata> Read(string path, CancellationToken ct)
        { Interlocked.Increment(ref reads); return Task.FromResult(new LocalMusicMetadata(Path.GetFileName(path), "artist", 1)); }
        var source = await LocalMusicSource.ScanAsync(fixture.Settings, default, Read);
        Require(source.Tracks.Count == 10000 && reads == 10000, "exact 10000 accepted");
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "overflow.wav"), "fixture"); reads = 0;
        bool rejected = false;
        try { await LocalMusicSource.ScanAsync(fixture.Settings, default, Read, previous: source); }
        catch (InvalidDataException) { rejected = true; }
        Require(rejected && reads == 0 && source.Tracks.Count == 10000, "overflow rejects before metadata work");
        using var empty = new Fixture(0); var progress = new List<int>();
        Require((await LocalMusicSource.ScanAsync(empty.Settings, default, Read, new DirectProgress(value => progress.Add(value)))).Tracks.Count == 0
            && reads == 0 && progress.SequenceEqual(new[] { 0, 0 }), "empty library");
    }
    private static async Task IdentityAsync()
    {
        using var fixture = new Fixture(8); int reads = 0;
        Task<LocalMusicMetadata> Read(string path, CancellationToken ct)
        { Interlocked.Increment(ref reads); return Task.FromResult(new LocalMusicMetadata(Path.GetFileName(path), "artist", 1)); }
        var source = await LocalMusicSource.ScanAsync(fixture.Settings, default, Read); reads = 0;
        Guid foreign = Guid.NewGuid();
        var other = await LocalMusicSource.ScanAsync(fixture.Settings with { SourceId = foreign }, default, Read, previous: source);
        Require(reads == 8 && other.Tracks.All(track => track.SourceInstanceId == foreign)
            && source.Tracks.All(track => track.SourceInstanceId == fixture.Settings.SourceId), "foreign source cannot reuse snapshot cache");
        try { other.GetFilePath(source.Tracks[0].Reference); throw new InvalidOperationException("Foreign source reference accepted"); }
        catch (MusicApiException) { }
    }
}
