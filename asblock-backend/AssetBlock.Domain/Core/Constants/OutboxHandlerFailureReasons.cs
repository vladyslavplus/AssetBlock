namespace AssetBlock.Domain.Core.Constants;

/// <summary>Bounded, non-sensitive outbox handler failure reasons persisted on retry/dead-letter.</summary>
public static class OutboxHandlerFailureReasons
{
    public const string NO_HANDLER = "NO_HANDLER";
    private const string HANDLER_EXCEPTION = "HANDLER_EXCEPTION";
    private const string MAX_ATTEMPTS_EXCEEDED = "MAX_ATTEMPTS_EXCEEDED";

    public static string HandlerException(Type exceptionType) =>
        $"{HANDLER_EXCEPTION}:{exceptionType.Name}";

    public static string MaxAttemptsExceeded(Type exceptionType) =>
        $"{MAX_ATTEMPTS_EXCEEDED}:{exceptionType.Name}";
}
