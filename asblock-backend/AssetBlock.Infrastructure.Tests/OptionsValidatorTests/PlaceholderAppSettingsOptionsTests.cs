using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Options;
using Microsoft.Extensions.Configuration;

namespace AssetBlock.Infrastructure.Tests.OptionsValidatorTests;

public sealed class PlaceholderAppSettingsOptionsTests
{
    [Fact]
    public void TrackedAppSettingsPlaceholders_ShouldFailActiveStorageAndKeepInactiveOptional()
    {
        var path = FindTrackedAppSettings();
        IConfigurationRoot config = new ConfigurationBuilder()
            .AddJsonFile(path, optional: false, reloadOnChange: false)
            .Build();

        JwtOptions jwt = config.GetSection(JwtOptions.SECTION_NAME).Get<JwtOptions>()!;
        EncryptionOptions encryption = config.GetSection(EncryptionOptions.SECTION_NAME).Get<EncryptionOptions>()!;
        StorageOptions storage = config.GetSection(StorageOptions.SECTION_NAME).Get<StorageOptions>()!;
        SeaweedFsOptions seaweed = config.GetSection(SeaweedFsOptions.SECTION_NAME).Get<SeaweedFsOptions>()!;
        MinioOptions minio = config.GetSection(MinioOptions.SECTION_NAME).Get<MinioOptions>()!;
        StripeOptions stripe = config.GetSection(StripeOptions.SECTION_NAME).Get<StripeOptions>()!;

        new JwtOptionsValidator().Validate(null, jwt).Failed.Should().BeTrue();
        new EncryptionOptionsValidator().Validate(null, encryption).Failed.Should().BeTrue();
        new StorageOptionsValidator().Validate(null, storage).Succeeded.Should().BeTrue();
        storage.Provider.Should().Be("SeaweedFs");

        new SeaweedFsOptionsValidator(config).Validate(null, seaweed).Failed.Should().BeTrue(
            "Active SeaweedFs placeholders must fail validation.");
        new MinioOptionsValidator(config).Validate(null, minio).Succeeded.Should().BeTrue(
            "Inactive Minio placeholders must not break startup.");

        config.GetSection("Elasticsearch").Exists().Should().BeFalse();
        new StripeOptionsValidator().Validate(null, stripe).Succeeded.Should().BeTrue(
            "Stripe placeholders must be treated as unset so Stripe stays optional.");
    }

    private static string FindTrackedAppSettings()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "AssetBlock.WebApi", "appsettings.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException("Could not locate AssetBlock.WebApi/appsettings.json for placeholder binding test.");
    }
}
