namespace FnMusic.Core;

public enum NasTaskAction { Cancel, Retry, Delete }

public sealed record NasBackgroundTask(Guid SourceInstanceId, string Id, string Type, string Name,
    long Total, long SuccessCount, long FailCount, long CanceledCount,
    bool Done, bool Cancelling, bool Canceled, bool Retryable, long CreatedAt)
{
    public bool CanCancel => !Done && !Cancelling;
    public bool CanRetry => Done && !Cancelling && Retryable;
    public bool CanDelete => Done && !Cancelling;
}
