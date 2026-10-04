#if DEBUG
using System.Text.Json;
using FnMusic.App.ViewModels;
using FnMusic.Infrastructure;

namespace FnMusic.App.Services;

// Read-only probe. Never writes media contents, identifiers, addresses or credentials to the report.
internal static class PresentationVerification
{
    public static async Task RunAsync()
    {
        var checks = new Dictionary<string, bool>();
        var skipped = new List<string>();
        var dimensions = new Dictionary<string, int[]>();
        string stage = "session", error = "";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var connection = new ConnectionViewModel();
        try
        {
            await connection.InitializeAsync();
            var api = connection.AuthenticatedClient ?? throw new InvalidOperationException();
            checks[stage] = true;
            stage = "tracks";
            var page = await api.ListTracksAsync(1, 20, timeout.Token);
            checks[stage] = true;
            var track = page.Tracks.FirstOrDefault();
            if (track is not null)
            {
                stage = "lyrics_contract";
                var lyrics = await api.GetLyricsAsync(track.Reference, timeout.Token);
                checks[stage] = true;
                if (lyrics.Lines.Count == 0) skipped.Add("no_lyrics_in_sample");
            }
            else skipped.Add("empty_library");
            var cover = page.Tracks.FirstOrDefault(t => !string.IsNullOrEmpty(t.CoverId));
            if (cover is not null)
            {
                stage = "cover_contract";
                var bytes = await api.GetCoverAsync(cover.CoverId!, timeout.Token);
                checks[stage] = bytes is { Length: > 0 };
                if (bytes is { Length: > 0 })
                {
                    stage = "thumbnail_decode";
                    var thumbnail = await ArtworkDecoder.DecodeAsync(bytes, ArtworkDecoder.ThumbnailEdge, timeout.Token);
                    dimensions[stage] = [thumbnail.PixelWidth, thumbnail.PixelHeight];
                    checks[stage] = thumbnail.PixelWidth > 0 && thumbnail.PixelHeight > 0
                        && Math.Max(thumbnail.PixelWidth, thumbnail.PixelHeight) <= ArtworkDecoder.ThumbnailEdge;
                    stage = "large_cover_contract";
                    var fullBytes = await api.GetCoverAsync(cover.CoverId!, NasCoverSize.Large, timeout.Token);
                    checks[stage] = fullBytes is { Length: > 0 };
                    if (fullBytes is { Length: > 0 })
                    {
                        stage = "large_cover_decode";
                        var full = await ArtworkDecoder.DecodeAsync(fullBytes, ArtworkDecoder.FullCoverEdge, timeout.Token);
                        dimensions[stage] = [full.PixelWidth, full.PixelHeight];
                        checks[stage] = full.PixelWidth > 0 && full.PixelHeight > 0
                            && Math.Max(full.PixelWidth, full.PixelHeight) <= ArtworkDecoder.FullCoverEdge
                            && Math.Max(full.PixelWidth, full.PixelHeight) >= Math.Max(thumbnail.PixelWidth, thumbnail.PixelHeight);
                    }
                }
            }
            else skipped.Add("no_cover_in_sample");
            stage = checks.Values.All(v => v) ? skipped.Count == 0 ? "complete" : "complete_with_skips" : "contract_mismatch";
        }
        catch (Exception ex) { error = ex.GetType().Name; }
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "presentation-verification.json"),
            JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UtcNow, stage, error, checks, skipped, dimensions }));
    }
}
#endif
