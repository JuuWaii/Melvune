using System.Text.Json;
using Melvune.Core;

namespace Melvune.Infrastructure;
public sealed partial class NasApiClient
{
    private void ValidateSettingsSource(Guid source)
    { if (SourceInstanceId == Guid.Empty || source != SourceInstanceId) throw new MusicApiException(MusicFailure.Rejected); }
    private static string RequiredSettingsString(JsonElement item, string key, int maxLength = 4096)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String || value.GetString()!.Length > maxLength)
            throw new MusicApiException(MusicFailure.InvalidResponse);
        return value.GetString()!;
    }
    private static bool SettingsBoolean(JsonElement item, string key)
    {
        if (!item.TryGetProperty(key, out var value)) return false;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new MusicApiException(MusicFailure.InvalidResponse);
        return value.GetBoolean();
    }
    public async Task<bool> IsAdministratorAsync(CancellationToken ct)
    {
        using var data = await RequestAsync(HttpMethod.Get, "user/me", null, ct);
        return RequiredSettingsString(data.RootElement, "role", 64) == "admin";
    }
    private async Task RequireAdministratorAsync(CancellationToken ct)
    { if (!await IsAdministratorAsync(ct)) throw new MusicApiException(MusicFailure.Rejected); }
    public async Task<NasServerSettings> GetServerSettingsAsync(CancellationToken ct)
    {
        using var data = await RequestAsync(HttpMethod.Get, "settings/server", null, ct);
        return new(SourceInstanceId, RequiredSettingsString(data.RootElement, "name", 4096), RequiredSettingsString(data.RootElement, "lang", 64));
    }
    public async Task<NasServerSettings> RenameServerAsync(NasServerSettings expected, string name, CancellationToken ct)
    {
        ValidateSettingsSource(expected.SourceInstanceId); name = name.Trim();
        if (name.Length is < 1 or > 32) throw new ArgumentException("Invalid server name.");
        await RequireAdministratorAsync(ct);
        if (await GetServerSettingsAsync(ct) != expected) throw new RemoteSettingsConflictException();
        using var response = await RequestAsync(HttpMethod.Post, "settings/server", new { name, lang = expected.Language }, ct);
        var confirmed = await GetServerSettingsAsync(ct);
        if (confirmed.Name != name || confirmed.Language != expected.Language) throw new RemoteSettingsConflictException();
        return confirmed;
    }
    private NasSharedLibrary ParseSharedLibrary(JsonElement item)
    {
        string id = RequiredSettingsString(item, "guid", 256), path = RequiredSettingsString(item, "path");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(path)) throw new MusicApiException(MusicFailure.InvalidResponse);
        string preference;
        if (item.TryGetProperty("metadataPreference", out var configured) && configured.ValueKind == JsonValueKind.String) preference = configured.GetString()!;
        else if (item.TryGetProperty("enableCloudMetadata", out var legacy) && legacy.ValueKind is JsonValueKind.True or JsonValueKind.False)
            preference = legacy.GetBoolean() ? "cloud_preferred" : "local_only";
        else preference = "";
        return new(new(SourceInstanceId, id, ReadOptionalString(item, "name") ?? ""), path, SettingsBoolean(item, "autoDownloadLyric"), preference);
    }
    public async Task<IReadOnlyList<NasSharedLibrary>> ListSharedLibrariesAsync(CancellationToken ct)
    {
        using var data = await RequestAsync(HttpMethod.Get, "shared-library/list", null, ct);
        var list = data.RootElement;
        if (list.ValueKind == JsonValueKind.Object)
        {
            foreach (string key in new[] { "list", "items", "rows", "libraries" })
                if (list.TryGetProperty(key, out var items) && items.ValueKind == JsonValueKind.Array) { list = items; break; }
        }
        if (list.ValueKind != JsonValueKind.Array) throw new MusicApiException(MusicFailure.InvalidResponse);
        var result = list.EnumerateArray().Select(ParseSharedLibrary).ToArray();
        if (result.Select(item => item.Library.Id).Distinct().Count() != result.Length) throw new MusicApiException(MusicFailure.InvalidResponse);
        return result;
    }
    public async Task<NasSharedLibrary> GetSharedLibraryAsync(MusicLibrary reference, CancellationToken ct)
    {
        ValidateSettingsSource(reference.SourceInstanceId);
        using var data = await RequestAsync(HttpMethod.Get, "shared-library/detail?guid=" + EncodeCollectionId(reference.Id), null, ct);
        var result = ParseSharedLibrary(data.RootElement);
        if (result.Library.Id != reference.Id) throw new MusicApiException(MusicFailure.InvalidResponse);
        return result;
    }
    private static void ValidateLibraryOptions(string path, string metadata)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 || path.Contains('\0') || metadata is not ("cloud_preferred" or "local_preferred" or "local_only"))
            throw new ArgumentException("Invalid library options.");
    }
    public async Task<NasSharedLibrary> CreateSharedLibraryAsync(string path, bool autoDownloadLyric, string metadataPreference, CancellationToken ct)
    {
        ValidateLibraryOptions(path, metadataPreference); ValidateSettingsSource(SourceInstanceId); await RequireAdministratorAsync(ct);
        using var data = await RequestAsync(HttpMethod.Post, "shared-library/create", new { path, autoDownloadLyric, metadataPreference }, ct);
        var created = ParseSharedLibrary(data.RootElement);
        var confirmed = await GetSharedLibraryAsync(created.Library, ct);
        if (confirmed.Path != path || confirmed.AutoDownloadLyric != autoDownloadLyric || confirmed.MetadataPreference != metadataPreference) throw new RemoteSettingsConflictException();
        return confirmed;
    }
    public async Task<NasSharedLibrary> UpdateSharedLibraryAsync(NasSharedLibrary expected, bool autoDownloadLyric, string metadataPreference, CancellationToken ct)
    {
        ValidateSettingsSource(expected.Library.SourceInstanceId); ValidateLibraryOptions(expected.Path, metadataPreference);
        await RequireAdministratorAsync(ct);
        if (await GetSharedLibraryAsync(expected.Library, ct) != expected) throw new RemoteSettingsConflictException();
        using var data = await RequestAsync(HttpMethod.Post, "shared-library/edit", new { guid = expected.Library.Id, path = expected.Path, autoDownloadLyric, metadataPreference }, ct);
        var confirmed = await GetSharedLibraryAsync(expected.Library, ct);
        if (confirmed.Path != expected.Path || confirmed.AutoDownloadLyric != autoDownloadLyric || confirmed.MetadataPreference != metadataPreference) throw new RemoteSettingsConflictException();
        return confirmed;
    }
    public async Task DeleteSharedLibraryAsync(NasSharedLibrary expected, CancellationToken ct)
    {
        ValidateSettingsSource(expected.Library.SourceInstanceId); await RequireAdministratorAsync(ct);
        if (await GetSharedLibraryAsync(expected.Library, ct) != expected) throw new RemoteSettingsConflictException();
        using var data = await RequestAsync(HttpMethod.Post, "shared-library/delete", new { guid = expected.Library.Id }, ct);
        if ((await ListSharedLibrariesAsync(ct)).Any(item => item.Library.Id == expected.Library.Id)) throw new RemoteSettingsConflictException();
    }
    public async Task ScanSharedLibraryAsync(MusicLibrary reference, CancellationToken ct)
    {
        ValidateSettingsSource(reference.SourceInstanceId); _ = EncodeCollectionId(reference.Id); await RequireAdministratorAsync(ct);
        using var data = await RequestAsync(HttpMethod.Post, "shared-library/scan", new { guid = reference.Id }, ct);
    }
    public async Task<IReadOnlyList<string>> ListAuthorizedDirectoriesAsync(CancellationToken ct)
    {
        using var data = await RequestAsync(HttpMethod.Get, "app-center/authed-dir/list", null, ct);
        if (data.RootElement.ValueKind != JsonValueKind.Object || !data.RootElement.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array) throw new MusicApiException(MusicFailure.InvalidResponse);
        return list.EnumerateArray().Select(item => RequiredSettingsString(item, "path")).ToArray();
    }
    public async Task<NasDefaultLibraryAccess> GetDefaultLibraryAccessAsync(CancellationToken ct)
    {
        using var data = await RequestAsync(HttpMethod.Get, "settings/user", null, ct);
        if (data.RootElement.ValueKind != JsonValueKind.Object || !data.RootElement.TryGetProperty("defaultSharedLibraryAccess", out var access)) throw new MusicApiException(MusicFailure.InvalidResponse);
        string mode = RequiredSettingsString(access, "mode", 32);
        if (mode is not ("all" or "partial" or "none") || !access.TryGetProperty("sharedLibraries", out var libraries) || libraries.ValueKind != JsonValueKind.Array)
            throw new MusicApiException(MusicFailure.InvalidResponse);
        return new(SourceInstanceId, mode, libraries.EnumerateArray().Select(item => RequiredSettingsString(item, "guid", 256)).Distinct().Order().ToArray());
    }
    private static bool SameDefaultAccess(NasDefaultLibraryAccess first, NasDefaultLibraryAccess second) =>
        first.SourceInstanceId == second.SourceInstanceId && first.Mode == second.Mode && first.LibraryIds.Order().SequenceEqual(second.LibraryIds.Order());
    public async Task<NasDefaultLibraryAccess> UpdateDefaultLibraryAccessAsync(NasDefaultLibraryAccess expected, string mode, IReadOnlyList<MusicLibrary> libraries, CancellationToken ct)
    {
        ValidateSettingsSource(expected.SourceInstanceId);
        if (mode is not ("all" or "partial" or "none") || libraries.Count > 10000) throw new ArgumentException("Invalid default access.");
        foreach (var library in libraries) { ValidateSettingsSource(library.SourceInstanceId); _ = EncodeCollectionId(library.Id); }
        await RequireAdministratorAsync(ct);
        if (!SameDefaultAccess(await GetDefaultLibraryAccessAsync(ct), expected)) throw new RemoteSettingsConflictException();
        string[] guids = mode == "partial" ? libraries.Select(item => item.Id).Distinct().Order().ToArray() : [];
        using var data = await RequestAsync(HttpMethod.Post, "settings/user", new { defaultSharedLibraryAccess = new { mode, guids } }, ct);
        var confirmed = await GetDefaultLibraryAccessAsync(ct);
        if (confirmed.Mode != mode || (mode == "partial" && !guids.SequenceEqual(confirmed.LibraryIds))) throw new RemoteSettingsConflictException();
        return confirmed;
    }
}
