using System.Text.Json;
using System.Text.Json.Serialization;

namespace FnMusic.Infrastructure;

/// <summary>Versioned, declarative JSON mapping; member paths never execute code.</summary>
public sealed record HttpJsonDefinition
{
    public int Version { get; init; } = 1;
    public string ListPath { get; init; } = "api/tracks";
    public string ItemsPath { get; init; } = "data.items";
    public string TotalPath { get; init; } = "data.total";
    public string PageParameter { get; init; } = "page";
    public string PageSizeParameter { get; init; } = "size";
    public int PageSize { get; init; } = 100;
    public int FirstPage { get; init; } = 1;
    public string IdPath { get; init; } = "id";
    public string TitlePath { get; init; } = "title";
    public string ArtistPath { get; init; } = "artist";
    public string AlbumPath { get; init; } = "album";
    public string DurationPath { get; init; } = "duration";
    public string DurationUnit { get; init; } = "seconds";
    public string PlayUrlPath { get; init; } = "url";
    public string CoverUrlPath { get; init; } = "cover";
    public string LyricsUrlPath { get; init; } = "lyrics";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, WriteIndented = true, MaxDepth = 8
    };

    public static HttpJsonDefinition Parse(string json)
    {
        if (json is null || json.Length > 16384) throw Invalid();
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw Invalid();
            HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
            foreach (var field in doc.RootElement.EnumerateObject()) if (!names.Add(field.Name)) throw Invalid();
            return Normalize(JsonSerializer.Deserialize<HttpJsonDefinition>(json, JsonOptions) ?? throw Invalid());
        }
        catch (JsonException) { throw Invalid(); }
    }

    public string ToJson() => JsonSerializer.Serialize(Normalize(this), JsonOptions);

    public static HttpJsonDefinition Normalize(HttpJsonDefinition value)
    {
        if (value is null || value.Version != 1 || value.PageSize is < 1 or > 1000 || value.FirstPage is < 0 or > 1000000)
            throw Invalid();
        string list = value.ListPath?.Trim() ?? "";
        // Relative deployment paths only. Catalog query keys are added by the transport.
        if (list.Length is < 1 or > 1024 || list.StartsWith('/') || list.Contains('\\') ||
            list.Any(char.IsControl) || list.Contains('?') || list.Contains('#') || list.Contains(':') || list.Contains('%') ||
            list.Split('/').Any(part => part.Length == 0 || part is "." or "..")) throw Invalid();
        string unit = value.DurationUnit?.Trim().ToLowerInvariant() ?? "";
        if (unit is not ("seconds" or "milliseconds")) throw Invalid();
        var normalized = value with
        {
            ListPath = list, ItemsPath = Member(value.ItemsPath, true), TotalPath = Member(value.TotalPath, true),
            PageParameter = Parameter(value.PageParameter), PageSizeParameter = Parameter(value.PageSizeParameter),
            IdPath = Member(value.IdPath, false), TitlePath = Member(value.TitlePath, true),
            ArtistPath = Member(value.ArtistPath, true), AlbumPath = Member(value.AlbumPath, true),
            DurationPath = Member(value.DurationPath, true), DurationUnit = unit, PlayUrlPath = Member(value.PlayUrlPath, false),
            CoverUrlPath = Member(value.CoverUrlPath, true), LyricsUrlPath = Member(value.LyricsUrlPath, true)
        };
        if (normalized.PageParameter == normalized.PageSizeParameter) throw Invalid();
        return normalized;
    }

    private static string Member(string? path, bool optional)
    {
        string text = path?.Trim() ?? "";
        if (optional && text.Length == 0) return "";
        if (text.Length is < 1 or > 256 || text.Split('.').Any(part => !Identifier(part))) throw Invalid();
        return text;
    }
    private static string Parameter(string? text)
    {
        string value = text?.Trim() ?? "";
        if (value.Length > 32 || !Identifier(value)) throw Invalid();
        return value;
    }
    private static bool Identifier(string value) => value.Length > 0 &&
        (char.IsAsciiLetter(value[0]) || value[0] == '_') && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
    private static ArgumentException Invalid() => new("Invalid HTTP JSON definition");
}
