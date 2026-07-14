using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Options;
using Microsoft.Extensions.Configuration;

namespace AssetBlock.Infrastructure.Tests.OptionsValidatorTests;

public sealed class PlaceholderAppSettingsOptionsTests
{
    [Fact]
    public void TrackedAppSettingsPlaceholders_ShouldFailRequiredOptions_AndKeepStripeOptional()
    {
        var path = FindTrackedAppSettings();
        var config = new ConfigurationBuilder()
            .AddJsonFile(path, optional: false, reloadOnChange: false)
            .Build();

        var jwt = config.GetSection(JwtOptions.SECTION_NAME).Get<JwtOptions>()!;
        var encryption = config.GetSection(EncryptionOptions.SECTION_NAME).Get<EncryptionOptions>()!;
        var minio = config.GetSection(MinioOptions.SECTION_NAME).Get<MinioOptions>()!;
        var elasticsearch = config.GetSection(ElasticsearchOptions.SECTION_NAME).Get<ElasticsearchOptions>()!;
        var stripe = config.GetSection(StripeOptions.SECTION_NAME).Get<StripeOptions>()!;

        new JwtOptionsValidator().Validate(null, jwt).Failed.Should().BeTrue();
        new EncryptionOptionsValidator().Validate(null, encryption).Failed.Should().BeTrue();
        new MinioOptionsValidator().Validate(null, minio).Failed.Should().BeTrue();
        new ElasticsearchOptionsValidator().Validate(null, elasticsearch).Failed.Should().BeTrue();
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
