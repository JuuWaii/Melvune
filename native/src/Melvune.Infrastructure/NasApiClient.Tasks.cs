using System.Text.Json;
using Melvune.Core;

namespace Melvune.Infrastructure;

public sealed partial class NasApiClient
{
    private static long TaskNumber(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out long number) || number < 0)
            throw new MusicApiException(MusicFailure.InvalidResponse);
        return number;
    }
    public async Task<IReadOnlyList<NasBackgroundTask>> ListBackgroundTasksAsync(CancellationToken ct)
    {
        using var data = await RequestAsync(HttpMethod.Get, "task/list", null, ct);
        var list = data.RootElement;
        if (list.ValueKind == JsonValueKind.Object && list.TryGetProperty("list", out var nested)) list = nested;
        if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > 10000)
            throw new MusicApiException(MusicFailure.InvalidResponse);
        var result = new List<NasBackgroundTask>(); var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) throw new MusicApiException(MusicFailure.InvalidResponse);
            string id = RequiredSettingsString(item, item.TryGetProperty("id", out _) ? "id" : "guid", 256);
            if (string.IsNullOrWhiteSpace(id) || !ids.Add(id) || !item.TryGetProperty("done", out var done) ||
                done.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new MusicApiException(MusicFailure.InvalidResponse);
            result.Add(new(SourceInstanceId, id, RequiredSettingsString(item, "type", 128), RequiredSettingsString(item, "name"),
                TaskNumber(item, "total"), TaskNumber(item, "successCount"), TaskNumber(item, "failCount"), TaskNumber(item, "canceledCount"),
                done.GetBoolean(), SettingsBoolean(item, "cancelling"), SettingsBoolean(item, "canceled"), SettingsBoolean(item, "retryable"), TaskNumber(item, "createdAt"))
            { LibraryId = ReadTaskLibraryId(item) });
        }
        return result;
    }

    private static string? ReadTaskLibraryId(JsonElement item)
    {
        if (!item.TryGetProperty("ext", out var ext) || ext.ValueKind == JsonValueKind.Null) return null;
        if (ext.ValueKind != JsonValueKind.Object) throw new MusicApiException(MusicFailure.InvalidResponse);
        if (!ext.TryGetProperty("libraryGUID", out var id) || id.ValueKind == JsonValueKind.Null) return null;
        if (id.ValueKind != JsonValueKind.String || id.GetString()!.Length > 256) throw new MusicApiException(MusicFailure.InvalidResponse);
        return string.IsNullOrWhiteSpace(id.GetString()) ? null : id.GetString();
    }

    public async Task<IReadOnlyList<NasBackgroundTask>> ChangeBackgroundTaskAsync(NasBackgroundTask expected, NasTaskAction action, CancellationToken ct)
    {
        ValidateSettingsSource(expected.SourceInstanceId); _ = EncodeCollectionId(expected.Id);
        if (!Enum.IsDefined(action)) throw new ArgumentOutOfRangeException(nameof(action));
        await RequireAdministratorAsync(ct);
        var current = (await ListBackgroundTasksAsync(ct)).SingleOrDefault(item => item.Id == expected.Id);
        // Progress may advance between reads; identity and the current action state must still match.
        if (current is null || current.Type != expected.Type || current.CreatedAt != expected.CreatedAt || current.LibraryId != expected.LibraryId ||
            !(action switch { NasTaskAction.Cancel => current.CanCancel, NasTaskAction.Retry => current.CanRetry, NasTaskAction.Delete => current.CanDelete, _ => false }))
            throw new RemoteSettingsConflictException();
        string route = action switch { NasTaskAction.Cancel => "cancel", NasTaskAction.Retry => "retry", _ => "delete" };
        using var response = await RequestAsync(HttpMethod.Post, "task/" + route, new { taskId = current.Id }, ct);
        var confirmed = await ListBackgroundTasksAsync(ct);
        if (action == NasTaskAction.Delete && confirmed.Any(item => item.Id == current.Id)) throw new RemoteSettingsConflictException();
        // Cancel and retry are asynchronous requests. The returned task states are the only completion evidence.
        return confirmed;
    }
}
