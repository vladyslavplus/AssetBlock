namespace AssetBlock.Infrastructure.Outbox;

internal static class OutboxHandlerFailureReason
{
    internal static string FromException(Exception exception) =>
        Domain.Core.Constants.OutboxHandlerFailureReasons.HandlerException(exception.GetType());

    internal static string MaxAttempts(Exception exception) =>
        Domain.Core.Constants.OutboxHandlerFailureReasons.MaxAttemptsExceeded(exception.GetType());
}
