namespace AssetBlock.Domain.Core.Primitives.AppSettingsOptions;

/// <summary>ASP.NET Core Data Protection key-ring location and at-rest protection.</summary>
public sealed class DataProtectionOptions
{
    public const string SECTION_NAME = "DataProtection";

    public const string MODE_DPAPI = "Dpapi";
    public const string MODE_CERTIFICATE = "Certificate";
    public const string MODE_KMS = "Kms";
    /// <summary>Plaintext key ring on disk. Allowed only outside Production.</summary>
    public const string MODE_NONE = "None";

    /// <summary>Dedicated writable directory for the key ring (must survive API restarts).</summary>
    public string KeysPath { get; set; } = string.Empty;

    /// <summary>
    /// At-rest protector: <c>Dpapi</c> (Windows), <c>Certificate</c>, <c>Kms</c>, or <c>None</c> (non-Production only).
    /// Empty resolves to Dpapi on Windows and None in Development/IntegrationTesting; Production on non-Windows requires Certificate or Kms.
    /// </summary>
    public string ProtectionMode { get; set; } = string.Empty;

    /// <summary>PKCS#12/PFX path for Certificate mode (from secret store / mounted secret, not source control).</summary>
    public string CertificatePath { get; set; } = string.Empty;

    /// <summary>Optional PFX password (User Secrets / env / secret store only).</summary>
    public string CertificatePassword { get; set; } = string.Empty;

    /// <summary>Optional Windows cert store thumbprint alternative to CertificatePath.</summary>
    public string CertificateThumbprint { get; set; } = string.Empty;

    /// <summary>Reserved for Kms mode (provider-specific key id / vault URI). Required when ProtectionMode is Kms.</summary>
    public string KmsKeyId { get; set; } = string.Empty;
}
