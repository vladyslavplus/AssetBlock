namespace AssetBlock.Domain.Core.Constants;

/// <summary>Outbox message type discriminators (wire values).</summary>
public static class OutboxMessageTypes
{
    public const string ASSET_BLOB_DELETE = "AssetBlobDelete";
    public const string NOTIFICATION_DISPATCH = "NotificationDispatch";
    public const string EMAIL_DISPATCH = "EmailDispatch";
    public const string EMAIL_ACTION_DISPATCH = "EmailActionDispatch";

    /// <summary>
    /// Durable intent to schedule initial analysis once the pinned policy is available. The
    /// proof and READY promotion commit with this intent; a recovery handler rereads the exact
    /// immutable evidence and policy and idempotently enqueues the single bound run/job.
    /// </summary>
    public const string ANALYSIS_SCHEDULING = "AnalysisScheduling";

    public const int MAX_ATTEMPTS = 10;
    public const int DISPATCH_BATCH_SIZE = 50;
    public const int LEASE_MINUTES = 5;
}
