using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Domain.Core.Primitives.AppSettingsOptions;

public sealed class EmailOptions
{
    public const string SECTION_NAME = "Email";

    /// <summary>Canonical provider name for this scope is <c>Smtp</c>.</summary>
    public string Provider { get; set; } = string.Empty;

    public string FromName { get; set; } = string.Empty;
    public string FromAddress { get; set; } = string.Empty;

    /// <summary>Public SPA origin used to build fixed library / seller links in templates.</summary>
    public string PublicAppBaseUrl { get; set; } = string.Empty;

    /// <summary>Domain portion of deterministic RFC Message-Id values (e.g. mail.localhost).</summary>
    public string MessageIdDomain { get; set; } = string.Empty;

    public EmailSmtpOptions Smtp { get; set; } = new();
}

public sealed class EmailSmtpOptions
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public SmtpSecurityMode Security { get; set; } = SmtpSecurityMode.NONE;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public int TimeoutSeconds { get; set; } = 30;
}
