using System.Text.Json;
using Melvune.Core;

namespace Melvune.Infrastructure;
public sealed partial class NasApiClient
{
    private static bool RequiredReconnectBoolean(JsonElement item, string name)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(name, out var value) ||
            value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new MusicApiException(MusicFailure.InvalidResponse);
        return value.GetBoolean();
    }
    public async Task<NasLibraryReconnectCheck> CheckLibraryReconnectAsync(NasSharedLibrary expected, string path, CancellationToken ct)
    {
        ValidateSettingsSource(expected.Library.SourceInstanceId);
        if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 || path.Contains('\0')) throw new ArgumentException("Invalid NAS path.");
        await RequireAdministratorAsync(ct);
        if (await GetSharedLibraryAsync(expected.Library, ct) != expected) throw new RemoteSettingsConflictException();
        using var data = await RequestAsync(HttpMethod.Post, "shared-library/reconnect/check", new { guid = expected.Library.Id, path }, ct);
        return new(expected, path, RequiredReconnectBoolean(data.RootElement, "matchAccepted"), RequiredReconnectBoolean(data.RootElement, "requiresConfirmation"));
    }
    public async Task<NasLibraryReconnectResult> ReconnectLibraryAsync(NasLibraryReconnectCheck expected, bool confirmMismatch, CancellationToken ct)
    {
        // Repeat the preflight so a changed library/path or changed matching result cannot silently reuse consent.
        var current = await CheckLibraryReconnectAsync(expected.Original, expected.Path, ct);
        if (current != expected) throw new RemoteSettingsConflictException();
        if (current.NeedsConfirmation && !confirmMismatch) throw new MusicApiException(MusicFailure.Rejected);
        using var data = await RequestAsync(HttpMethod.Post, "shared-library/reconnect", new { guid = current.Original.Library.Id, path = current.Path }, ct);
        if (!RequiredReconnectBoolean(data.RootElement, "reconnected")) throw new RemoteSettingsConflictException();
        var confirmed = await GetSharedLibraryAsync(current.Original.Library, ct);
        if (confirmed.Path != current.Path || confirmed.AutoDownloadLyric != current.Original.AutoDownloadLyric ||
            confirmed.MetadataPreference != current.Original.MetadataPreference) throw new RemoteSettingsConflictException();
        return new(confirmed, SettingsBoolean(data.RootElement, "scanStarted"));
    }
}
