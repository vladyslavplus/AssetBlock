using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Serilog;
using DpOptions = AssetBlock.Domain.Core.Primitives.AppSettingsOptions.DataProtectionOptions;

namespace AssetBlock.WebApi.Extensions;

/// <summary>Configures persisted Data Protection with explicit at-rest protection modes.</summary>
internal static class DataProtectionExtensions
{
    public const string KEY_RING_MARKER_FILE_NAME = ".assetblock-dataprotection-keys";

    public static IServiceCollection AddAssetBlockDataProtection(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var section = configuration.GetSection(DpOptions.SECTION_NAME);
        var options = section.Get<DpOptions>() ?? new DpOptions();

        if (string.IsNullOrWhiteSpace(options.KeysPath) || options.KeysPath.StartsWith('<'))
        {
            throw new InvalidOperationException(
                "DataProtection:KeysPath must be configured to a writable directory outside source control.");
        }

        var keysDirectory = EnsureDedicatedKeyRingDirectory(options.KeysPath);
        var mode = ResolveProtectionMode(options.ProtectionMode, environment);

        var builder = services.AddDataProtection()
            .SetApplicationName("AssetBlock")
            .PersistKeysToFileSystem(keysDirectory);

        ApplyProtector(builder, mode, options, environment);
        return services;
    }

    internal static string ResolveProtectionMode(string? configuredMode, IHostEnvironment environment)
    {
        var mode = configuredMode?.Trim() ?? string.Empty;
        if (mode.Length > 0)
        {
            return mode;
        }

        if (OperatingSystem.IsWindows())
        {
            return DpOptions.MODE_DPAPI;
        }

        if (environment.IsProduction())
        {
            throw new InvalidOperationException(
                "DataProtection:ProtectionMode must be Certificate or Kms in Production on non-Windows. "
                + "Plaintext PersistKeysToFileSystem is not allowed.");
        }

        return DpOptions.MODE_NONE;
    }

    internal static DirectoryInfo EnsureDedicatedKeyRingDirectory(string keysPath)
    {
        var fullPath = Path.GetFullPath(keysPath);
        var leaf = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (!IsDedicatedKeyRingDirectoryName(leaf))
        {
            throw new InvalidOperationException(
                "DataProtection:KeysPath must be a dedicated key-ring directory whose name is "
                + "'dataprotection-keys' or starts with 'assetblock-dataprotection-keys' "
                + "(refusing to use an arbitrary existing folder).");
        }

        var existed = Directory.Exists(fullPath);
        Directory.CreateDirectory(fullPath);

        var markerPath = Path.Combine(fullPath, KEY_RING_MARKER_FILE_NAME);
        if (!File.Exists(markerPath))
        {
            if (existed && HasUnexpectedKeyRingContent(fullPath))
            {
                throw new InvalidOperationException(
                    "DataProtection:KeysPath already exists and is not an AssetBlock key-ring directory "
                    + $"(missing {KEY_RING_MARKER_FILE_NAME} and contains unexpected files). "
                    + "Choose a new dedicated path.");
            }

            File.WriteAllText(
                markerPath,
                "AssetBlock Data Protection key ring directory. Do not store other data here.\n");
        }

        return new DirectoryInfo(fullPath);
    }

    internal static bool IsDedicatedKeyRingDirectoryName(string leaf) =>
        leaf.Equals("dataprotection-keys", StringComparison.OrdinalIgnoreCase)
        || leaf.StartsWith("assetblock-dataprotection-keys", StringComparison.OrdinalIgnoreCase);

    private static bool HasUnexpectedKeyRingContent(string fullPath)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(fullPath))
        {
            var name = Path.GetFileName(entry);
            if (name.Equals(KEY_RING_MARKER_FILE_NAME, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // ASP.NET Core Data Protection key files look like key-{guid}.xml
            if (name.StartsWith("key-", StringComparison.OrdinalIgnoreCase)
                && name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static void ApplyProtector(
        IDataProtectionBuilder builder,
        string mode,
        DpOptions options,
        IHostEnvironment environment)
    {
        if (mode.Equals(DpOptions.MODE_DPAPI, StringComparison.OrdinalIgnoreCase))
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new InvalidOperationException(
                    "DataProtection:ProtectionMode=Dpapi is only supported on Windows.");
            }

            // Encrypt key material for the current Windows user. Restrict NTFS ACL outside the app
            // (deployment-owned directory); the process does not rewrite ACLs on arbitrary paths.
            builder.ProtectKeysWithDpapi(protectToLocalMachine: false);
            return;
        }

        if (mode.Equals(DpOptions.MODE_CERTIFICATE, StringComparison.OrdinalIgnoreCase))
        {
            var certificate = LoadCertificate(options);
            builder.ProtectKeysWithCertificate(certificate);
            return;
        }

        if (mode.Equals(DpOptions.MODE_KMS, StringComparison.OrdinalIgnoreCase))
        {
            // Provider-specific wiring (Azure Key Vault / AWS KMS) belongs in the deployment host.
            throw new InvalidOperationException(
                "DataProtection:ProtectionMode=Kms requires a deployment-specific KMS protector "
                + $"(KmsKeyId={options.KmsKeyId}). Wire ProtectKeysWithAzureKeyVault or equivalent "
                + "in the host before enabling this mode; plaintext filesystem keys are not allowed.");
        }

        if (mode.Equals(DpOptions.MODE_NONE, StringComparison.OrdinalIgnoreCase))
        {
            if (environment.IsProduction())
            {
                throw new InvalidOperationException(
                    "DataProtection:ProtectionMode=None is not allowed in Production.");
            }

            Log.Warning(
                "Data Protection keys at {KeysPath} use ProtectionMode=None (plaintext at rest). "
                + "Use only for local Development/IntegrationTesting. Production must use Certificate or Kms.",
                options.KeysPath);
            return;
        }

        throw new InvalidOperationException($"Unsupported DataProtection:ProtectionMode '{mode}'.");
    }

    private static X509Certificate2 LoadCertificate(DpOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.CertificatePath)
            && !options.CertificatePath.StartsWith('<'))
        {
            var password = string.IsNullOrEmpty(options.CertificatePassword)
                ? null
                : options.CertificatePassword;
            return X509CertificateLoader.LoadPkcs12FromFile(options.CertificatePath, password);
        }

        if (!string.IsNullOrWhiteSpace(options.CertificateThumbprint)
            && !options.CertificateThumbprint.StartsWith('<'))
        {
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadOnly);
            var found = store.Certificates.Find(
                X509FindType.FindByThumbprint,
                options.CertificateThumbprint.Replace(" ", string.Empty, StringComparison.Ordinal),
                validOnly: false);
            if (found.Count == 0)
            {
                using var machineStore = new X509Store(StoreName.My, StoreLocation.LocalMachine);
                machineStore.Open(OpenFlags.ReadOnly);
                found = machineStore.Certificates.Find(
                    X509FindType.FindByThumbprint,
                    options.CertificateThumbprint.Replace(" ", string.Empty, StringComparison.Ordinal),
                    validOnly: false);
            }

            if (found.Count == 0)
            {
                throw new InvalidOperationException(
                    "DataProtection:CertificateThumbprint did not match a certificate in CurrentUser/LocalMachine My store.");
            }

            return found[0];
        }

        throw new InvalidOperationException(
            "DataProtection Certificate mode requires CertificatePath or CertificateThumbprint from a secret store.");
    }
}
