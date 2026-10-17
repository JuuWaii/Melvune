#if DEBUG
using System.Security.Cryptography;
using Melvune.Core;
using Melvune.Infrastructure;

namespace Melvune.App.Services;
internal static partial class LibraryLifecycleVerification
{
    private static string Name(string path) => path[(path.LastIndexOf('/') + 1)..];
    private static async Task<IReadOnlyList<(MusicTrack Track, string Path)>> WaitForFixtureFilesAsync(
        NasApiClient api, Fixture fixture, string directory, CancellationToken ct)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(TimeSpan.FromSeconds(30));
        while (true)
        {
            try { return await ReadFixtureFilesAsync(api, fixture, directory, bounded.Token); }
            catch (InvalidOperationException) when (!bounded.IsCancellationRequested)
            { await Task.Delay(TimeSpan.FromMilliseconds(500), bounded.Token); }
        }
    }
    private static async Task<IReadOnlyList<(MusicTrack Track, string Path)>> ReadFixtureFilesAsync(
        NasApiClient api, Fixture fixture, string directory, CancellationToken ct)
    {
        var seen = new HashSet<TrackReference>(); var result = new List<(MusicTrack Track, string Path)>();
        // Refuse an incomplete/unstable page walk rather than matching by title.
        for (int page = 1; page <= 101; page++)
        {
            var files = await api.ListTrackFilesForVerificationAsync(page, 100, ct);
            foreach (var file in files)
            {
                Require(seen.Add(file.Track.Reference) && seen.Count <= 10000);
                if (Within(file.Path, directory)) result.Add(file);
            }
            if (files.Count < 100)
            {
                Require(result.Count == fixture.FileCount && result.All(file => !file.Track.IsCue)
                    && result.Select(file => Name(file.Path)).Order(StringComparer.Ordinal)
                        .SequenceEqual(fixture.Files.Select(file => file.Name).Order(StringComparer.Ordinal)));
                return result.OrderBy(file => Name(file.Path), StringComparer.Ordinal).ToArray();
            }
        }
        throw new InvalidOperationException("Indexed file walk exceeded the verification bound.");
    }
    private static async Task VerifyFixtureBytesAsync(NasApiClient api, IReadOnlyList<(MusicTrack Track, string Path)> files,
        Fixture fixture, CancellationToken ct)
    {
        foreach (var file in files)
        {
            var expected = fixture.Files.Single(item => item.Name == Name(file.Path));
            using var stream = await api.OpenTrackStreamAsync(file.Track.Reference, ct);
            string hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
            Require(hash.Equals(expected.Sha256, StringComparison.OrdinalIgnoreCase));
        }
    }
}
#endif
