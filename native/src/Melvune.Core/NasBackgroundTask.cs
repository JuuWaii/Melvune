namespace Melvune.Core;

public enum NasTaskAction { Cancel, Retry, Delete }

public sealed record NasBackgroundTask(Guid SourceInstanceId, string Id, string Type, string Name,
    long Total, long SuccessCount, long FailCount, long CanceledCount,
    bool Done, bool Cancelling, bool Canceled, bool Retryable, long CreatedAt)
{
    // Optional server association, used to distinguish a library's scan from
    // unrelated scans/scrapes. Task names are display text, not identities.
    public string? LibraryId { get; init; }
    public bool CanCancel => !Done && !Cancelling;
    public bool CanRetry => Done && !Cancelling && Retryable;
    public bool CanDelete => Done && !Cancelling;
}
