namespace AssetBlock.Domain.Core.Enums;

/// <summary>SMTP transport security mode for <see cref="Primitives.AppSettingsOptions.EmailSmtpOptions"/>.</summary>
public enum SmtpSecurityMode
{
    NONE,
    START_TLS,
    SSL_ON_CONNECT
}
