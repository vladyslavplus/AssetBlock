namespace AssetBlock.Domain.Core.Primitives.AppSettingsOptions;

public sealed class ClamAvOptions
{
    public const string SECTION_NAME = "ClamAv";

    public bool Enabled { get; set; }
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 3310;
    public const int MIN_TIMEOUT_MS = 1;
    public const int MAX_CONNECT_TIMEOUT_MS = 60_000;
    public const int MAX_IO_TIMEOUT_MS = 300_000;
    public const long MAX_STREAM_BYTES = 2L * 1024 * 1024 * 1024;
    public static readonly TimeSpan MinSignatureAge = TimeSpan.FromHours(1);
    public static readonly TimeSpan MaxSignatureAgAge = TimeSpan.FromDays(7);

    public int ConnectTimeoutMs { get; set; } = 5000;
    public int ReadTimeoutMs { get; set; } = 30000;
    public int WriteTimeoutMs { get; set; } = 30000;
    public long MaxStreamBytes { get; set; } = 250L * 1024 * 1024;

    /// <summary>
    /// clamd StreamMaxLength in bytes. 0 disables disconnect inference.
    /// A non-zero value must match the running daemon StreamMaxLength exactly
    /// (compose clamav/clamd.env uses 280 MiB).
    /// </summary>
    public long DaemonMaxStreamBytes { get; set; } = 280L * 1024 * 1024;
    public TimeSpan MaxSignatureAge { get; set; } = TimeSpan.FromHours(72);
    public int MaxResponseBytes { get; set; } = 4096;
}
