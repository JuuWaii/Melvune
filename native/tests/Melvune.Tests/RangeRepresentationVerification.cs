using System.Net;
using System.Net.Http.Headers;
using Melvune.Core;
using Melvune.Infrastructure;

internal static class RangeRepresentationVerification
{
    private static readonly byte[] Song = Enumerable.Range(0, 600001).Select(index => (byte)(index % 251)).ToArray();
    private static void Require(bool condition)
    { if (!condition) throw new InvalidOperationException("Range representation invariant failed."); }

    public static async Task StrongVersionAsync()
    {
        foreach (string value in new[] { "\"generation-A\"", "\"\"" })
        {
            var tag = new EntityTagHeaderValue(value);
            int calls = 0;
            using var stream = await HttpRangeStream.OpenAsync(new HttpClient(new Handler(request =>
            {
                Require(request.Headers.IfMatch.Count == (calls++ == 0 ? 0 : 1));
                if (calls > 1) Require(request.Headers.IfMatch.Single().Tag == tag.Tag && !request.Headers.IfMatch.Single().IsWeak);
                return Response(request, tag);
            })), new Uri("https://range.invalid/audio"), default);
            byte[] actual = new byte[16];
            Require(await stream.ReadAsync(actual) == 16 && actual.SequenceEqual(Song[..16]));
            stream.Seek(4096, SeekOrigin.Begin);
            Require(await stream.ReadAsync(actual) == 16 && actual.SequenceEqual(Song[4096..4112]) && calls == 2);
            stream.Seek(300000, SeekOrigin.Begin);
            Require(await stream.ReadAsync(actual) == 16 && actual.SequenceEqual(Song[300000..300016]) && calls == 3);
        }
    }

    public static async Task ChangedVersionAsync()
    {
        var original = new EntityTagHeaderValue("\"generation-A\"");
        foreach (string scenario in new[] { "changed", "weak", "missing", "full", "precondition" })
        {
            int calls = 0;
            using var stream = await HttpRangeStream.OpenAsync(new HttpClient(new Handler(request =>
            {
                int call = ++calls;
                if (call != 3) return Response(request, original);
                var response = Response(request, scenario switch
                {
                    "changed" => new EntityTagHeaderValue("\"generation-a\""),
                    "weak" => new EntityTagHeaderValue(original.Tag, true),
                    "missing" => null,
                    _ => original
                });
                if (scenario == "full") response.StatusCode = HttpStatusCode.OK;
                if (scenario == "precondition") response.StatusCode = HttpStatusCode.PreconditionFailed;
                return response;
            })), new Uri("https://range.invalid/audio"), default);
            Require(await stream.ReadAsync(new byte[16]) == 16);
            stream.Seek(300000, SeekOrigin.Begin);
            byte[] destination = Enumerable.Repeat((byte)0xcc, 16).ToArray();
            bool rejected = false;
            try { _ = await stream.ReadAsync(destination); }
            catch (MusicApiException exception) when (exception.Failure == MusicFailure.InvalidResponse) { rejected = true; }
            Require(rejected && stream.Position == 300000 && destination.All(value => value == 0xcc));
            // A refused block is not cached: a retry must request and validate it.
            Require(await stream.ReadAsync(destination) == 16 && destination.SequenceEqual(Song[300000..300016]) && calls == 4);
        }
    }

    public static async Task UnavailableVersionAsync()
    {
        foreach (bool weak in new[] { false, true })
        {
            int calls = 0;
            using var stream = await HttpRangeStream.OpenAsync(new HttpClient(new Handler(request =>
            {
                calls++;
                Require(request.Headers.IfMatch.Count == 0 && request.Headers.IfRange is null);
                var response = Response(request, weak ? new EntityTagHeaderValue("\"generation\"", true) : null);
                response.Content.Headers.LastModified = DateTimeOffset.UtcNow.AddHours(-2);
                return response;
            })), new Uri("https://range.invalid/audio"), default);
            Require(await stream.ReadAsync(new byte[16]) == 16);
            stream.Seek(300000, SeekOrigin.Begin);
            Require(await stream.ReadAsync(new byte[16]) == 16 && calls == 3);
        }
    }

    private static HttpResponseMessage Response(HttpRequestMessage request, EntityTagHeaderValue? tag)
    {
        var range = request.Headers.Range!.Ranges.Single();
        int from = checked((int)range.From!.Value), to = checked((int)range.To!.Value);
        var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(Song[from..(to + 1)]) };
        response.Headers.ETag = tag;
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, Song.Length);
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("audio/flac");
        return response;
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(response(request));
    }
}
